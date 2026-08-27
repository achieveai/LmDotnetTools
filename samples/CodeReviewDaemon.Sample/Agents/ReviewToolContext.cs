using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Per-run context that turns a diff-only review loop into a tool-assisted one. Non-null only on the
/// non-S2S <c>EnableToolAssistedReview</c> path; when null the factory builds today's empty-registry loop.
/// The agent's tool set is bounded to the read-only allow-list via <see cref="ReadOnlyToolFilter"/>.
/// <para>
/// Write-scoping is NOT enforced here. On the mandatory-S2S path the review runs inside a conversation the
/// LmStreaming review host owns, and the ONLY thing that bounds where the reviewer may write is the review
/// lease's sandbox mount — the leased slot is the reviewer's writable surface, and nothing outside it is
/// mounted writable. There is no in-process tool filter that grants or scopes writes; a reader looking for
/// the write-scope enforcement point should look at the lease's sandbox mount, not at any tool allow-list.
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
/// Copies ONLY the allow-listed tool contracts+handlers from a source registry into the loop's registry.
/// The daemon owns all posting, so <c>Write</c>/<c>Edit</c> (and anything else off the allow-list) are
/// dropped even if the gateway advertises them — a hard read-only boundary on the agent's tool set.
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
