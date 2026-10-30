using System.Text;
using System.Text.Json.Nodes;
using WorkFor.Host;
using Xunit;

namespace WorkFor.Validation.Tests;

public sealed class NativeRecoveryBudgetTests
{
    private const string Fingerprint = "validated-native-binary-v1";

    [Fact]
    public void ThreeRelatedFailuresQuarantineAndSurviveHostRestart()
    {
        using var data = new TestDirectory();
        var clock = new ManualClock();
        var budget = new NativeRecoveryBudget(data.Path, Fingerprint, clock);
        Assert.False(budget.IsQuarantined);
        Assert.Equal(0, budget.FailureCount);

        budget.RecordFailure("BOOTSTRAP_FAILED");
        clock.Advance(TimeSpan.FromMinutes(2));
        budget.RecordFailure("EXPLORER_EXITED");
        Assert.False(budget.IsQuarantined);
        clock.Advance(TimeSpan.FromMinutes(2));
        budget.RecordFailure("NATIVE_PIPE_BROKEN");

        Assert.True(budget.IsQuarantined);
        Assert.Equal(3, budget.FailureCount);
        var restarted = new NativeRecoveryBudget(data.Path, Fingerprint, clock);
        Assert.True(restarted.IsQuarantined);
        Assert.Equal(3, restarted.FailureCount);
        Assert.Equal("NATIVE_PIPE_BROKEN", restarted.Describe()["lastCode"]!.GetValue<string>());
    }

    [Fact]
    public void ExpiredFailuresCannotAccidentallyCompleteTheThreshold()
    {
        using var data = new TestDirectory();
        var clock = new ManualClock();
        var budget = new NativeRecoveryBudget(data.Path, Fingerprint, clock);
        budget.RecordFailure("FIRST");
        budget.RecordFailure("SECOND");
        clock.Advance(TimeSpan.FromMinutes(11));
        budget.RecordFailure("NEW_WINDOW");

        Assert.False(budget.IsQuarantined);
        Assert.Equal(1, budget.FailureCount);
        Assert.Equal(1, new NativeRecoveryBudget(data.Path, Fingerprint, clock).FailureCount);
    }

    [Fact]
    public void QuarantineDoesNotAutomaticallyRetryAfterTheWindowExpires()
    {
        using var data = new TestDirectory();
        var clock = new ManualClock();
        var budget = new NativeRecoveryBudget(data.Path, Fingerprint, clock);
        for (var i = 0; i < 3; i++) budget.RecordFailure("RELATED_EXIT");
        clock.Advance(TimeSpan.FromHours(1));

        var restarted = new NativeRecoveryBudget(data.Path, Fingerprint, clock);
        Assert.Equal(0, restarted.FailureCount);
        Assert.True(restarted.IsQuarantined);
        Assert.True(restarted.Describe()["quarantined"]!.GetValue<bool>());
    }

    [Fact]
    public void ExplicitRetryClearsAndPersistsTheQuarantine()
    {
        using var data = new TestDirectory();
        var clock = new ManualClock();
        var budget = new NativeRecoveryBudget(data.Path, Fingerprint, clock);
        budget.RecordFailure("RETRY_BUDGET_EXCEEDED");
        Assert.True(budget.IsQuarantined);

        budget.Reset();
        var restarted = new NativeRecoveryBudget(data.Path, Fingerprint, clock);
        Assert.False(restarted.IsQuarantined);
        Assert.Equal(0, restarted.FailureCount);
        Assert.Null(restarted.Describe()["lastCode"]);
    }

    [Fact]
    public void ANewBinaryStartsItsOwnBudgetInsteadOfInheritingOldQuarantine()
    {
        using var data = new TestDirectory();
        var clock = new ManualClock();
        var oldBinary = new NativeRecoveryBudget(data.Path, Fingerprint, clock);
        oldBinary.RecordFailure("RETRY_BUDGET_EXCEEDED");

        var replacement = new NativeRecoveryBudget(data.Path, "validated-native-binary-v2", clock);
        Assert.False(replacement.IsQuarantined);
        Assert.Equal(0, replacement.FailureCount);
        replacement.RecordFailure("NEW_BINARY_FIRST_FAILURE");
        var restarted = new NativeRecoveryBudget(data.Path, "validated-native-binary-v2", clock);
        Assert.False(restarted.IsQuarantined);
        Assert.Equal(1, restarted.FailureCount);
    }

