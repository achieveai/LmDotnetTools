namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Reports how the gateway resolved a sandbox's requested plugin selection (spec Section 5.3).
/// <see cref="Supported"/> is false when the gateway accepted the create call but does not support
/// plugin filtering; callers must not infer capability from an absent <see cref="SandboxInfo.PluginResolution"/> —
/// that means the gateway is old enough to not report resolution at all, a stronger "unknown" signal.
/// </summary>
public sealed class SandboxPluginResolution
{
    public SandboxPluginResolution(
        bool supported,
        IReadOnlyList<SandboxPluginRef>? requested = null,
        IReadOnlyList<SandboxPluginRef>? effective = null,
        IReadOnlyList<SandboxPluginRef>? failed = null
    )
    {
        Supported = supported;
        // Unlike Effective/Failed, Requested is tri-state and must not collapse null to []:
        // null means the wire request field was absent/explicit-null (legacy "all plugins").
        Requested = requested is null ? null : [.. requested];
        Effective = effective is null ? [] : [.. effective];
        Failed = failed is null ? [] : [.. failed];
    }

    /// <summary>Whether the gateway supports plugin filtering at all.</summary>
    public bool Supported { get; }

    /// <summary>The selection the gateway saw on the request; <c>null</c> means "no explicit selection".</summary>
    public IReadOnlyList<SandboxPluginRef>? Requested { get; }

    /// <summary>The plugins the gateway actually activated.</summary>
    public IReadOnlyList<SandboxPluginRef> Effective { get; }

    /// <summary>The requested plugins the gateway could not activate.</summary>
    public IReadOnlyList<SandboxPluginRef> Failed { get; }
}
