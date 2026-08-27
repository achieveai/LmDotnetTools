using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Per-run context that turns a diff-only review loop into a tool-assisted one. Non-null only on the
/// non-S2S <c>EnableToolAssistedReview</c> path; when null the factory builds today's empty-registry loop.
/// <para>
/// <see cref="ReadOnlyToolAllowList"/> is DATA carried on this record, not an enforcement point.
/// <see cref="ReadOnlyToolFilter"/> is the only thing that would apply it, and it has no production caller:
/// the sole <c>IReviewAgentLoopFactory</c> implementation, <c>S2SReviewAgentLoopFactory</c>, never reads this
/// context. Nothing in this process bounds the hosted agent's tool set today — the tools it gets come from
/// the gateway session the LmStreaming review host provisions.
/// </para>
/// <para>
/// Write-scoping is NOT enforced here, and it is NOT enforced by a sandbox mount either. The pooled-review
/// design settled that: a per-path read-only bind mount is unavailable (the gateway exposes one
/// whole-workspace <c>read_only</c> flag), so <c>repos/&lt;Repo&gt;</c> stays writable to the agent —
/// see the R1–R3 resolution in
/// <c>docs/superpowers/specs/2026-07-05-daemon-pooled-review-workspace-design.md</c>. What bounds the
/// reviewer is that writes outside the notes dir are INEFFECTIVE rather than blocked:
/// </para>
/// <list type="number">
/// <item>the COMMIT GATE — <c>DaemonReviewStageExecutor.CommitPooledNotesAsync</c> commits with
/// <c>stagePaths: [lease.NotesRelPath]</c>, so only <c>PRs/&lt;pr&gt;/…</c> is ever staged;</item>
/// <item>no write credential in the agent session;</item>
/// <item><c>SlotHygiene</c>'s strip + clean-on-entry, which erases anything else in the slot before reuse.</item>
/// </list>
/// <para>
/// A reader looking for the write-scope enforcement point should look at the commit gate and
/// <c>SlotHygiene</c> — not at a mount, and not at any tool allow-list. This matches what
/// <c>appsettings.s2s.json</c> records under <c>_EnableReviewerWrites_comment</c>.
/// </para>
/// <para>
/// Sub-agent orchestration (templates, discovery, model selection) is ALWAYS handled by the LmStreaming
/// review host on the S2S path — the daemon never builds SubAgentOptions locally.
/// </para>
/// </summary>
internal sealed record ReviewToolContext(
    string GatewayBaseUrl,
    string SessionId,
    IReadOnlyList<string> ReadOnlyToolAllowList,
    SandboxCredential Credential);

/// <summary>
/// Copies ONLY the allow-listed tool contracts+handlers from a source registry into the loop's registry:
/// <c>Write</c>/<c>Edit</c> (and anything else off the allow-list) are dropped even if the gateway
/// advertises them.
/// <para>
/// REACHABILITY: this is what the filter would do if it ran, not a boundary that is standing today. It has
/// no production caller — only <c>ReadOnlyToolFilterTests</c> invokes <see cref="Apply"/> — because the
/// in-process registry-building path it belonged to was retired when the daemon went mandatory-S2S, and the
/// hosted agent's tool set is decided by the gateway session instead. Do not cite it as an active control.
/// </para>
/// </summary>
internal static class ReadOnlyToolFilter
{
    public static void Apply(FunctionRegistry source, FunctionRegistry target, IReadOnlyList<string> allowList)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(allowList);

        var allowed = new HashSet<string>(allowList, StringComparer.Ordinal);
        var (contracts, handlers) = source.Build();
        foreach (var contract in contracts)
        {
            if (allowed.Contains(contract.Name) && handlers.TryGetValue(contract.Name, out var handler))
            {
                _ = target.AddFunction(contract, handler, "sandbox");
            }
        }
    }
}