    [Theory]
    [InlineData("{ invalid json")]
    [InlineData("null")]
    [InlineData("[]")]
    public void BrokenRecoveryFilesQuarantineWithoutCrashingTheHost(string source)
    {
        using var data = new TestDirectory();
        data.WriteRecovery(source);
        var budget = new NativeRecoveryBudget(data.Path, Fingerprint, new ManualClock());
        AssertInvalidState(budget);
        budget.Reset();
        Assert.False(new NativeRecoveryBudget(data.Path, Fingerprint).IsQuarantined);
    }

    [Theory]
    [InlineData("quarantined")]
    [InlineData("failuresMs")]
    [InlineData("fingerprint")]
    [InlineData("schemaVersion")]
    public void MissingRequiredRecoveryFieldsAreNotTreatedAsANewInstallation(string field)
    {
        using var data = new TestDirectory();
        var saved = ValidRecoveryState();
        saved.Remove(field);
        data.WriteRecovery(saved.ToJsonString());
        AssertInvalidState(new NativeRecoveryBudget(data.Path, Fingerprint, new ManualClock()));
    }

    [Fact]
    public void OversizedRecoveryMetadataCannotSilentlyResetTheBudget()
    {
        using var data = new TestDirectory();
        var saved = ValidRecoveryState();
        saved["lastCode"] = new string('x', 17 * 1024);
        data.WriteRecovery(saved.ToJsonString());
        AssertInvalidState(new NativeRecoveryBudget(data.Path, Fingerprint, new ManualClock()));
    }

    [Fact]
    public void UnknownSchemaCannotSilentlyResetTheBudget()
    {
        using var data = new TestDirectory();
        var saved = ValidRecoveryState();
        saved["schemaVersion"] = 99;
        data.WriteRecovery(saved.ToJsonString());
        AssertInvalidState(new NativeRecoveryBudget(data.Path, Fingerprint, new ManualClock()));
    }

    [Theory]
    [InlineData("lastCode", "[]")]
    [InlineData("fingerprint", "123")]
    [InlineData("quarantined", "\"false\"")]
    [InlineData("failuresMs", "null")]
    [InlineData("failuresMs", "[\"not-a-timestamp\"]")]
    [InlineData("failuresMs", "[999999999999999999999999999999999]")]
    public void InvalidRecoveryFieldTypesQuarantineWithoutEscapingTheConstructor(string field, string json)
    {
        using var data = new TestDirectory();
        var saved = ValidRecoveryState();
        saved[field] = JsonNode.Parse(json);
        data.WriteRecovery(saved.ToJsonString());
        AssertInvalidState(new NativeRecoveryBudget(data.Path, Fingerprint, new ManualClock()));
    }

    [Fact]
    public void FailureCodeIsBoundedBeforePersisting()
    {
        using var data = new TestDirectory();
        var budget = new NativeRecoveryBudget(data.Path, Fingerprint, new ManualClock());
        budget.RecordFailure(new string('X', 1000));
        Assert.Equal("NATIVE_FAILURE", budget.Describe()["lastCode"]!.GetValue<string>());
        Assert.True(new FileInfo(data.RecoveryFile).Length < 16 * 1024);
    }

    private static JsonObject ValidRecoveryState() => new()
    {
        ["schemaVersion"] = 1,
        ["fingerprint"] = Fingerprint,
        ["quarantined"] = false,
        ["lastCode"] = "RELATED_EXIT",
        ["failuresMs"] = new JsonArray()
    };

    private static void AssertInvalidState(NativeRecoveryBudget budget)
    {
        Assert.True(budget.IsQuarantined);
        Assert.Equal("RECOVERY_STATE_INVALID", budget.Describe()["lastCode"]!.GetValue<string>());
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class TestDirectory : IDisposable
    {
        private readonly string _parent = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WorkFor.Validation.Tests"));
        public string Path { get; }
        public string RecoveryFile => System.IO.Path.Combine(Path, "state", "native-recovery.json");
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(_parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void WriteRecovery(string source)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RecoveryFile)!);
            File.WriteAllText(RecoveryFile, source, new UTF8Encoding(false));
        }
        public void Dispose()
        {
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(_parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean a directory outside the test root.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
