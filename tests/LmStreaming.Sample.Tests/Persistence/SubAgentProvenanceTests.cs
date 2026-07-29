using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>
/// Pure unit coverage for <see cref="SubAgentProvenance"/>'s <c>Build</c>/<c>TryProject</c> pair — the
/// write/read halves of the durable parent→child stamp, independent of any store or wrapper.
/// Task 1 (daemon-recursive-review-completion-barrier) adds the exact terminal status/timestamp the
/// manager pushes causally at completion, so a reconstructed roster reports the real outcome instead
/// of the old always-<c>"persisted"</c> placeholder.
/// </summary>
public sealed class SubAgentProvenanceTests
{
    private const string ParentThreadId = "thread-parent";
    private const string ChildThreadId = "subagent-child-1";

    private static SubAgentSnapshot MakeSnapshot(
        SubAgentStatus status,
        DateTimeOffset? terminalAtUtc = null) =>
        new(
            AgentId: "child-1",
            Name: "alpha",
            TemplateName: "code-reviewer:security",
            Task: "check auth",
            Status: status,
            ThreadId: ChildThreadId,
            LastActivityUtc: null,
            TerminalAtUtc: terminalAtUtc);

    [Theory]
    [InlineData(SubAgentStatus.Completed)]
    [InlineData(SubAgentStatus.Error)]
    [InlineData(SubAgentStatus.Stopped)]
    public void Build_StampsExactStatusAndTerminalTimestamp_ForTerminalStatuses(SubAgentStatus status)
    {
        var terminalAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var properties = SubAgentProvenance.Build(
            ParentThreadId, MakeSnapshot(status, terminalAt));

        properties[SubAgentProvenance.StatusKey].Should().Be(status.ToString().ToLowerInvariant());
        properties[SubAgentProvenance.TerminalAtKey].Should().Be(terminalAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void Build_StampsRunningStatus_ButNoTerminalTimestamp()
    {
        var properties = SubAgentProvenance.Build(ParentThreadId, MakeSnapshot(SubAgentStatus.Running));

        properties[SubAgentProvenance.StatusKey].Should().Be("running");
        properties.ContainsKey(SubAgentProvenance.TerminalAtKey).Should().BeFalse(
            "a still-running sub-agent has no terminal instant to record");
    }

    [Fact]
    public void Build_FallsBackToUtcNow_WhenSnapshotOmitsTerminalAtUtc()
    {
        var before = DateTimeOffset.UtcNow;
        var properties = SubAgentProvenance.Build(
            ParentThreadId, MakeSnapshot(SubAgentStatus.Completed, terminalAtUtc: null));
        var after = DateTimeOffset.UtcNow;

        var stamped = (long)properties[SubAgentProvenance.TerminalAtKey];
        stamped.Should().BeInRange(before.ToUnixTimeMilliseconds(), after.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void RoundTrip_BuildThenTryProject_PreservesExactTerminalStatusAndTimestamp()
    {
        var terminalAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_500_000);
        var metadata = new ThreadMetadata
        {
            ThreadId = ChildThreadId,
            LastUpdated = 1, // Deliberately stale/irrelevant: TerminalAtKey must win over this.
            Properties = SubAgentProvenance.Build(
                ParentThreadId, MakeSnapshot(SubAgentStatus.Completed, terminalAt)),
        };

        var summary = SubAgentProvenance.TryProject(metadata, ParentThreadId);

        summary.Should().NotBeNull();
        summary!.Status.Should().Be("completed");
        summary.LastActivityUtc.Should().Be(terminalAt,
            "the exact terminal instant captured at the transition must survive the round trip " +
            "rather than being recomputed from LastUpdated");
    }

    [Fact]
    public void TryProject_FallsBackToLastUpdated_WhenStatusIsRunning()
    {
        var metadata = new ThreadMetadata
        {
            ThreadId = ChildThreadId,
            LastUpdated = 42_000,
            Properties = SubAgentProvenance.Build(ParentThreadId, MakeSnapshot(SubAgentStatus.Running)),
        };

        var summary = SubAgentProvenance.TryProject(metadata, ParentThreadId);

        summary!.Status.Should().Be("running");
        summary.LastActivityUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(42_000),
            "with no terminal instant stamped, activity falls back to the metadata's own LastUpdated");
    }

    [Fact]
    public void TryProject_ReturnsUnknownStatus_ForLegacyMetadataWithNoStatusStamp()
    {
        // Simulates metadata persisted before Task 1's status/terminal-timestamp stamps existed:
        // only the parent link (and possibly name/template/task) is present.
        var metadata = new ThreadMetadata
        {
            ThreadId = ChildThreadId,
            LastUpdated = 5_000,
            Properties = SubAgentProvenance.Build(ParentThreadId, snapshot: null),
        };

        var summary = SubAgentProvenance.TryProject(metadata, ParentThreadId);

        summary!.Status.Should().Be(SubAgentProvenance.UnknownStatus);
        summary.Status.Should().Be("unknown");
        summary.LastActivityUtc.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(5_000));
    }

    [Fact]
    public void TryProject_ReturnsNull_WhenParentThreadIdDoesNotMatch()
    {
        var metadata = new ThreadMetadata
        {
            ThreadId = ChildThreadId,
            LastUpdated = 1,
            Properties = SubAgentProvenance.Build(
                "thread-someone-else", MakeSnapshot(SubAgentStatus.Completed, DateTimeOffset.UtcNow)),
        };

        SubAgentProvenance.TryProject(metadata, ParentThreadId).Should().BeNull();
    }
}
