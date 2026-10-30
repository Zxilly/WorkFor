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
            directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var scriptHost = Option(args, "--script-host") ?? Path.Combine(AppContext.BaseDirectory, "WorkFor.ScriptHost.exe");
            if (!File.Exists(scriptHost)) throw new FileNotFoundException("找不到脚本进程，请使用完整构建目录或 --script-host 指定路径。", scriptHost);
            var ui = Option(args, "--ui") ?? Path.Combine(AppContext.BaseDirectory, "WorkFor.UI.exe");
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var once = args.Contains("--once");
            var mutexName = $"Local\\WorkFor.Host-{identity.User!.Value}-{Process.GetCurrentProcess().SessionId}";
            var first = true;
            using var mutex = once ? null : new Mutex(false, mutexName, out first);
            if (!once && !first) { Console.Error.WriteLine("当前会话已在运行 WorkFor.Host。"); return 2; }
            var configurationKey = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..24];
            using var configurationMutex = new Mutex(false, $"Global\\WorkFor.Config-{identity.User!.Value}-{configurationKey}", out var ownsConfiguration);
            if (!ownsConfiguration) { Console.Error.WriteLine("该配置目录已在使用。"); return 2; }
            var nativePath = Option(args, "--native-bridge");
            var launcher = Option(args, "--launcher");
            StartupRegistration? startup = null;
            if (!once && launcher is not null) startup = new(Path.GetFullPath(launcher), ["--background"]);
            else if (!once && string.Equals(Path.GetFileName(Environment.ProcessPath), "WorkFor.Host.exe", StringComparison.OrdinalIgnoreCase))
            {
                var startupArguments = new List<string> { "--data-dir", directory, "--script-host", Path.GetFullPath(scriptHost), "--ui", Path.GetFullPath(ui) };
                if (nativePath is not null) startupArguments.AddRange(["--native-bridge", Path.GetFullPath(nativePath)]);
                startup = new(Path.GetFullPath(Environment.ProcessPath!), startupArguments);
            }
            async Task<EvaluationError?> ValidatePersistedAsync(JsonObject candidate)
            {
                await using var validation = new ScriptProcess(scriptHost);
                return await validation.ValidateAsync(candidate["rule"]!["manifest"]!.AsObject(),
                    candidate["rule"]!["source"]!.GetValue<string>(), candidate["rule"]!["parameters"]!.AsObject(), stop.Token);
            }
            EvaluationError? ValidatePersisted(JsonObject candidate) => ValidatePersistedAsync(candidate).GetAwaiter().GetResult();
            var config = new ConfigurationStore(directory, startup, ValidatePersisted);
            if (!once && !args.Contains("--no-startup-sync"))
            {
                try { config.ReconcileStartup(); }
                catch (ConfigException error) { Console.Error.WriteLine(error.Message); }
            }
            await using var script = new ScriptProcess(scriptHost);
            var initial = config.Snapshot();
            await using var native = new NativeSession(once ? null : nativePath, directory,
                initial["display"]!["taskbarWidthDip"]!.GetValue<int>(), config.Revision);
            var runtime = new HostRuntime(config, script, scriptHost, ui, native, new SnapshotStore(directory));
            if (args.Contains("--once"))
            {
                var time = Option(args, "--at") is { } timestamp
                    ? DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : DateTimeOffset.UtcNow;
                var snapshot = await runtime.EvaluateAsync(time, stop.Token);
                Console.WriteLine(snapshot.ToJsonString());
                return snapshot["status"]?.GetValue<string>() == "fresh" ? 0 : 1;
            }
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
            runtime.PublishPersisted();
            native.Start();
            Console.Error.WriteLine("WorkFor 计算宿主已启动；每秒根据绝对时刻重新计算。Ctrl+C 退出。");
            try { await runtime.RunAsync(stop.Token); }
            finally { await native.DisposeAsync(); await runtime.FlushSnapshotAsync(); }
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

internal sealed class HostRuntime(ConfigurationStore configuration, ScriptProcess script, string scriptHost, string ui, NativeSession native, SnapshotStore snapshots)
{
    private readonly string _generation = Guid.NewGuid().ToString();
    private long _sequence;
    private JsonObject? _lastSuccess = snapshots.LoadStale(DateTimeOffset.UtcNow, configuration.Revision, configuration.Snapshot()["rule"]!["contentHash"]!.GetValue<string>());
    private DateTimeOffset _lastSaved;
    private long _lastSavedRevision;
    private string? _snapshotNotice;
    private EvaluationError? _lastError;
    private long _failureRevision = -1;
    private int _failureCount;
    private DateTimeOffset _nextAttempt;
    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly SemaphoreSlim _evaluate = new(1);
    private readonly SemaphoreSlim _previews = new(2);
    public event Action<JsonObject>? SnapshotPublished;

    public void PublishPersisted()
    {
        if (_lastSuccess is null) return;
        var value = (JsonObject)_lastSuccess.DeepClone();
        value["generation"] = _generation; value["sequence"] = 0;
        SnapshotPublished?.Invoke(value);
    }

