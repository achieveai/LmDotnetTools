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
    /// and "caller expressed no preference". Since gateway ADR 0045 that default is the operator set PLUS every
    /// git marketplace the app registered (<see cref="SandboxClient.RegisterAppMarketplaceAsync"/>).
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

    /// <summary>Read-only plugin directory mounts. Empty by default; omitted from the create request.</summary>
    public IReadOnlyList<SandboxPluginMount> PluginMounts { get; }

    /// <summary>
    /// Per-sandbox environment variables to set at create time, defensively copied at construction.
    /// Never <c>null</c>; an empty map omits the wire field entirely (see <c>ToWireDto</c>).
    /// </summary>
    public IReadOnlyDictionary<string, string> Env { get; }

    /// <summary>
    /// The sandbox base image (ADR 0039): a reference such as <c>debian:bookworm-slim</c> or a pinned
    /// <c>repo@sha256:…</c> digest. <c>null</c> omits the field so the gateway applies the app's default
    /// image, else its own. The gateway's allow-list decides what is accepted; a refusal surfaces as
    /// <see cref="SandboxErrorKind.ImageRejected"/>. The acknowledged image comes back on
    /// <see cref="SandboxInfo.Image"/>.
    /// </summary>
    public string? Image { get; }

    /// <summary>
    /// The sandbox home (gateway issue #142): a subdirectory of the workspace, relative and
    /// <c>/</c>-separated, that becomes the default directory for relative paths, shells and operations.
    /// Created on demand and immutable for the session. <c>null</c> keeps the home at the workspace root.
    /// <see cref="SandboxClient.CreateAsync"/> requires the gateway to acknowledge exactly this home (in its
    /// normalized form) and fails otherwise; see <see cref="SandboxInfo.WorkspaceHome"/>.
    /// </summary>
    public string? Home { get; }

    /// <summary>
    /// The sandbox's skill-tier policy (gateway ADR 0041), sent as the create request's <c>skills</c> block.
    /// <c>null</c> omits the field, so the request is byte-identical to one built before this property
    /// existed and the gateway applies its own default (every admitted skill included). The resolved
    /// policy comes back on <see cref="SandboxInfo.Skills"/>; a ref that does not resolve fails the create
    /// with <see cref="SandboxErrorKind.SkillRejected"/>.
    /// </summary>
    public SandboxSkillsPolicy? Skills { get; }

    public SandboxCreateRequest(
        string workspace,
        IReadOnlyList<string>? marketplaces = null,
        IReadOnlyList<SandboxAuthProvider>? authProviders = null,
        IReadOnlyList<SandboxNetworkRule>? networkRules = null,
        SandboxDiscoverySettings? discovery = null,
        IReadOnlyList<SandboxPluginRef>? pluginSelection = null,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyList<SandboxPluginMount>? pluginMounts = null,
        string? image = null,
        string? home = null,
        SandboxSkillsPolicy? skills = null
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
        PluginMounts = pluginMounts is null ? [] : [.. pluginMounts];
        // Null means "not requested"; a present value must say something, because the gateway would
        // otherwise reject an empty home (it can never name the root) or resolve an empty image oddly.
        if (image is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(image);
        }

        if (home is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(home);
        }

        Image = image;
        Home = home;
        Skills = skills;
        Env = env is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(env, StringComparer.Ordinal);
    }
}
