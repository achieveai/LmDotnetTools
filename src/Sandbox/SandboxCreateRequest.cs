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
    /// The sandbox working directory (gateway ADR 0044, formerly "home"): a subdirectory of the workspace,
    /// relative and <c>/</c>-separated, that becomes the default directory for relative paths and shells.
    /// It is <b>the only place agents can write</b>: the rest of the workspace becomes read-only for the MCP
    /// tools and shells, while the harness surfaces (direct file API, operations) still write everywhere.
    /// Operations started without a working directory run at the workspace root, not here. Created on
    /// demand and immutable for the session. <c>null</c> sets none, leaving the whole workspace writable.
    /// <see cref="SandboxClient.CreateAsync"/> requires the gateway to acknowledge exactly this directory (in
    /// its normalized form) and fails otherwise; see <see cref="SandboxInfo.WorkspaceWorkingDir"/>.
    /// </summary>
    public string? WorkingDir { get; }

    /// <summary>
    /// The sandbox's property bag (gateway ADR 0042), defensively copied; empty omits the field. Keys in the
    /// app's registry <c>partition</c> list are REQUIRED (<c>400 missing_partition_property</c>), become workspace
    /// directory levels, and are immutable; the rest change later with <see cref="SandboxClient.UpdatePropertiesAsync"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; }

    /// <summary>The unified sandbox webhook (gateway PR #205), or <c>null</c> to omit the field.</summary>
    public SandboxWebhookSettings? Webhook { get; }

    /// <summary>Skill tiers for every agent of the sandbox (gateway ADR 0041), or <c>null</c> to omit the field.</summary>
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
        string? workingDir = null,
        IReadOnlyDictionary<string, string>? properties = null,
        SandboxWebhookSettings? webhook = null,
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
        // otherwise reject an empty working directory (it can never name the root) or resolve an empty image oddly.
        if (image is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(image);
        }

        if (workingDir is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workingDir);
        }

        Image = image;
        WorkingDir = workingDir;
        Properties = properties is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(properties, StringComparer.Ordinal);
        Webhook = webhook;
        Skills = skills;
        Env = env is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(env, StringComparer.Ordinal);
    }
}
