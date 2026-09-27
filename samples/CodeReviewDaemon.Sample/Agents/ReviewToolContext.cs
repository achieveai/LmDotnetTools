using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Compatibility input for the shared review-loop factory contract. The S2S transport does not consume
/// this in-process tool context; production workflow grants are applied by the hosted session adapter.
/// </summary>
internal sealed record ReviewToolContext(
    string GatewayBaseUrl,
    string SessionId,
    IReadOnlyList<string> ReadOnlyToolAllowList,
    SandboxCredential Credential
);

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
