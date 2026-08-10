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
    /// Optional home directory for the sandbox, expressed RELATIVE to <see cref="Workspace"/> and sent
    /// as the gateway's <c>home</c> field; <c>null</c> omits it so the gateway's own default (the
    /// workspace root) applies. The gateway creates the directory if missing, surfaces it to the agent
    /// as <c>SANDBOX_HOME</c>, and starts operations there — so this is how a caller that mounts ONE
    /// directory for several concurrent consumers gives each its own corner of it.
    /// <para>
    /// Normalized to forward slashes with no leading/trailing separator, and rejected outright if any
    /// segment is <c>..</c> or the value is rooted: a home that escapes the mount is the one input here
    /// that could hand an agent a directory outside the workspace it was scoped to. The gateway enforces
    /// this too; failing in the SDK turns a remote 400 into a precise local argument error.
    /// </para>
    /// </summary>
    public string? Home { get; }

    public SandboxCreateRequest(
        string workspace,
        IReadOnlyList<string>? marketplaces = null,
        IReadOnlyList<SandboxAuthProvider>? authProviders = null,
        IReadOnlyList<SandboxNetworkRule>? networkRules = null,
        SandboxDiscoverySettings? discovery = null,
        string? home = null
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
        Home = NormalizeHome(home);
    }

    /// <summary>
    /// Canonicalizes a workspace-relative home to the gateway's spelling, or <c>null</c> when the caller
    /// expressed no preference. Blank is treated as absent rather than as "the workspace root": the two
    /// mean the same thing to the gateway, and omitting the field keeps the request byte-identical to one
    /// from a caller that predates this option.
    /// </summary>
    private static string? NormalizeHome(string? home)
    {
        if (string.IsNullOrWhiteSpace(home))
        {
            return null;
        }

        var value = home.Trim().Replace('\\', '/');
        if (value.StartsWith('/') || (value.Length > 1 && value[1] == ':'))
        {
            throw new ArgumentException(
                $"The sandbox home must be relative to the workspace, but '{home}' is rooted.",
                nameof(home)
            );
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Array.Exists(segments, s => s == ".."))
        {
            throw new ArgumentException(
                $"The sandbox home must stay inside the workspace, but '{home}' traverses above it.",
                nameof(home)
            );
        }

        // Drop no-op '.' segments so 'a/./b' and 'a/b' produce the same wire value.
        var cleaned = string.Join('/', Array.FindAll(segments, s => s != "."));
        return cleaned.Length == 0 ? null : cleaned;
    }
}
