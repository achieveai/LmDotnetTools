namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// An egress allow/deny rule to attach to a sandbox at creation. The SDK only carries this value
/// through to the gateway's <c>network</c> field — it neither constructs network policy nor
/// decides which rules a workspace needs; that decision belongs to the application.
/// </summary>
public sealed class SandboxNetworkRule
{
    /// <summary>Rule id, unique within the request.</summary>
    public string Id { get; }

    /// <summary>Gateway rule action (e.g. <c>"allow"</c> or <c>"deny"</c>).</summary>
    public string Action { get; }

    /// <summary>Hosts this rule matches, defensively copied at construction.</summary>
    public IReadOnlyList<string> Hosts { get; }

    /// <summary>Ports this rule matches, defensively copied at construction.</summary>
    public IReadOnlyList<int> Ports { get; }

    /// <summary>HTTP methods this rule matches, defensively copied at construction.</summary>
    public IReadOnlyList<string> Methods { get; }

    /// <summary>URL path patterns this rule matches, defensively copied at construction.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>
    /// Id of the <see cref="SandboxAuthProvider"/> this rule injects a token from, or an empty
    /// string when the rule requires no token injection.
    /// </summary>
    public string AuthProvider { get; }

    /// <summary>OAuth scopes this rule requires, defensively copied at construction.</summary>
    public IReadOnlyList<string> RequiredScopes { get; }

    /// <summary>Rule evaluation priority; lower values are evaluated first.</summary>
    public int Priority { get; }

    public SandboxNetworkRule(
        string id,
        string action,
        IReadOnlyList<string>? hosts = null,
        IReadOnlyList<int>? ports = null,
        IReadOnlyList<string>? methods = null,
        IReadOnlyList<string>? paths = null,
        string authProvider = "",
        IReadOnlyList<string>? requiredScopes = null,
        int priority = 0
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(authProvider);

        Id = id;
        Action = action;
        Hosts = hosts is null ? [] : [.. hosts];
        Ports = ports is null ? [] : [.. ports];
        Methods = methods is null ? [] : [.. methods];
        Paths = paths is null ? [] : [.. paths];
        AuthProvider = authProvider;
        RequiredScopes = requiredScopes is null ? [] : [.. requiredScopes];
        Priority = priority;
    }
}
