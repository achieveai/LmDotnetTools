namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// A sandbox the gateway is tracking, as returned by <see cref="SandboxClient.CreateAsync"/>,
/// <see cref="SandboxClient.GetAsync"/>, and <see cref="SandboxClient.ListAsync"/>.
/// </summary>
public sealed class SandboxInfo
{
    /// <summary>Gateway session id; the value sent as the <c>X-Session-ID</c> header on session-scoped calls.</summary>
    public string SessionId { get; }

    /// <summary>Gateway-allocated container id backing the sandbox, when the gateway reports one.</summary>
    public string? ContainerId { get; }

    /// <summary>
    /// The workspace path INSIDE the sandbox container, when the gateway reports one. This is a
    /// remote path meaningful to the gateway/container, never a local host path — the SDK never
    /// resolves or creates host-filesystem paths.
    /// </summary>
    public string? WorkspaceContainerPath { get; }

    /// <summary>Gateway-reported logical workspace identity, never a host filesystem path.</summary>
    public string? WorkspaceRelativePath { get; }

    /// <summary>
    /// The persisted workspace mount id (<c>session_mounts.id</c>) the gateway's direct file/command
    /// APIs are keyed by, when the gateway reports one. Present on a create/get result from a
    /// #119-inclusive gateway; <c>null</c> on a <see cref="SandboxClient.ListAsync"/> result (the list
    /// response carries no volumes) or against a pre-#119 gateway. Callers rarely need this directly —
    /// the SDK resolves it internally per session — but it is surfaced so a caller already holding a
    /// create result can avoid a redundant lookup.
    /// </summary>
    public long? WorkspaceMountId { get; }

    /// <summary>
    /// The gateway's own status word for the session at the moment it answered, when it reported
    /// one. Open vocabulary, and empty against a gateway that reports none — the SDK never invents a
    /// value, because a synthesized status would be indistinguishable from a reported one.
    /// </summary>
    public string Status { get; }

    /// <summary>
    /// What the gateway confirmed it loaded into the session. Never <see langword="null"/>: against
    /// a gateway that does not report an inventory this is a
    /// <see cref="SandboxInventoryStatuses.Unavailable"/> value carrying the reason, so a caller
    /// always gets an answer rather than having to distinguish "no items" from "no field".
    /// </summary>
    public SandboxInventory Inventory { get; }

    /// <summary>
    /// How the gateway resolved this sandbox's requested plugin selection, or <see langword="null"/>
    /// when the gateway did not report one. Unlike <see cref="Inventory"/> this is NEVER defaulted:
    /// <see langword="null"/> is a distinct, stronger "capability unknown" signal than a resolution
    /// whose <see cref="SandboxPluginResolution.Supported"/> flag is false.
    /// </summary>
    public SandboxPluginResolution? PluginResolution { get; }

    /// <summary>
    /// The image the sandbox was created from after the gateway resolved it (request, then app default,
    /// then server default; ADR 0039). Reported on a <see cref="SandboxClient.CreateAsync"/> result and, from
    /// gateways that report it on GET, on a <see cref="SandboxClient.GetAsync"/> result (a session recorded
    /// without an image reports the server default there). <see langword="null"/> on list results, on GET
    /// from a gateway that does not report it, and against a gateway that predates client images.
    /// </summary>
    public string? Image { get; }

    /// <summary>
    /// The registry digest reference (<c>repo@sha256:…</c>) the session is pinned to, from a create result or a
    /// GET that reports it. <see langword="null"/> for a locally built image, on list results, and on older gateways.
    /// </summary>
    public string? ImageDigest { get; }

    /// <summary>
    /// The runtime's conformance report on the image, with its non-fatal warnings, from a create result.
    /// <see langword="null"/> under the gateway's legacy image contract, on GET/list results, and on older gateways.
    /// </summary>
    public SandboxImageReport? ImageReport { get; }

    /// <summary>
    /// The sandbox home the gateway persisted for the workspace mount (normalized, relative to the
    /// workspace), reported on create and GET results. <see langword="null"/> when the home is the
    /// workspace root, and on a list result.
    /// </summary>
    public string? WorkspaceHome { get; }

    /// <summary>
    /// The skill-tier policy the gateway resolved for the sandbox (ADR 0041), echoed on a
    /// <see cref="SandboxClient.CreateAsync"/> result whether or not the request carried
    /// <see cref="SandboxCreateRequest.Skills"/>: its <see cref="SandboxSkillsPolicy.DefaultTier"/> is always
    /// set and its <see cref="SandboxSkillsPolicy.Included"/> holds the resolved skill keys (directory paths),
    /// not the refs the request sent. <see langword="null"/> when the gateway did not report one (a gateway
    /// that predates skill tiers, and list results).
    /// </summary>
    public SandboxSkillsPolicy? Skills { get; }

    public SandboxInfo(
        string sessionId,
        string? containerId = null,
        string? workspaceContainerPath = null,
        long? workspaceMountId = null,
        string? status = null,
        SandboxInventory? inventory = null,
        SandboxPluginResolution? pluginResolution = null,
        string? workspaceRelativePath = null,
        string? image = null,
        string? imageDigest = null,
        SandboxImageReport? imageReport = null,
        string? workspaceHome = null,
        SandboxSkillsPolicy? skills = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        ContainerId = containerId;
        WorkspaceContainerPath = workspaceContainerPath;
        WorkspaceRelativePath = workspaceRelativePath;
        WorkspaceMountId = workspaceMountId;
        Status = status ?? string.Empty;
        // A result that carries no inventory at all — a create against a gateway that predates the
        // field, or any non-create result (ListAsync never reports one) — still answers the
        // question, so a caller never has to distinguish "no items" from "no field".
        Inventory = inventory ?? SandboxInventory.Unavailable(SandboxInventory.NoInventoryReported);
        PluginResolution = pluginResolution;
        Image = image;
        ImageDigest = imageDigest;
        ImageReport = imageReport;
        WorkspaceHome = workspaceHome;
        Skills = skills;
    }
}
