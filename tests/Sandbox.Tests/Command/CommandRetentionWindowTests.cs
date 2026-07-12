using System.Text;
using AchieveAi.LmDotnetTools.Sandbox.Command;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// F1: the operation-id idempotency/recovery retention window is bounded at 24 hours, and the SDK's
/// behavior is honest about it end-to-end. WITHIN the window (younger than 24h, and exactly at the 24h
/// boundary) a same-id retry is answered from the retained completion marker and the command is NEVER
/// re-run. PAST the window the bounded stale sweep reclaims the marker, after which a same-id retry is
/// treated as a NEW operation and legitimately re-executes. These drive the real
/// <see cref="SandboxClient.ExecuteAsync"/> against a <see cref="FakeSandboxGateway"/> whose stale-purge
/// re-validation uses the same deterministic age rule as the shipped shell wrapper, so the window
/// boundary is proven with seeded timestamps rather than a 24h wall-clock wait.
/// </summary>
public class CommandRetentionWindowTests
{
    private const string Session = "sess-1";
    private const long Day = CommandArtifactLayout.StaleAgeSeconds;

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public async Task ExecuteAsync_SameIdWellWithinRetentionWindow_RecoversMarker_WithoutReRunning()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var command = new SandboxCommand(["work"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        var now = Now;
        // Completed one hour ago (deep within the 24h window), lease already expired.
        fake.SeedCompleted(
            op,
            CommandTestSupport.Digest(Session, command),
            exitCode: 5,
            stdout: Utf8("retained-output"),
            stderr: Utf8("retained-err"),
            lease: now - 60,
            created: now - 3600
        );
        fake.AddGcEntry(op, now - 60, now - 3600);

        var result = await client.ExecuteAsync(Session, command);

        result.ExitCode.Should().Be(5);
        result.StandardOutput.Should().Be("retained-output");
        // Answered from the marker, and the within-window sweep never reclaims it.
        fake.SideEffectCount.Should().Be(0);
        fake.RunSubmissionCount.Should().Be(0);
        fake.Requests.Should().NotContain(r => r.Kind == CommandScriptKind.GcPurge);
    }

    [Fact]
    public async Task ExecuteAsync_SameIdExactlyAtRetentionBoundary_StillRecovers_MarkerNotSwept()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var command = new SandboxCommand(["work"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        var now = Now;
        // Exactly 24h old with an expired lease: the boundary is INCLUSIVE, so it stays recoverable.
        fake.SeedCompleted(
            op,
            CommandTestSupport.Digest(Session, command),
            exitCode: 0,
            stdout: Utf8("boundary-output"),
            stderr: [],
            lease: now - 100,
            created: now - Day
        );
        fake.AddGcEntry(op, now - 100, now - Day);
        // A different programmed output would surface only if the command were (wrongly) re-run.
        fake.Program(op, exitCode: 9, stdout: Utf8("SHOULD-NOT-RUN"), stderr: []);

        var first = await client.ExecuteAsync(Session, command);
        var second = await client.ExecuteAsync(Session, command);

        first.StandardOutput.Should().Be("boundary-output");
        second.StandardOutput.Should().Be("boundary-output", "an operation exactly 24h old is still recovered, never re-run");
        second.ExitCode.Should().Be(0);
        // The command never ran: both calls were answered from the retained marker, and the =24h sweep
        // did not reclaim it.
        fake.SideEffectCount.Should().Be(0);
        fake.RunSubmissionCount.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_SameIdPastRetentionWindow_MarkerSwept_ReExecutesAsNewOperation()
    {
        var fake = new FakeSandboxGateway();
        using var client = CommandTestSupport.CreateClient(fake);
        var command = new SandboxCommand(["work"], operationId: "op-1");
        var op = CommandTestSupport.OperationDirectory(Session, "op-1");
        var now = Now;
        // Completed two days ago with a long-expired lease: strictly past the retention window.
        fake.SeedCompleted(
            op,
            CommandTestSupport.Digest(Session, command),
            exitCode: 1,
            stdout: Utf8("stale-marker-output"),
            stderr: [],
            lease: now - Day,
            created: now - (Day * 2)
        );
        fake.AddGcEntry(op, now - Day, now - (Day * 2));
        // What a genuine re-execution (after the marker is swept) would produce.
        fake.Program(op, exitCode: 0, stdout: Utf8("fresh-execution"), stderr: []);

        // First call recovers the retained marker (no re-run), then its end-of-run sweep reclaims the
        // past-window marker.
        var recovered = await client.ExecuteAsync(Session, command);
        recovered.StandardOutput.Should().Be("stale-marker-output");
        recovered.ExitCode.Should().Be(1);
        fake.SideEffectCount.Should().Be(0);
        fake.CleanedOperations.Should().Contain(op, "the past-window marker is reclaimed by the bounded sweep");

        // A later same-id retry now finds nothing and is treated as a NEW operation — it re-executes.
        var reexecuted = await client.ExecuteAsync(Session, command);

        reexecuted.StandardOutput.Should().Be("fresh-execution");
        reexecuted.ExitCode.Should().Be(0);
        fake.SideEffectCount.Should().Be(1, "past the retention window the command legitimately runs again");
    }
}
