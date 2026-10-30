using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using WorkFor.Contracts;
using WorkFor.Core;

namespace WorkFor.Host;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("WorkFor.Host [--data-dir PATH] [--script-host EXE] [--ui EXE] [--native-bridge EXE] [--once] [--at ISO8601]");
            return 0;
        }
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            var directory = Option(args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkFor");
            var scriptHost = Option(args, "--script-host") ?? Path.Combine(AppContext.BaseDirectory, "WorkFor.ScriptHost.exe");
            if (!File.Exists(scriptHost)) throw new FileNotFoundException("找不到脚本进程，请使用完整构建目录或 --script-host 指定路径。", scriptHost);
            var ui = Option(args, "--ui") ?? Path.Combine(AppContext.BaseDirectory, "WorkFor.UI.exe");
            var config = new ConfigurationStore(directory);
            await using var script = new ScriptProcess(scriptHost);
            await using var native = new NativeSession(args.Contains("--once") ? null : Option(args, "--native-bridge"));
            var runtime = new HostRuntime(config, script, scriptHost, ui, native);
            if (args.Contains("--once"))
            {
                var time = Option(args, "--at") is { } timestamp
                    ? DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : DateTimeOffset.UtcNow;
                var snapshot = await runtime.EvaluateAsync(time, stop.Token);
                Console.WriteLine(snapshot.ToJsonString());
                return snapshot["status"]?.GetValue<string>() == "fresh" ? 0 : 1;
            }
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var mutexName = $"Local\\WorkFor.Host-{identity.User!.Value}-{Process.GetCurrentProcess().SessionId}";
            using var mutex = new Mutex(true, mutexName, out var first);
            if (!first) { Console.Error.WriteLine("当前会话已在运行 WorkFor.Host。"); return 2; }
            async Task<JsonObject> HandleControl(string type, JsonObject payload, CancellationToken token)
            {
                if (type == "host.shutdown")
                {
                    _ = Task.Run(async () => { await Task.Delay(150); stop.Cancel(); });
                    return new() { ["stopping"] = true };
                }
                return await runtime.ControlAsync(type, payload, token);
            }
            await using var hub = new PipeHub(HandleControl, runtime.OpenDetails);
            runtime.SnapshotPublished += hub.Publish;
            hub.Start();
            native.Start();
            Console.Error.WriteLine("WorkFor 计算宿主已启动；每秒根据绝对时刻重新计算。Ctrl+C 退出。");
            try { await runtime.RunAsync(stop.Token); }
            finally { await native.DisposeAsync(); }
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception error)
        {
            Console.Error.WriteLine($"WorkFor 启动失败：{error.Message}");
            return 1;
        }
    }

    private static string? Option(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return null;
        return index + 1 < args.Length ? args[index + 1] : throw new ArgumentException($"Missing value after {option}.");
    }
}

internal sealed class HostRuntime(ConfigurationStore configuration, ScriptProcess script, string scriptHost, string ui, NativeSession native)
{
    private readonly string _generation = Guid.NewGuid().ToString();
    private long _sequence;
    private JsonObject? _lastSuccess;
    private EvaluationError? _lastError;
    private long _failureRevision = -1;
    private int _failureCount;
    private DateTimeOffset _nextAttempt;
    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly SemaphoreSlim _evaluate = new(1);
    private readonly SemaphoreSlim _previews = new(2);
    public event Action<JsonObject>? SnapshotPublished;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var ticker = Task.Run(async () =>
        {
            try { while (await timer.WaitForNextTickAsync(cancellationToken)) _changed.Writer.TryWrite(true); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }, cancellationToken);
        _changed.Writer.TryWrite(true);
        try
        {
            await foreach (var _ in _changed.Reader.ReadAllAsync(cancellationToken))
            {
                var snapshot = await EvaluateAsync(DateTimeOffset.UtcNow, cancellationToken);
                if (snapshot["configRevision"]?.GetValue<long>() == configuration.Revision)
                    SnapshotPublished?.Invoke(snapshot);
                else _changed.Writer.TryWrite(true);
            }
        }
        finally { timer.Dispose(); await ticker; }
    }

