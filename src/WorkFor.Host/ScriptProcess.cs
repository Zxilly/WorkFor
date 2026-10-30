using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using WorkFor.Contracts;
using WorkFor.Core;

namespace WorkFor.Host;

/// <summary>One serialized, bounded process connection. A timeout discards the process, never just its Task.</summary>
internal sealed class ScriptProcess(string executable) : IAsyncDisposable
{
    private readonly SemaphoreSlim _serial = new(1);
    private Process? _process;
    private SafeFileHandle? _job;
    private bool _cold;
    private Task? _stderrDrain;

    public async Task<EvaluationError?> ValidateAsync(JsonObject manifest, string source, JsonObject parameters, CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                Start();
                var request = FramedJson.Message("rule.validate", "0", new JsonObject
                { ["manifest"] = manifest.DeepClone(), ["source"] = source, ["parameters"] = parameters.DeepClone() });
                await FramedJson.WriteAsync(_process!.StandardInput.BaseStream, request, FramedJson.ControlLimit, deadline.Token);
                var message = await FramedJson.ReadAsync(_process.StandardOutput.BaseStream, FramedJson.ControlLimit, deadline.Token);
                _cold = false;
                if (message["protocolVersion"]?.GetValue<int>() != 1 || message["requestId"]?.GetValue<string>() != "0"
                    || message["type"]?.GetValue<string>() != "rule.validation") throw new InvalidDataException("Invalid validation response.");
                return message["payload"]?["valid"]?.GetValue<bool>() == true ? null
                    : message["payload"]?["error"]?.Deserialize<EvaluationError>(WorkForJson.Options)
                        ?? new("MANIFEST_INVALID", "规则清单验证失败。");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { Stop(); return new("SCRIPT_TIMEOUT", "规则清单验证超过进程截止。", Retryable: true); }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { Stop(); return new("SCRIPT_HOST_EXITED", "规则验证进程通信失败。", Retryable: true); }
            catch (OperationCanceledException) { Stop(); throw; }
        }
        finally { _serial.Release(); }
    }

    public async Task<EvaluationOutcome> EvaluateAsync(string source, WorkForContext context, CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                Start();
                deadline.CancelAfter(_cold ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(750));
                var request = FramedJson.Message("evaluation.evaluate", context.EvaluationId,
                    new JsonObject { ["source"] = source, ["context"] = JsonSerializer.SerializeToNode(context, WorkForJson.Options) });
                await FramedJson.WriteAsync(_process!.StandardInput.BaseStream, request, FramedJson.ControlLimit, deadline.Token);
                var message = await FramedJson.ReadAsync(_process.StandardOutput.BaseStream, FramedJson.ControlLimit, deadline.Token);
                _cold = false;
                if (message["protocolVersion"]?.GetValue<int>() != 1 || message["requestId"]?.GetValue<string>() != context.EvaluationId)
                    throw new InvalidDataException("Script process returned an invalid envelope.");
                if (message["type"]?.GetValue<string>() == "evaluation.error")
                    return new(null, message["payload"]!.Deserialize<EvaluationError>(WorkForJson.Options));
                if (message["type"]?.GetValue<string>() != "evaluation.result") throw new InvalidDataException("Unexpected script response.");
                var result = message["payload"]!.Deserialize<RuleResult>(WorkForJson.Options)
                    ?? throw new InvalidDataException("Missing result.");
                if (result.ApiVersion != 1 || result.Currency != context.Currency.Code || !MoneyFormatter.IsCanonical(result.AmountMinor)
                    || result.RatePerHourMinor is { } rate && !MoneyFormatter.IsCanonical(rate))
                    throw new InvalidDataException("Invalid script result.");
                return new(result, null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Stop();
                return EvaluationOutcome.Failed("SCRIPT_TIMEOUT", "脚本超过进程执行截止，已终止并将在下次计算重建。", true);
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Stop();
                return EvaluationOutcome.Failed("SCRIPT_HOST_EXITED", "脚本进程通信失败，等待重建。", true);
            }
            catch (OperationCanceledException) { Stop(); throw; }
        }
        finally { _serial.Release(); }
    }

    private void Start()
    {
        if (_process is { HasExited: false }) return;
        Stop();
        var info = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Do not expose unrelated gateway credentials to the user-script process.
        foreach (var name in info.Environment.Keys.Where(x => x.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
            || x.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || x.Contains("API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(name);
        _process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start script process.");
        try { _job = ProcessJob.Attach(_process); }
        catch { Stop(); throw; }
        var reader = _process.StandardError;
        _stderrDrain = Task.Run(async () =>
        {
            var buffer = new char[1024];
            try { while (await reader.ReadAsync(buffer) != 0) { } }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
        _cold = true;
    }

    private void Stop()
    {
        _job?.Dispose(); _job = null;
        if (_process is not null)
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            _process.Dispose(); _process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _serial.WaitAsync();
        try { Stop(); }
        finally { _serial.Release(); }
        if (_stderrDrain is not null) try { await _stderrDrain; } catch (IOException) { }
        _serial.Dispose();
    }
}

internal static class ProcessJob
{
    public static SafeFileHandle Attach(Process process)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits
        {
            Basic = new BasicLimits { LimitFlags = 0x2000 | 0x100 | 0x8, ActiveProcessLimit = 1 },
            ProcessMemoryLimit = 192 * 1024 * 1024
        };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimits>());
        try
        {
            Marshal.StructureToPtr(limits, memory, false);
            if (!SetInformationJobObject(job, 9, memory, (uint)Marshal.SizeOf<ExtendedLimits>())
                || !AssignProcessToJobObject(job, process.Handle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
        catch { job.Dispose(); throw; }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, IntPtr information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
