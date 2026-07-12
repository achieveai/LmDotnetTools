namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Request to create a sandbox. <see cref="Workspace"/> is a LOGICAL workspace identifier — the
/// relative leaf the gateway mounts — never a host filesystem path: the SDK does not resolve or
/// create host paths, leaving directory/host-path resolution entirely to the gateway (which may be
/// remote) or the caller's own policy layer.
/// </summary>
public sealed class SandboxCreateRequest
{
    /// <summary>
    /// Logical workspace leaf sent as the gateway's <c>workspace</c> field. An empty string mounts
    /// the gateway's workspace root.
    /// </summary>
    public string Workspace { get; }

    /// <summary>
    /// Marketplace aliases to activate for this sandbox, defensively copied at construction. Empty
    /// means "omit the field" so the gateway applies its own default set, matching the gateway's
    /// distinction between "no marketplaces selected" (which would need an explicit empty array)
    /// and "caller expressed no preference".
    /// </summary>
    public IReadOnlyList<string> Marketplaces { get; }

    /// <summary>Auth providers to attach, defensively copied at construction. Never constructed by the SDK.</summary>
    public IReadOnlyList<SandboxAuthProvider> AuthProviders { get; }

    /// <summary>Network rules to attach, defensively copied at construction. Never constructed by the SDK.</summary>
    public IReadOnlyList<SandboxNetworkRule> NetworkRules { get; }

    /// <summary>Discovery webhook settings to attach, or <c>null</c> to omit the field entirely.</summary>
    public SandboxDiscoverySettings? Discovery { get; }

    public SandboxCreateRequest(
        string workspace,
        IReadOnlyList<string>? marketplaces = null,
        IReadOnlyList<SandboxAuthProvider>? authProviders = null,
        IReadOnlyList<SandboxNetworkRule>? networkRules = null,
        SandboxDiscoverySettings? discovery = null
    )
    {
        // Null is rejected but an EMPTY string is a valid workspace leaf (the gateway's root) — this
        // mirrors the wire field, which is a plain string, not an optional path.
        ArgumentNullException.ThrowIfNull(workspace);

        Workspace = workspace;
        Marketplaces = marketplaces is null ? [] : [.. marketplaces];
        AuthProviders = authProviders is null ? [] : [.. authProviders];
        NetworkRules = networkRules is null ? [] : [.. networkRules];
        Discovery = discovery;
    }
}