    public async Task FlushSnapshotAsync()
    {
        if (_lastSuccess?["status"]?.GetValue<string>() != "fresh") return;
        try { await snapshots.SaveAsync(_lastSuccess, CancellationToken.None); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { _snapshotNotice = "未能保存最近计算结果；下次启动会按绝对时间重新计算。"; }
    }

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
            native.Configure(config["display"]!["taskbarWidthDip"]!.GetValue<int>(), revision);
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
                ["rateLabel"] = result.RatePerHourMinor is null ? "" : config["rule"]!["id"]!.GetValue<string>() == "builtin.monthly" ? "折算时薪" : "当前时薪",
                ["scheduledElapsedText"] = $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分钟 {duration.Seconds} 秒",
                ["note"] = result.Note ?? "", ["metrics"] = JsonSerializer.SerializeToNode(result.Metrics, WorkForJson.Options),
                ["taskbarState"] = native.Snapshot()["state"]?.DeepClone(),
                ["nativeState"] = native.Snapshot()
            };
            _lastSuccess = (JsonObject)snapshot.DeepClone();
            if (_lastSavedRevision != revision || now - _lastSaved >= TimeSpan.FromSeconds(30))
            {
                await FlushSnapshotAsync();
                _lastSaved = now; _lastSavedRevision = revision;
            }
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
        var snapshot = _lastSuccess is null ? new JsonObject { ["amountText"] = "--",
                ["title"] = config["display"]!["period"]!.GetValue<string>() == "month" ? "本月收入" : "今日收入",
                ["currency"] = config["display"]!["currency"]!.DeepClone() }
            : (JsonObject)_lastSuccess.DeepClone();
        if (_lastSuccess is not null)
        {
            snapshot["lastSuccessfulRevision"] = _lastSuccess["configRevision"]?.DeepClone();
            snapshot["lastSuccessfulAtMs"] = _lastSuccess["evaluatedAtMs"]?.DeepClone();
        }
        snapshot["generation"] = _generation;
        snapshot["sequence"] = sequence;
        snapshot["configRevision"] = config["revision"]!.DeepClone();
        snapshot["status"] = "ruleError";
        snapshot["statusText"] = _lastSuccess is null ? "计算失败" : "计算失败 · 显示上次结果";
        snapshot["expiresAtMs"] = now.ToUnixTimeMilliseconds();
        snapshot["error"] = JsonSerializer.SerializeToNode(error, WorkForJson.Options);
        snapshot["taskbarState"] = native.Snapshot()["state"]?.DeepClone();
        snapshot["nativeState"] = native.Snapshot();
        return snapshot;
    }

    public async Task<JsonObject> ControlAsync(string type, JsonObject payload, CancellationToken cancellationToken)
    {
        if (type == "config.get") return configuration.Snapshot();
        if (type == "rule.catalog") return BuiltinRules.Catalog();
        if (type == "native.retry") { native.Retry(); return new() { ["retryRequested"] = true }; }
        if (type == "diagnostics.get") return new()
        {
            ["generation"] = _generation, ["sequence"] = Interlocked.Read(ref _sequence),
            ["configRevision"] = configuration.Revision, ["taskbarState"] = native.Snapshot()["state"]?.DeepClone(),
            ["nativeState"] = native.Snapshot(),
            ["lastError"] = JsonSerializer.SerializeToNode(_lastError, WorkForJson.Options),
            ["configRecoveryNotice"] = configuration.RecoveryNotice,
            ["configRecoveredRevision"] = configuration.RecoveredRevision,
            ["startupNotice"] = configuration.StartupNotice,
            ["snapshotNotice"] = _snapshotNotice ?? snapshots.Notice,
            ["nativeStatus"] = native.Snapshot()["layoutAttached"]?.GetValue<bool>() == true
                ? "原生任务栏区域已附着。" : "原生任务栏区域未附着，请查看 nativeState。"
        };
        if (type == "ui.openDetails") { OpenDetails(payload); return new() { ["opened"] = true }; }
        if (type == "ui.openSettings") { OpenDetails(payload, settings: true); return new() { ["opened"] = true }; }
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
            native.Configure(committed["display"]!["taskbarWidthDip"]!.GetValue<int>(), committed["revision"]!.GetValue<long>());
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

    public void OpenDetails(JsonObject payload) => OpenDetails(payload, settings: false);

    private void OpenDetails(JsonObject payload, bool settings)
    {
        if (!File.Exists(ui)) return;
        GrantExistingUiForeground();
        var info = new ProcessStartInfo(Path.GetFullPath(ui)) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        if (settings) info.ArgumentList.Add("--settings");
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

    private void GrantExistingUiForeground()
    {
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", PipeHub.Name("ui"),
                System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.CurrentUserOnly);
            pipe.Connect(100);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)) return;
            using var process = Process.GetProcessById(checked((int)pid));
            if (process.SessionId == Process.GetCurrentProcess().SessionId &&
                string.Equals(Path.GetFullPath(process.MainModule!.FileName), Path.GetFullPath(ui), StringComparison.OrdinalIgnoreCase))
                AllowSetForegroundWindow(pid);
        }
        catch (Exception error) when (error is IOException or TimeoutException or InvalidOperationException
            or System.ComponentModel.Win32Exception or ArgumentException) { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
