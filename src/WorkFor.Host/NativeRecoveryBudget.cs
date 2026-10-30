using System.Text.Json.Nodes;
using WorkFor.Contracts;

namespace WorkFor.Host;

/// <summary>A durable, bounded crash budget. This is recovery policy, not evidence that a DLL caused a crash.</summary>
internal sealed class NativeRecoveryBudget
{
    private readonly string _file;
    private readonly string _fingerprint;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly List<long> _failures = [];
    private bool _quarantined;
    private string? _lastCode;

    public NativeRecoveryBudget(string directory, string fingerprint, TimeProvider? clock = null)
    {
        _file = Path.Combine(Path.GetFullPath(directory), "state", "native-recovery.json");
        _fingerprint = fingerprint;
        _clock = clock ?? TimeProvider.System;
        try
        {
            if (!File.Exists(_file)) return;
            if (new FileInfo(_file).Length > 16 * 1024) throw new InvalidDataException("Recovery file exceeds limit.");
            var saved = FramedJson.Parse(File.ReadAllBytes(_file));
            if (saved["schemaVersion"]?.GetValue<int>() != 1 || saved["fingerprint"] is not JsonValue key
                || !key.TryGetValue<string>(out var savedFingerprint) || string.IsNullOrEmpty(savedFingerprint)
                || saved["quarantined"] is not JsonValue flag || !flag.TryGetValue<bool>(out var quarantine)
                || saved["failuresMs"] is not JsonArray failures || failures.Count > 3)
                throw new InvalidDataException("Recovery file structure is invalid.");
            var timestamps = failures.Select(x => x is JsonValue value && value.TryGetValue<long>(out var timestamp)
                ? timestamp : throw new InvalidDataException("Invalid failure timestamp.")).ToArray();
            var lastCode = saved["lastCode"]?.GetValue<string>();
            if (lastCode?.Length > 80) throw new InvalidDataException("Invalid last error code.");
            if (savedFingerprint != fingerprint) return;
            _quarantined = quarantine;
            _lastCode = lastCode;
            _failures.AddRange(timestamps);
            Prune();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException)
        {
            // Corrupt recovery metadata cannot change salary or execute code.
            // Keep attachment disabled until the user explicitly retries.
            _quarantined = true;
            _lastCode = "RECOVERY_STATE_INVALID";
        }
    }

    public bool IsQuarantined { get { lock (_gate) return _quarantined; } }
    public int FailureCount { get { lock (_gate) { Prune(); return _failures.Count; } } }

    public void RecordFailure(string code)
    {
        lock (_gate)
        {
            Prune();
            _failures.Add(_clock.GetUtcNow().ToUnixTimeMilliseconds());
            if (_failures.Count > 3) _failures.RemoveAt(0);
            _quarantined |= _failures.Count >= 3 || code == "RETRY_BUDGET_EXCEEDED";
            _lastCode = code.Length > 80 ? "NATIVE_FAILURE" : code;
            Save();
        }
    }

    public void Reset()
    {
        lock (_gate) { _failures.Clear(); _quarantined = false; _lastCode = null; Save(); }
    }

    public JsonObject Describe()
    {
        lock (_gate)
        {
            Prune();
            return new()
            {
                ["quarantined"] = _quarantined, ["failureCount"] = _failures.Count,
                ["lastCode"] = _lastCode, ["windowSeconds"] = 600, ["failureLimit"] = 3
            };
        }
    }

    private void Prune()
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        _failures.RemoveAll(x => x < now - 600_000 || x > now + 60_000);
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var saved = new JsonObject
        {
            ["schemaVersion"] = 1, ["fingerprint"] = _fingerprint,
            ["quarantined"] = _quarantined, ["lastCode"] = _lastCode,
            ["failuresMs"] = new JsonArray(_failures.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
        };
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(saved.ToJsonString());
                output.Write(bytes); output.Flush(true);
            }
            File.Move(temporary, _file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
