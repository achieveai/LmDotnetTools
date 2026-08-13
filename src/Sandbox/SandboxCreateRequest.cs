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

    /// <summary>
    /// Explicit plugin selection for this sandbox, defensively copied at construction. Tri-state:
    /// <c>null</c> = the caller expressed no preference (legacy "all plugins"), an empty list =
    /// explicitly no plugins, and a non-empty list = an explicit subset. Unlike
    /// <see cref="Marketplaces"/>, <c>null</c> is NEVER collapsed to an empty list.
    /// </summary>
    public IReadOnlyList<SandboxPluginRef>? PluginSelection { get; }

    public SandboxCreateRequest(
        string workspace,
        IReadOnlyList<string>? marketplaces = null,
        IReadOnlyList<SandboxAuthProvider>? authProviders = null,
        IReadOnlyList<SandboxNetworkRule>? networkRules = null,
        SandboxDiscoverySettings? discovery = null,
        IReadOnlyList<SandboxPluginRef>? pluginSelection = null
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
        // Unlike Marketplaces/AuthProviders/NetworkRules, null and [] are semantically different here
        // (tri-state plugin selection): null must stay null, not collapse to an empty list.
        PluginSelection = pluginSelection is null ? null : [.. pluginSelection];
    }
}
