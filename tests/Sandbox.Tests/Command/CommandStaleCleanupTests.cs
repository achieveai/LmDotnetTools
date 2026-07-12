using System.Globalization;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

public class CommandStaleCleanupTests
{
    private const long Now = 2_000_000_000;
    private const long Day = CommandArtifactLayout.StaleAgeSeconds;

    /// <summary>A valid, distinct 32-char lowercase-hex operation-directory name — the only shape the stale sweep (and its name validation) accepts.</summary>
    private static string HexName(int seed) => seed.ToString("x8", CultureInfo.InvariantCulture).PadLeft(32, '0');

    [Fact]
    public void SelectStale_ExpiredButYoungerThan24h_IsKept()
    {
        var entries = new[] { ("young", Now - 100, Now - 3600) };

        CommandStaleCleanup.SelectStale(entries, Now).Should().BeEmpty();
    }

    [Fact]
    public void SelectStale_ExpiredAndExactly24hOld_IsKept_WithinRetentionWindow()
    {
        // The 24h retention window is INCLUSIVE of its boundary: an operation exactly 24h old is still
        // recoverable, so a same-id retry at =24h is answered from the marker, never re-run.
        var entries = new[] { ("boundary", Now - 100, Now - Day) };

        CommandStaleCleanup.SelectStale(entries, Now).Should().BeEmpty();
    }

    [Fact]
    public void SelectStale_ExpiredAndJustPastTheRetentionWindow_IsDeleted()
    {
        var entries = new[] { ("just-past", Now - 100, Now - (Day + 1)) };

        CommandStaleCleanup.SelectStale(entries, Now).Should().ContainSingle().Which.Should().Be("just-past");
    }

    [Fact]
    public void SelectStale_ExpiredAndOlderThan24h_IsDeleted()
    {
        var entries = new[] { ("old", Now - 100, Now - (Day * 3)) };

        CommandStaleCleanup.SelectStale(entries, Now).Should().ContainSingle().Which.Should().Be("old");
    }

    [Fact]
    public void SelectStale_ActiveLeaseIsProtected_EvenWhenOld()
    {
        var entries = new[] { ("active", Now + 1000, Now - (Day * 5)) };

        CommandStaleCleanup.SelectStale(entries, Now).Should().BeEmpty();
    }

    [Fact]
    public void SelectStale_NeverEstablishedLease_IsProtected_NotDeleted()
    {
        // A directory still claiming has no established lease yet (0). It must never be selected, or
        // cleanup would race a winner mid-establishment — the GC-path form of the F2 TOCTOU.
        var entries = new[] { ("claiming", 0L, Now - (Day * 5)) };

        CommandStaleCleanup.SelectStale(entries, Now).Should().BeEmpty();
    }

    [Fact]
    public void SelectStale_NeverEstablishedCreated_IsProtected_NotDeleted()
    {
        var entries = new[] { ("claiming", Now - 100, 0L) };

        CommandStaleCleanup.SelectStale(entries, Now).Should().BeEmpty();
    }

    [Fact]
    public void SelectStale_Mixed_SelectsOnlyExpiredAndPastTheRetentionWindow()
    {
        var entries = new[]
        {
            ("young", Now - 100, Now - 3600),
            ("boundary", Now - 1, Now - Day),
            ("just-past", Now - 1, Now - (Day + 1)),
            ("old", Now - 100, Now - (Day * 2)),
            ("active", Now + 100, Now - (Day * 9)),
        };

        // "young" (within window), "boundary" (exactly 24h, still retained), and "active" (unexpired
        // lease) are all kept; only the two strictly past the retention window are selected.
        CommandStaleCleanup.SelectStale(entries, Now).Should().BeEquivalentTo(["just-past", "old"]);
    }

    [Fact]
    public async Task ExecuteAsync_TriggersStaleCleanup_DeletesOnlyExpiredOldDirectories()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory("sess-1", "op-1");
        fake.Program(op, exitCode: 0, stdout: Encoding.UTF8.GetBytes("ok"), stderr: []);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var stale = HexName(1);
        var active = HexName(2);
        var young = HexName(3);
        fake.AddGcEntry(stale, now - 100, now - (Day * 2));
        fake.AddGcEntry(active, now + 100_000, now - (Day * 2));
        fake.AddGcEntry(young, now - 100, now - 100);

        _ = await client.ExecuteAsync("sess-1", new SandboxCommand(["work"], operationId: "op-1"));

        fake.CleanedOperations.Should().Contain(stale);
        fake.CleanedOperations.Should().NotContain(active);
        fake.CleanedOperations.Should().NotContain(young);
    }

    [Fact]
    public async Task ExecuteAsync_StaleCleanup_IsBounded_NeverScansBeyondTheScanLimit()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory("sess-1", "op-1");
        fake.Program(op, exitCode: 0, stdout: Encoding.UTF8.GetBytes("ok"), stderr: []);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var overLimit = CommandArtifactLayout.StaleScanLimit + 5;
        var gcNames = Enumerable.Range(0, overLimit).Select(HexName).ToList();
        foreach (var name in gcNames)
        {
            fake.AddGcEntry(name, now - 100, now - (Day * 2));
        }

        _ = await client.ExecuteAsync("sess-1", new SandboxCommand(["work"], operationId: "op-1"));

        fake.CleanedOperations.Count(gcNames.Contains).Should().Be(CommandArtifactLayout.StaleScanLimit);
        fake.CleanedOperations.Should().Contain(gcNames[0]);
        fake.CleanedOperations.Should().NotContain(gcNames[CommandArtifactLayout.StaleScanLimit + 4]);
    }

    [Fact]
    public async Task ExecuteAsync_StaleCleanup_IsSessionScoped_NeverTouchesAnotherSession()
    {
        var fakeA = new FakeSandboxGateway();
        var fakeB = new FakeSandboxGateway();
        using var clientA = CommandTestSupport.CreateClient(fakeA);
        var opA = CommandTestSupport.OperationDirectory("sess-A", "op-1");
        fakeA.Program(opA, exitCode: 0, stdout: Encoding.UTF8.GetBytes("ok"), stderr: []);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var aStale = HexName(10);
        var bStale = HexName(11);
        fakeA.AddGcEntry(aStale, now - 100, now - (Day * 2));
        fakeB.AddGcEntry(bStale, now - 100, now - (Day * 2));

        _ = await clientA.ExecuteAsync("sess-A", new SandboxCommand(["work"], operationId: "op-1"));

        fakeA.CleanedOperations.Should().Contain(aStale);
        fakeB.Requests.Should().BeEmpty();
        fakeB.CleanedOperations.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_StaleSweep_IgnoresNonHexDirectoryNames_NeverPurgingThem()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory("sess-1", "op-1");
        fake.Program(op, exitCode: 0, stdout: Encoding.UTF8.GetBytes("ok"), stderr: []);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // A stale-but-non-hex name (uppercase, wrong length, or shell metacharacters) must be rejected by
        // the SDK's listing validation and never handed to a purge — defense in depth beyond the shell.
        fake.AddGcEntry("$(touch pwned)", now - 100, now - (Day * 2));
        fake.AddGcEntry("../escape", now - 100, now - (Day * 2));
        fake.AddGcEntry(new string('A', 32), now - 100, now - (Day * 2));

        _ = await client.ExecuteAsync("sess-1", new SandboxCommand(["work"], operationId: "op-1"));

        fake.Requests.Should().NotContain(r => r.Kind == CommandScriptKind.GcPurge);
        fake.CleanedOperations.Should().OnlyContain(name => name == op);
    }
}
