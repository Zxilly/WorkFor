using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using WorkFor.Host;
using WorkFor.Core;
using Xunit;

namespace WorkFor.Persistence.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WorkFor.Persistence." + Guid.NewGuid().ToString("N"));
    public PersistenceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CorruptPrimaryRestoresValidatedBackupAndPreservesBothWages()
    {
        var store = new ConfigurationStore(_root);
        await store.ActivateAsync(store.Candidate(new() { ["parameters"] = new JsonObject { ["hourlyRate"] = "22.50" } }), 1, TestContext.Current.CancellationToken);
        await store.ActivateAsync(store.Candidate(new() { ["parameters"] = new JsonObject { ["hourlyRate"] = "99.00" } }), 2, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "{broken", TestContext.Current.CancellationToken);
        var recovered = new ConfigurationStore(_root);
        Assert.Equal(2, recovered.Revision);
        Assert.Equal("22.50", recovered.Snapshot()["rule"]!["parameters"]!["hourlyRate"]!.GetValue<string>());
        Assert.NotNull(recovered.RecoveryNotice);
        Assert.Equal(2, recovered.RecoveredRevision);
        Assert.Single(Directory.GetFiles(_root, "settings.corrupt.*.json"));
        Assert.Equal(2, JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root,"settings.backup.json"),TestContext.Current.CancellationToken))!["revision"]!.GetValue<int>());
        Assert.Equal(2, new ConfigurationStore(_root).Revision);
    }

    [Fact]
    public async Task HashMismatchRecoversBackupRatherThanAcceptingChangedSalaryRule()
    {
        var store = new ConfigurationStore(_root);
        await store.ActivateAsync(store.Candidate(new()), 1, TestContext.Current.CancellationToken);
        await store.ActivateAsync(store.Candidate(new()), 2, TestContext.Current.CancellationToken);
        var broken = store.Snapshot(); broken["rule"]!["source"] = "export default()=>0;";
        await WriteConfig(broken);
        Assert.Equal(2, new ConfigurationStore(_root).RecoveredRevision);
    }

    [Fact]
    public async Task BrokenPrimaryAndBackupNeverResetToDefaults()
    {
        await File.WriteAllTextAsync(Path.Combine(_root,"settings.json"),"{",TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root,"settings.backup.json"),"{",TestContext.Current.CancellationToken);
        Assert.Equal("CONFIG_RECOVERY_FAILED",Assert.Throws<ConfigException>(()=>new ConfigurationStore(_root)).Code);
    }

    [Fact]
    public async Task FutureSchemaDoesNotDowngradeToOlderBackup()
    {
        var valid = new ConfigurationStore(_root).Snapshot();
        await File.WriteAllTextAsync(Path.Combine(_root,"settings.backup.json"),valid.ToJsonString(),TestContext.Current.CancellationToken);
        valid["schemaVersion"] = 99; await WriteConfig(valid);
        Assert.Equal("SCHEMA_VERSION_UNSUPPORTED",Assert.Throws<ConfigException>(()=>new ConfigurationStore(_root)).Code);
        Assert.Equal(99,JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root,"settings.json"),TestContext.Current.CancellationToken))!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task LegacyV1MigratesWithoutChangingParametersOrBusinessRevision()
    {
        var legacy = new ConfigurationStore(_root).Snapshot();
        legacy["schemaVersion"]=1; legacy["revision"]=42;
        legacy["rule"]!["parameters"]!["hourlyRate"]="37.25";
        legacy["rule"]!.AsObject().Remove("manifest");
        await WriteConfig(legacy);
        var migrated = new ConfigurationStore(_root);
        Assert.Equal(42,migrated.Revision);
        Assert.Equal(2,migrated.Snapshot()["schemaVersion"]!.GetValue<int>());
        Assert.Equal("37.25",migrated.Snapshot()["rule"]!["parameters"]!["hourlyRate"]!.GetValue<string>());
        Assert.NotNull(migrated.RecoveryNotice);
        Assert.Equal(1,JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root,"settings.backup.json"),TestContext.Current.CancellationToken))!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task PersistedParameterValidationRunsBeforeMigrationAndDoesNotPolluteBackup()
    {
        var valid = new ConfigurationStore(_root).Snapshot();
        valid["rule"]!["parameters"]!["hourlyRate"]="22.50";
        await File.WriteAllTextAsync(Path.Combine(_root,"settings.backup.json"),valid.ToJsonString(),TestContext.Current.CancellationToken);
        var invalid=valid.DeepClone().AsObject();invalid["schemaVersion"]=1;
        invalid["rule"]!["parameters"]!["hourlyRate"]="99.00";
        invalid["rule"]!.AsObject().Remove("manifest");
        await WriteConfig(invalid);
        var checks=0;
        var recovered=new ConfigurationStore(_root,persistedValidator:config=>
        {
            checks++;
            return config["rule"]!["parameters"]!["hourlyRate"]!.GetValue<string>()=="99.00" ? new EvaluationError("PARAMETER_INVALID","Rejected by test schema") : null;
        });
        Assert.Equal(2,checks);
        Assert.Equal("22.50",recovered.Snapshot()["rule"]!["parameters"]!["hourlyRate"]!.GetValue<string>());
        Assert.Equal("22.50",JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root,"settings.backup.json"),TestContext.Current.CancellationToken))!["rule"]!["parameters"]!["hourlyRate"]!.GetValue<string>());
        Assert.NotNull(recovered.RecoveryNotice);
    }

    [Fact]
    public void DefaultsAreAlsoPassedThroughPersistedValidator()
    {
        var count=0;
        _=new ConfigurationStore(_root,persistedValidator:_=>{count++;return null;});
        Assert.Equal(1,count);
    }

    [Fact]
    public async Task UnavailableValidationProcessDoesNotRollBackToOlderSalary()
    {
        var store=new ConfigurationStore(_root);
        await store.ActivateAsync(store.Candidate(new()),1,TestContext.Current.CancellationToken);
        await store.ActivateAsync(store.Candidate(new(){["parameters"]=new JsonObject{["hourlyRate"]="99.00"}}),2,TestContext.Current.CancellationToken);
        var error=Assert.Throws<ConfigException>(()=>new ConfigurationStore(_root,persistedValidator:_=>new EvaluationError("SCRIPT_HOST_EXITED","Unavailable",Retryable:true)));
        Assert.Equal("CONFIG_VALIDATION_UNAVAILABLE",error.Code);
        Assert.Equal("99.00",new ConfigurationStore(_root).Snapshot()["rule"]!["parameters"]!["hourlyRate"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"calendar\":{\"dateOverrides\":{\"2027-01-01\":[{\"start\":\"09:00\",\"end\":\"18:00\",\"unexpected\":true}]}}}")]
    [InlineData("{\"calendar\":{\"dateOverrides\":{\"not-a-date\":[]}}}")]
    [InlineData("{\"calendar\":{\"dateOverrides\":{\"2027-01-01\":null}}}")]
    [InlineData("{\"calendar\":{\"dateOverrides\":[]}}")]
    [InlineData("{\"calendar\":{\"weekdays\":[1,1]}}")]
    [InlineData("{\"startWithWindows\":null}")]
    public void InvalidCalendarAndToggleValuesAreRejected(string patch)
    {
        var store = new ConfigurationStore(_root);
        Assert.Throws<ConfigException>(()=>store.Candidate(JsonNode.Parse(patch)!.AsObject()));
        Assert.Equal(1,store.Revision);
    }

    [Fact]
    public void DateOverrideShiftCountsAndFutureDstInvalidTimesAreValidated()
    {
        var shifts = new JsonArray();
        for(var i=0;i<17;i++)shifts.Add(new JsonObject { ["start"]="09:00",["end"]="10:00" });
        var store = new ConfigurationStore(_root);
        Assert.Throws<ConfigException>(()=>store.Candidate(new(){["calendar"]=new JsonObject{["dateOverrides"]=new JsonObject{["2027-01-01"]=shifts}}}));
        Assert.Throws<ConfigException>(()=>store.Candidate(JsonNode.Parse("""{"calendar":{"timeZone":"America/New_York","dateOverrides":{"2027-03-14":[{"start":"02:30","end":"04:00"}]}}}""")!.AsObject()));
    }

    [Fact]
    public void RuleIdentityAndSchemaReferencesMustMatch()
    {
        var store = new ConfigurationStore(_root);
        Assert.Equal("MANIFEST_INVALID",Assert.Throws<ConfigException>(()=>store.Candidate(new(){["rule"]=new JsonObject{["id"]="custom.other"}})).Code);
        var manifest=store.Snapshot()["rule"]!["manifest"]!.DeepClone().AsObject();
        manifest["parametersSchema"]=new JsonObject{["$ref"]="https://example.com/schema"};
        Assert.Equal("MANIFEST_INVALID",Assert.Throws<ConfigException>(()=>store.Candidate(new(){["manifest"]=manifest})).Code);
    }

    [Fact]
    public async Task StartupToggleWritesExactCommandAndIsIdempotentWithoutTouchingRealRegistry()
    {
        var registry = new FakeRegistry();
        var entry=Path.Combine(_root,"WorkFor.Host.exe");await File.WriteAllTextAsync(entry,"test only",TestContext.Current.CancellationToken);
        var registration=new StartupRegistration(entry,["--data-dir",Path.Combine(_root,"salary data")],registry);
        var store=new ConfigurationStore(Path.Combine(_root,"data"),registration);
        await store.ActivateAsync(store.Candidate(new(){["startWithWindows"]=true}),1,TestContext.Current.CancellationToken);
        Assert.True(registration.IsEnabled);
        Assert.Equal(registration.Command,registry.Value);
        registration.SetEnabled(true);Assert.Equal(1,registry.Writes);
        await store.ActivateAsync(store.Candidate(new(){["startWithWindows"]=false}),2,TestContext.Current.CancellationToken);
        Assert.Null(registry.Value);
    }

    [Fact]
    public async Task StartupWriteFailureDoesNotCommitPreferenceOrRevision()
    {
        var registry=new FakeRegistry{FailWrite=true};
        var entry=Path.Combine(_root,"WorkFor.exe");await File.WriteAllTextAsync(entry,"test only",TestContext.Current.CancellationToken);
        var store=new ConfigurationStore(Path.Combine(_root,"data"),new StartupRegistration(entry,registry:registry));
        var error=await Assert.ThrowsAsync<ConfigException>(()=>store.ActivateAsync(store.Candidate(new(){["startWithWindows"]=true}),1,TestContext.Current.CancellationToken));
        Assert.Equal("STARTUP_REGISTRATION_FAILED",error.Code);
        Assert.Equal(1,store.Revision);Assert.False(store.Snapshot()["startWithWindows"]!.GetValue<bool>());Assert.Null(registry.Value);
    }

    [Fact]
    public async Task RegistryReadbackPreventsReportingAnIgnoredWriteAsSuccess()
    {
        var registry=new FakeRegistry{IgnoreWrite=true};
        var entry=Path.Combine(_root,"WorkFor.exe");await File.WriteAllTextAsync(entry,"test only",TestContext.Current.CancellationToken);
        var store=new ConfigurationStore(Path.Combine(_root,"data"),new StartupRegistration(entry,registry:registry));
        var error=await Assert.ThrowsAsync<ConfigException>(()=>store.ActivateAsync(store.Candidate(new(){["startWithWindows"]=true}),1,TestContext.Current.CancellationToken));
        Assert.Equal("STARTUP_REGISTRATION_FAILED",error.Code);Assert.Equal(1,store.Revision);Assert.Null(registry.Value);
    }

    [Fact]
    public async Task DiskCommitFailureRollsBackRegistry()
    {
        var registry=new FakeRegistry();
        var entry=Path.Combine(_root,"WorkFor.exe");await File.WriteAllTextAsync(entry,"test only",TestContext.Current.CancellationToken);
        var store=new ConfigurationStore(Path.Combine(_root,"data"),new StartupRegistration(entry,registry:registry));
        await store.ActivateAsync(store.Candidate(new()),1,TestContext.Current.CancellationToken);
        using var locked=new FileStream(Path.Combine(_root,"data","settings.json"),FileMode.Open,FileAccess.Read,FileShare.Read);
        var error=await Assert.ThrowsAsync<ConfigException>(()=>store.ActivateAsync(store.Candidate(new(){["startWithWindows"]=true}),2,TestContext.Current.CancellationToken));
        Assert.Equal("CONFIG_WRITE_FAILED",error.Code);Assert.Null(registry.Value);Assert.Equal(2,store.Revision);
    }

    [Fact]
    public async Task RegistryRollbackFailureIsExplicitAndDoesNotCommitConfiguration()
    {
        var registry=new FakeRegistry{Value="previous trusted command",FailWriteNumber=2};
        var entry=Path.Combine(_root,"WorkFor.exe");await File.WriteAllTextAsync(entry,"test only",TestContext.Current.CancellationToken);
        var store=new ConfigurationStore(Path.Combine(_root,"data"),new StartupRegistration(entry,registry:registry));
        await store.ActivateAsync(store.Candidate(new()),1,TestContext.Current.CancellationToken);
        using var locked=new FileStream(Path.Combine(_root,"data","settings.json"),FileMode.Open,FileAccess.Read,FileShare.Read);
        var error=await Assert.ThrowsAsync<ConfigException>(()=>store.ActivateAsync(store.Candidate(new(){["startWithWindows"]=true}),2,TestContext.Current.CancellationToken));
        Assert.Equal("STARTUP_ROLLBACK_FAILED",error.Code);Assert.NotNull(store.StartupNotice);Assert.Equal(2,store.Revision);
    }

    [Fact]
    public void WindowsQuotingRoundTripsEmbeddedQuotesAndTrailingBackslashes()
    {
        string[] args=[@"C:\Program Files\WorkFor\WorkFor.exe","--data-dir",@"C:\salary data\","a\"b",""];
        var command=string.Join(" ",args.Select(StartupRegistration.Quote));
        var native=CommandLineToArgvW(command,out var count);
        try
        {
            Assert.NotEqual(IntPtr.Zero,native);Assert.Equal(args.Length,count);
            for(var i=0;i<count;i++)Assert.Equal(args[i],Marshal.PtrToStringUni(Marshal.ReadIntPtr(native,i*IntPtr.Size)));
        }
        finally{LocalFree(native);}
    }

    [Fact]
    public async Task SuccessfulSnapshotReloadIsAlwaysStaleAndDoesNotMatchOtherRules()
    {
        var snapshots=new SnapshotStore(_root);var snapshot=GoodSnapshot();
        await snapshots.SaveAsync(snapshot,TestContext.Current.CancellationToken);
        var loaded=snapshots.LoadStale(DateTimeOffset.UtcNow,7,"sha256:"+new string('a',64));
        Assert.NotNull(loaded);Assert.Equal("stale",loaded["status"]!.GetValue<string>());Assert.True(loaded["isPersisted"]!.GetValue<bool>());
        Assert.Equal("12860",loaded["amountMinor"]!.GetValue<string>());Assert.Equal(snapshot["evaluatedAtMs"]!.GetValue<long>(),loaded["evaluatedAtMs"]!.GetValue<long>());
        Assert.Null(snapshots.LoadStale(DateTimeOffset.UtcNow,8,"sha256:"+new string('a',64)));
        Assert.Null(snapshots.LoadStale(DateTimeOffset.UtcNow,7,"sha256:"+new string('b',64)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root,"state"),"*.tmp"));
    }

    [Fact]
    public async Task SnapshotCorruptionIsIgnoredAndErrorsCannotOverwriteLastSuccess()
    {
        var snapshots=new SnapshotStore(_root);var snapshot=GoodSnapshot();
        await snapshots.SaveAsync(snapshot,TestContext.Current.CancellationToken);
        snapshot["status"]="ruleError";
        await Assert.ThrowsAsync<ArgumentException>(()=>snapshots.SaveAsync(snapshot,TestContext.Current.CancellationToken));
        var broken=GoodSnapshot();broken.Remove("currency");
        await File.WriteAllTextAsync(Path.Combine(_root,"state","last-success.json"),new JsonObject{["schemaVersion"]=1,["snapshot"]=broken}.ToJsonString(),TestContext.Current.CancellationToken);
        Assert.Null(snapshots.LoadStale(DateTimeOffset.UtcNow,7,"sha256:"+new string('a',64)));Assert.NotNull(snapshots.Notice);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":99,\"snapshot\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"snapshot\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"snapshot\":{}}")]
    public async Task InvalidSnapshotDocumentsAlwaysProduceNoticeInsteadOfThrowing(string text)
    {
        Directory.CreateDirectory(Path.Combine(_root,"state"));
        await File.WriteAllTextAsync(Path.Combine(_root,"state","last-success.json"),text,TestContext.Current.CancellationToken);
        var snapshots=new SnapshotStore(_root);
        Assert.Null(snapshots.LoadStale(DateTimeOffset.UtcNow,7,"sha256:"+new string('a',64)));
        Assert.NotNull(snapshots.Notice);
    }

    [Fact]
    public async Task OversizedSnapshotIsIgnoredBeforeParsing()
    {
        Directory.CreateDirectory(Path.Combine(_root,"state"));
        await File.WriteAllTextAsync(Path.Combine(_root,"state","last-success.json"),new string(' ',32769),TestContext.Current.CancellationToken);
        var snapshots=new SnapshotStore(_root);
        Assert.Null(snapshots.LoadStale(DateTimeOffset.UtcNow,7,"sha256:"+new string('a',64)));
        Assert.NotNull(snapshots.Notice);
    }

    private static JsonObject GoodSnapshot()=>new(){["generation"]=Guid.NewGuid().ToString(),["sequence"]=2,["configRevision"]=7,["ruleHash"]="sha256:"+new string('a',64),["status"]="fresh",["title"]="今日收入",["amountMinor"]="12860",["amountText"]="¥128.60",["currency"]="CNY",["evaluatedAtMs"]=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()};
    private Task WriteConfig(JsonObject value)=>File.WriteAllTextAsync(Path.Combine(_root,"settings.json"),value.ToJsonString(),TestContext.Current.CancellationToken);
    public void Dispose(){var resolved=Path.GetFullPath(_root);if(!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(resolved).StartsWith("WorkFor.Persistence.",StringComparison.Ordinal))throw new InvalidOperationException("Unsafe test cleanup path.");Directory.Delete(resolved,recursive:true);}
    private sealed class FakeRegistry:IStartupRegistry
    {
        public string? Value;public int Writes;public bool FailWrite;public bool IgnoreWrite;public int FailWriteNumber;
        public string? Read()=>Value;
        public void Write(string command){Writes++;if(FailWrite||Writes==FailWriteNumber)throw new UnauthorizedAccessException("simulated");if(!IgnoreWrite)Value=command;}
        public void Delete()=>Value=null;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CommandLineToArgvW(string command,out int count);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
}
