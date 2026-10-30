using System.Text.Json.Nodes;
using Xunit;

namespace WorkFor.Host.Tests;

public sealed class CompletedFeaturesTests
{
    [Fact]
    public async Task BuiltinMonthlyCatalogPreviewsAndActivatesAsOneRule()
    {
        await using var host = await RunningHost.StartAsync();
        var catalog = await host.RequestAsync("rule.catalog", cancellationToken: TestContext.Current.CancellationToken);
        var rules = catalog["rules"]!.AsArray();
        Assert.Equal(2, rules.Count);
        var monthly = rules.Select(x => x!.AsObject()).Single(x => x["id"]!.GetValue<string>() == "builtin.monthly");
        var candidate = new JsonObject
        {
            ["expectedRevision"] = 1,
            ["source"] = monthly["source"]!.DeepClone(),
            ["manifest"] = monthly["manifest"]!.DeepClone(),
            ["parameters"] = monthly["defaultParameters"]!.DeepClone(),
            ["rule"] = new JsonObject { ["id"] = "builtin.monthly", ["version"] = "1.0.0" },
            ["display"] = new JsonObject { ["period"] = "month" },
            ["at"] = "2026-10-30T11:00:00+08:00"
        };
        var preview = await host.RequestAsync("rule.preview", candidate, TestContext.Current.CancellationToken);
        Assert.Equal("1545454", preview["result"]!["amountMinor"]!.GetValue<string>());
        Assert.Equal(1, (await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken))["revision"]!.GetValue<long>());
        var saved = await host.RequestAsync("config.activate", candidate, TestContext.Current.CancellationToken);
        Assert.Equal("builtin.monthly", saved["rule"]!["id"]!.GetValue<string>());
        await using var display = await host.SubscribeAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var packet = await WorkFor.Contracts.FramedJson.ReadAsync(display, 16384, deadline.Token);
            var snapshot = packet["payload"]!;
            if (snapshot["configRevision"]!.GetValue<long>() != 2 || snapshot["status"]!.GetValue<string>() != "fresh") continue;
            Assert.Equal("本月收入", snapshot["title"]!.GetValue<string>());
            Assert.Equal("折算时薪", snapshot["rateLabel"]!.GetValue<string>());
            break;
        }
    }

    [Fact]
    public async Task DateOverrideIsAppliedToTheAbsoluteTimePreview()
    {
        await using var host = await RunningHost.StartAsync();
        var preview = await host.RequestAsync("rule.preview", new JsonObject
        {
            ["at"] = "2026-10-30T11:00:00+08:00",
            ["calendar"] = new JsonObject { ["dateOverrides"] = new JsonObject { ["2026-10-30"] = new JsonArray() } }
        }, TestContext.Current.CancellationToken);
        Assert.Equal("0", preview["result"]!["amountMinor"]!.GetValue<string>());
        Assert.Equal(1, (await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken))["revision"]!.GetValue<long>());
    }

    [Fact]
    public async Task HostAndIsolatedValidatorAgreeOnBusinessDataAndBooleanSchemas()
    {
        await using var host = await RunningHost.StartAsync();
        var current = await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken);
        var manifest = current["rule"]!["manifest"]!.DeepClone().AsObject();
        manifest["parametersSchema"] = JsonNode.Parse("""
            {"type":"object","required":["$id","$ref"],"additionalProperties":false,
             "properties":{"$id":{"const":"business-record"},"$ref":{"const":"ordinary-data"}}}
            """);
        var candidate = new JsonObject
        {
            ["manifest"] = manifest,
            ["parameters"] = new JsonObject { ["$id"] = "business-record", ["$ref"] = "ordinary-data" },
            ["source"] = "export default ctx => ({apiVersion:1,currency:ctx.currency.code,amountMinor:'123'});"
        };
        Assert.True((await host.RequestAsync("rule.preview", candidate, TestContext.Current.CancellationToken))["valid"]!.GetValue<bool>());
        manifest["parametersSchema"] = true;
        Assert.True((await host.RequestAsync("rule.preview", candidate, TestContext.Current.CancellationToken))["valid"]!.GetValue<bool>());
        manifest["parametersSchema"] = false;
        var rejected = await host.RequestEnvelopeAsync("rule.preview", candidate, TestContext.Current.CancellationToken);
        Assert.Equal("PARAMETER_INVALID", rejected["payload"]!["code"]!.GetValue<string>());
        manifest["parametersSchema"] = new JsonObject { ["$ref"] = "https://example.invalid/never-fetch" };
        rejected = await host.RequestEnvelopeAsync("rule.preview", candidate, TestContext.Current.CancellationToken);
        Assert.Equal("MANIFEST_INVALID", rejected["payload"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task CorruptPrimaryRecoversVerifiedBackupWithoutResettingSalary()
    {
        await using var host = await RunningHost.StartAsync();
        await host.RequestAsync("config.activate", new JsonObject
        { ["expectedRevision"] = 1, ["parameters"] = new JsonObject { ["hourlyRate"] = "32.00" } }, TestContext.Current.CancellationToken);
        await host.RequestAsync("config.activate", new JsonObject
        { ["expectedRevision"] = 2, ["parameters"] = new JsonObject { ["hourlyRate"] = "48.00" } }, TestContext.Current.CancellationToken);
        await host.RestartAsync(directory => File.WriteAllText(Path.Combine(directory, "settings.json"), "{corrupt-primary"));
        var recovered = await host.RequestAsync("config.get", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, recovered["schemaVersion"]!.GetValue<int>());
        Assert.Equal(2, recovered["revision"]!.GetValue<long>());
        Assert.Equal("32.00", recovered["rule"]!["parameters"]!["hourlyRate"]!.GetValue<string>());
        var diagnostics = await host.RequestAsync("diagnostics.get", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics["configRecoveryNotice"]!.GetValue<string>()));
        Assert.Equal(2, diagnostics["configRecoveredRevision"]!.GetValue<long>());
    }
}
