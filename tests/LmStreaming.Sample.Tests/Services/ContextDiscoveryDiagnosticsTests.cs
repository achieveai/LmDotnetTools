
namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the in-memory arrival tracker behind <c>GET /api/diagnostics/context-discovery</c>: it
/// records each authenticated context-discovery webhook the app receives, keyed by session, so an
/// operator can see whether anything is actually arriving (and when). The silent-failure mode this
/// guards against is the gateway never reaching the loopback callback host — in which case the
/// count stays at zero for every session.
/// </summary>
public sealed class ContextDiscoveryDiagnosticsTests
{
    [Fact]
    public void RecordReceived_FirstArrival_SetsCountTimestampAndPath()
    {
        var diagnostics = new ContextDiscoveryDiagnostics();
        var before = DateTimeOffset.UtcNow;

        diagnostics.RecordReceived("sess-1", "context_file", "CLAUDE.md");

        var after = DateTimeOffset.UtcNow;
        var snapshot = diagnostics.Snapshot();
        snapshot.Should().ContainKey("sess-1");
        var state = snapshot["sess-1"];
        state.Count.Should().Be(1);
        state.LastKind.Should().Be("context_file");
        state.LastPath.Should().Be("CLAUDE.md");
        state.LastReceivedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void RecordReceived_MultipleSameSession_IncrementsAndKeepsLatest()
    {
        var diagnostics = new ContextDiscoveryDiagnostics();

        diagnostics.RecordReceived("sess-1", "context_file", "CLAUDE.md");
        diagnostics.RecordReceived("sess-1", "context_file", "AGENTS.md");

        var state = diagnostics.Snapshot()["sess-1"];
        state.Count.Should().Be(2);
        state.LastPath.Should().Be("AGENTS.md", "the latest arrival's path is surfaced");
    }

    [Fact]
    public void RecordReceived_DifferentSessions_TrackedIndependently()
    {
        var diagnostics = new ContextDiscoveryDiagnostics();

        diagnostics.RecordReceived("sess-1", "context_file", "CLAUDE.md");
        diagnostics.RecordReceived("sess-2", "subagent", "echo");
        diagnostics.RecordReceived("sess-2", "context_file", "AGENTS.md");

        var snapshot = diagnostics.Snapshot();
        snapshot["sess-1"].Count.Should().Be(1);
        snapshot["sess-2"].Count.Should().Be(2);
    }

    [Fact]
    public void RecordReceived_BlankSessionId_Ignored()
    {
        var diagnostics = new ContextDiscoveryDiagnostics();

        diagnostics.RecordReceived("", "context_file", "CLAUDE.md");
        diagnostics.RecordReceived("   ", "context_file", "CLAUDE.md");
        diagnostics.RecordReceived(null, "context_file", "CLAUDE.md");

        diagnostics.Snapshot().Should().BeEmpty("a discovery with no session id can't be attributed");
    }

    [Fact]
    public void Snapshot_OnFreshInstance_IsEmpty()
    {
        new ContextDiscoveryDiagnostics().Snapshot().Should().BeEmpty();
    }
}
