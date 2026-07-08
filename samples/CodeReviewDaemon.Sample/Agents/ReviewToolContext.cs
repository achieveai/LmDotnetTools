using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Per-run context that turns a diff-only review loop into a tool-assisted one. Non-null only on the
/// <c>EnableToolAssistedReview</c> path; when null the factory builds today's empty-registry loop.
/// <para>
/// The pooled scoped-writable reviewer (Layer 1) additionally sets <see cref="EnableReviewerWrites"/>
/// with a <see cref="WritableToolAllowList"/> and the <see cref="NotesDir"/>/<see cref="ScratchDir"/>
/// roots the writes are scoped to. When those are present the factory builds the registry via
/// <see cref="ScopedToolFilter"/> (read-only tools + scoped <c>Write</c>/<c>Edit</c>/<c>Bash</c>);
/// otherwise it stays on the read-only <see cref="ReadOnlyToolFilter"/> path exactly as before.
/// </para>
/// </summary>
internal sealed record ReviewToolContext(
    string GatewayBaseUrl,
    string SessionId,
    IReadOnlyList<string> ReadOnlyToolAllowList,
    SubAgentOptions? SubAgentOptions,
    bool EnableReviewerWrites = false,
    IReadOnlyList<string>? WritableToolAllowList = null,
    string? NotesDir = null,
    string? ScratchDir = null);

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
