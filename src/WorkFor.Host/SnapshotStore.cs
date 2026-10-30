using System.Globalization;
using System.Text.Json.Nodes;
using WorkFor.Contracts;
using WorkFor.Core;

namespace WorkFor.Host;

internal sealed class SnapshotStore(string directory)
{
    private readonly string _file = Path.Combine(Path.GetFullPath(directory), "state", "last-success.json");
    private readonly SemaphoreSlim _write = new(1);
    public string? Notice { get; private set; }

    public async Task SaveAsync(JsonObject snapshot, CancellationToken cancellationToken)
    {
        if (snapshot["status"]?.GetValue<string>() != "fresh") throw new ArgumentException("Only successful fresh snapshots can be persisted.");
        Validate(snapshot);
        var envelope = new JsonObject { ["schemaVersion"] = 1, ["savedAtMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["snapshot"] = snapshot.DeepClone() };
        if (System.Text.Encoding.UTF8.GetByteCount(envelope.ToJsonString()) > 32 * 1024) throw new InvalidDataException("Snapshot exceeds size limit.");
        await _write.WaitAsync(cancellationToken);
        try { await AtomicJsonFile.WriteAsync(_file, envelope, null, cancellationToken); Notice = null; }
        finally { _write.Release(); }
    }

    public JsonObject? LoadStale(DateTimeOffset now, long currentRevision, string currentRuleHash)
    {
        if (!File.Exists(_file)) return null;
        try
        {
            if (new FileInfo(_file).Length > 32 * 1024) throw new InvalidDataException("Snapshot exceeds size limit.");
            var envelope = FramedJson.Parse(File.ReadAllBytes(_file));
            if (envelope["schemaVersion"]?.GetValue<int>() != 1 || envelope["snapshot"] is not JsonObject snapshot)
                throw new InvalidDataException("Unsupported snapshot schema.");
            Validate(snapshot);
            if (snapshot["configRevision"]?.GetValue<long>() != currentRevision || snapshot["ruleHash"]?.GetValue<string>() != currentRuleHash)
            { Notice = "上次结果属于其他配置版本，已忽略。"; return null; }
            snapshot = (JsonObject)snapshot.DeepClone();
            snapshot["status"] = "stale";
            snapshot["statusText"] = "上次结果，等待重新计算";
            snapshot["expiresAtMs"] = now.ToUnixTimeMilliseconds();
            snapshot["isPersisted"] = true;
            snapshot["accessibleText"] = $"上次保存的收入 {snapshot["amountText"]!.GetValue<string>()}，等待重新计算";
            snapshot.Remove("error");
            Notice = "已读取上次成功结果，当前显示为陈旧值，正在重新计算。";
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or NullReferenceException or FormatException or OverflowException)
        { Notice = "上次成功结果文件损坏或不可读取，已忽略；不会用它作为收入累加起点。"; return null; }
    }

    private static void Validate(JsonObject snapshot)
    {
        if (snapshot["status"]?.GetValue<string>() != "fresh" || snapshot["amountMinor"] is not JsonValue amount || !amount.TryGetValue<string>(out var minor) || !MoneyFormatter.IsCanonical(minor))
            throw new InvalidDataException("Snapshot does not contain valid successful money.");
        _ = CurrencyCatalog.Get(snapshot["currency"]!.GetValue<string>());
        var text = snapshot["amountText"]!.GetValue<string>();
        if (string.IsNullOrEmpty(text) || text.Length > 128 || snapshot["title"]?.GetValue<string>() is not { Length: > 0 and <= 40 }) throw new InvalidDataException("Invalid display text.");
        if (Integer(snapshot["configRevision"]) is not > 0 || Integer(snapshot["evaluatedAtMs"]) is not { } timestamp) throw new InvalidDataException("Invalid snapshot identity.");
        _ = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        var hash = snapshot["ruleHash"]!.GetValue<string>();
        if (!hash.StartsWith("sha256:", StringComparison.Ordinal) || hash.Length != 71 || hash[7..].Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid rule hash.");
    }

    private static long? Integer(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var number)) return number;
        return value.TryGetValue<int>(out var small) ? small : null;
    }
}