    public async Task<JsonObject> EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _evaluate.WaitAsync(cancellationToken);
        try
        {
            var config = configuration.Snapshot();
            var revision = config["revision"]!.GetValue<long>();
            var sequence = Interlocked.Increment(ref _sequence);
            if (_failureRevision != revision)
            {
                _failureRevision = revision; _failureCount = 0; _nextAttempt = DateTimeOffset.MinValue; _lastError = null;
            }
            if (_lastError is not null && now < _nextAttempt)
                return FailureSnapshot(config, now, sequence, _lastError);
            var context = ConfigurationStore.Context(config, now, sequence.ToString(CultureInfo.InvariantCulture));
            var outcome = await script.EvaluateAsync(config["rule"]!["source"]!.GetValue<string>(), context, cancellationToken);
            if (revision != configuration.Revision)
            {
                _changed.Writer.TryWrite(true);
                return FailureSnapshot(config, now, sequence, new("CONFIG_CHANGED", "配置已更新，正在重新计算。"));
            }
            _lastError = outcome.Error;
            if (!outcome.Success)
            {
                RegisterFailure(now);
                return FailureSnapshot(config, now, sequence, outcome.Error ?? new("RESULT_INVALID", "脚本没有返回结果。"));
            }
            _failureCount = 0; _nextAttempt = DateTimeOffset.MinValue;
            var result = outcome.Result!;
            var elapsed = context.ActivePeriod == "day" ? context.Periods.Day.ElapsedScheduledMs : context.Periods.Month.ElapsedScheduledMs;
            var duration = TimeSpan.FromMilliseconds(elapsed);
            var locale = config["display"]!["locale"]!.GetValue<string>();
            var amount = MoneyFormatter.Format(result.AmountMinor, context.Currency, locale);
            var snapshot = new JsonObject
            {
                ["generation"] = _generation, ["sequence"] = sequence,
                ["configRevision"] = revision, ["ruleHash"] = config["rule"]!["contentHash"]!.DeepClone(),
                ["evaluatedAtMs"] = now.ToUnixTimeMilliseconds(), ["expiresAtMs"] = now.AddSeconds(5).ToUnixTimeMilliseconds(),
                ["title"] = context.ActivePeriod == "day" ? "今日收入" : "本月收入",
                ["amountText"] = amount, ["amountMinor"] = result.AmountMinor, ["currency"] = result.Currency,
                ["status"] = "fresh", ["statusText"] = "实时更新", ["accessibleText"] = $"收入 {amount}，实时更新",
                ["rateText"] = result.RatePerHourMinor is { } rate ? MoneyFormatter.Format(rate, context.Currency, locale) + " / 小时" : "",
                ["scheduledElapsedText"] = $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟 {duration.Seconds} 秒",
                ["note"] = result.Note ?? "", ["metrics"] = JsonSerializer.SerializeToNode(result.Metrics, WorkForJson.Options),
                ["taskbarState"] = native.Snapshot()["state"]?.DeepClone(),
                ["nativeState"] = native.Snapshot()
            };
            _lastSuccess = (JsonObject)snapshot.DeepClone();
            return snapshot;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException)
        {
            var config = configuration.Snapshot();
            _lastError = new("CONTEXT_INVALID", "计薪日历或上下文计算失败，请检查设置。");
            RegisterFailure(now);
            return FailureSnapshot(config, now, Interlocked.Increment(ref _sequence), _lastError);
        }
        finally { _evaluate.Release(); }
    }

    private void RegisterFailure(DateTimeOffset now)
    {
        _failureCount = Math.Min(_failureCount + 1, 6);
        _nextAttempt = now.AddSeconds(Math.Min(30, 1 << (_failureCount - 1)));
    }

    private JsonObject FailureSnapshot(JsonObject config, DateTimeOffset now, long sequence, EvaluationError error)
    {
        var snapshot = _lastSuccess is null ? new JsonObject { ["amountText"] = "--", ["title"] = "今日收入" }
            : (JsonObject)_lastSuccess.DeepClone();
        snapshot["generation"] = _generation;
        snapshot["sequence"] = sequence;
        snapshot["configRevision"] = config["revision"]!.DeepClone();
        snapshot["status"] = "ruleError";
        snapshot["statusText"] = "计算失败";
        snapshot["expiresAtMs"] = now.ToUnixTimeMilliseconds();
        snapshot["error"] = JsonSerializer.SerializeToNode(error, WorkForJson.Options);
        snapshot["taskbarState"] = native.Snapshot()["state"]?.DeepClone();
        return snapshot;
    }

    public async Task<JsonObject> ControlAsync(string type, JsonObject payload, CancellationToken cancellationToken)
    {
        if (type == "config.get") return configuration.Snapshot();
        if (type == "diagnostics.get") return new()
        {
            ["generation"] = _generation, ["sequence"] = Interlocked.Read(ref _sequence),
            ["configRevision"] = configuration.Revision, ["taskbarState"] = native.Snapshot()["state"]?.DeepClone(),
            ["nativeState"] = native.Snapshot(),
            ["lastError"] = JsonSerializer.SerializeToNode(_lastError, WorkForJson.Options),
            ["nativeStatus"] = native.Snapshot()["layoutAttached"]?.GetValue<bool>() == true
                ? "原生任务栏区域已附着。" : "原生任务栏区域未附着，请查看 nativeState。"
        };
        if (type == "ui.openDetails") { OpenDetails(payload); return new() { ["opened"] = true }; }
        if (type is not ("rule.validate" or "rule.preview" or "config.activate"))
            throw new ConfigException("UNKNOWN_REQUEST", "不支持的控制消息。");
        await _previews.WaitAsync(cancellationToken);
        try { return await PreviewOrActivateAsync(type, payload, cancellationToken); }
        finally { _previews.Release(); }
    }

    private async Task<JsonObject> PreviewOrActivateAsync(string type, JsonObject payload, CancellationToken cancellationToken)
    {
        var candidate = configuration.Candidate(payload);
        var now = payload["at"] is JsonValue at
            ? DateTimeOffset.Parse(at.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : DateTimeOffset.UtcNow;
        await using var preview = new ScriptProcess(scriptHost);
        var validation = await preview.ValidateAsync(candidate["rule"]!["manifest"]!.AsObject(),
            candidate["rule"]!["source"]!.GetValue<string>(), candidate["rule"]!["parameters"]!.AsObject(), cancellationToken);
        if (validation is not null) throw new ConfigException(validation.Code, validation.Message);
        var context = ConfigurationStore.Context(candidate, now, Interlocked.Increment(ref _sequence).ToString(CultureInfo.InvariantCulture));
        var outcome = await preview.EvaluateAsync(candidate["rule"]!["source"]!.GetValue<string>(), context, cancellationToken);
        if (!outcome.Success)
            throw new ConfigException(outcome.Error?.Code ?? "RESULT_INVALID", outcome.Error?.Message ?? "脚本没有返回结果。", outcome.Error?.Line, outcome.Error?.Column);
        if (type == "config.activate")
        {
            var expected = payload["expectedRevision"]?.GetValue<long>() ?? throw new ConfigException("CONFIG_CONFLICT", "缺少 expectedRevision。");
            var committed = await configuration.ActivateAsync(candidate, expected, cancellationToken);
            _changed.Writer.TryWrite(true);
            return committed;
        }
        return new()
        {
            ["valid"] = true,
            ["result"] = JsonSerializer.SerializeToNode(outcome.Result, WorkForJson.Options),
            ["amountText"] = MoneyFormatter.Format(outcome.Result!.AmountMinor, context.Currency, candidate["display"]!["locale"]!.GetValue<string>()),
            ["evaluatedAtMs"] = now.ToUnixTimeMilliseconds()
        };
    }

    public void OpenDetails(JsonObject payload)
    {
        if (!File.Exists(ui)) return;
        var info = new ProcessStartInfo(Path.GetFullPath(ui)) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        if (payload["anchorRect"] is JsonObject anchor)
        {
            try
            {
                var x = anchor["x"]!.GetValue<int>();
                var y = anchor["y"]!.GetValue<int>();
                var width = anchor["width"]!.GetValue<int>();
                var height = anchor["height"]!.GetValue<int>();
                if (x is >= -100_000 and <= 100_000 && y is >= -100_000 and <= 100_000
                    && width is > 0 and < 10000 && height is > 0 and < 10000)
                {
                    info.ArgumentList.Add("--anchor-rect");
                    info.ArgumentList.Add($"{x},{y},{width},{height}");
                }
            }
            catch (Exception error) when (error is InvalidOperationException or FormatException or NullReferenceException) { }
        }
        var started = Process.Start(info);
        if (started is not null) AllowSetForegroundWindow((uint)started.Id);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
