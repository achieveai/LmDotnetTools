using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Reads the completion barrier's snapshot straight from a live, in-process <see cref="SubAgentManager"/>
/// — the manager for the review loop this source was constructed against, passed in directly from the same
/// call stack that owns it (e.g. the review executor holding the loop's own
/// <c>MultiTurnAgentLoop.SubAgentManager</c>). Deliberately NOT resolved through a loop-lookup registry: the
/// brief for this task is explicit that no such registry should be added to
/// <c>LiveReviewAgentLoopFactory</c> — the manager's identity is established once, at construction, and
/// never re-resolved.
/// <see cref="SubAgentManager.ListAgents"/> already returns a point-in-time, handle-free snapshot (its own
/// doc comment: "Carries no live handles and is safe to hand to callers outside the SubAgents module"), so
/// this adapter only needs to map that shape onto the barrier's provider-neutral
/// <see cref="ReviewSubAgentNode"/> — no execution handle (agent, provider, or the manager itself) is ever
/// placed on a returned node.
/// </summary>
internal sealed class InProcessReviewSubAgentCompletionSource : IReviewSubAgentCompletionSource
{
    private readonly SubAgentManager _manager;

    public InProcessReviewSubAgentCompletionSource(SubAgentManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    /// <summary>
    /// Every currently-registered sub-agent is, by construction, a direct (depth-1) child of
    /// <paramref name="parentThreadId"/> — the in-process manager only ever tracks its own review loop's
    /// direct spawns, never grandchildren, so no recursive walk is needed here. <paramref name="run"/> is
    /// unused: the manager's registered children are already scoped to this one run's loop.
    /// </summary>
    public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
        ReviewRun run,
        string parentThreadId,
        CancellationToken ct)
    {
        var nodes = _manager
            .ListAgents()
            .Select(snapshot => new ReviewSubAgentNode
            {
                AgentId = snapshot.AgentId,
                ThreadId = snapshot.ThreadId,
                ParentThreadId = parentThreadId,
                Depth = 1,
                Status = MapStatus(snapshot.Status),
                Name = snapshot.Name,
                Template = snapshot.TemplateName,
                TerminalAtUtc = snapshot.TerminalAtUtc,
                FailureCode = null,
            })
            .ToArray();

        return Task.FromResult(new ReviewSubAgentTreeSnapshot(nodes));
    }

    /// <summary>
    /// <see cref="SubAgentStatus"/> is a closed 4-case enum never deserialized from untrusted input, so
    /// every case is handled explicitly; the default arm exists only as defense-in-depth against a future
    /// case being added here and forgotten — mapping it to <see cref="ReviewSubAgentStatus.Unknown"/> (which
    /// keeps the barrier waiting) rather than silently defaulting to terminal, mirroring the S2S source's
    /// fail-closed philosophy for malformed/unrecognized status values.
    /// </summary>
    private static ReviewSubAgentStatus MapStatus(SubAgentStatus status) =>
        status switch
        {
            SubAgentStatus.Running => ReviewSubAgentStatus.Running,
            SubAgentStatus.Completed => ReviewSubAgentStatus.Completed,
            SubAgentStatus.Error => ReviewSubAgentStatus.Error,
            SubAgentStatus.Stopped => ReviewSubAgentStatus.Stopped,
            _ => ReviewSubAgentStatus.Unknown,
        };
}
