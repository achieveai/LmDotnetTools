using System.Text;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

public class CommandStaleCleanupTests
{
    private const long Now = 2_000_000_000;
    private const long Day = CommandArtifactLayout.StaleAgeSeconds;

    [Fact]
    public void SelectStale_ExpiredButYoungerThan24h_IsKept()
    {
        var entries = new[] { ("young", Now - 100, Now - 3600) };

        CommandStaleCleanup.SelectStale(entries, Now).Should().BeEmpty();
    }

    [Fact]
    public void SelectStale_ExpiredAndExactly24hOld_IsDeleted()
    {
        var entries = new[] { ("boundary", Now - 100, Now - Day) };

        CommandStaleCleanup.SelectStale(entries, Now).Should().ContainSingle().Which.Should().Be("boundary");
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
    public void SelectStale_Mixed_SelectsOnlyExpiredAndOldEnough()
    {
        var entries = new[]
        {
            ("young", Now - 100, Now - 3600),
            ("boundary", Now - 1, Now - Day),
            ("old", Now - 100, Now - (Day * 2)),
            ("active", Now + 100, Now - (Day * 9)),
        };

        CommandStaleCleanup.SelectStale(entries, Now).Should().BeEquivalentTo(["boundary", "old"]);
    }

    [Fact]
    public async Task ExecuteAsync_TriggersStaleCleanup_DeletesOnlyExpiredOldDirectories()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var op = CommandTestSupport.OperationDirectory("sess-1", "op-1");
        fake.Program(op, exitCode: 0, stdout: Encoding.UTF8.GetBytes("ok"), stderr: []);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        fake.AddGcEntry("stale", now - 100, now - (Day * 2));
        fake.AddGcEntry("active", now + 100_000, now - (Day * 2));
        fake.AddGcEntry("young", now - 100, now - 100);

        _ = await client.ExecuteAsync("sess-1", new SandboxCommand(["work"], operationId: "op-1"));

        fake.CleanedOperations.Should().Contain("stale");
        fake.CleanedOperations.Should().NotContain("active");
        fake.CleanedOperations.Should().NotContain("young");
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
        for (var i = 0; i < overLimit; i++)
        {
            fake.AddGcEntry($"stale-{i}", now - 100, now - (Day * 2));
        }

        _ = await client.ExecuteAsync("sess-1", new SandboxCommand(["work"], operationId: "op-1"));

        fake.CleanedOperations.Count(name => name.StartsWith("stale-", StringComparison.Ordinal))
            .Should()
            .Be(CommandArtifactLayout.StaleScanLimit);
        fake.CleanedOperations.Should().Contain("stale-0");
        fake.CleanedOperations.Should().NotContain($"stale-{CommandArtifactLayout.StaleScanLimit + 4}");
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
        fakeA.AddGcEntry("a-stale", now - 100, now - (Day * 2));
        fakeB.AddGcEntry("b-stale", now - 100, now - (Day * 2));

        _ = await clientA.ExecuteAsync("sess-A", new SandboxCommand(["work"], operationId: "op-1"));

        fakeA.CleanedOperations.Should().Contain("a-stale");
        fakeB.Requests.Should().BeEmpty();
        fakeB.CleanedOperations.Should().BeEmpty();
    }
}
