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
    /// The working directory the gateway persisted for the workspace mount (normalized, relative to the
    /// workspace; <c>volumes.workspace.working_dir</c>), reported on create and GET results. When set, the
    /// rest of the workspace is read-only for agents (ADR 0044). <see langword="null"/> when none is set,
    /// on a list result, and from a gateway that predates ADR 0044 (it reports only the legacy <c>home</c>,
    /// which this SDK deliberately ignores because such a gateway does not make the workspace read-only).
    /// </summary>
    public string? WorkspaceWorkingDir { get; }

    /// <summary>
    /// The sandbox's property bag (ADR 0042) from a create or GET result. <see langword="null"/> on a list result
    /// and from a gateway that predates properties; empty when the gateway reports none.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Properties { get; }

    /// <summary>The partition keys the sandbox was created under (ADR 0042); empty for an unpartitioned app or when not reported.</summary>
    public IReadOnlyList<string> Partition { get; }

    /// <summary>
    /// The webhook the sandbox registered, from a GET result (gateway PR #205). <see langword="null"/> when none is
    /// registered, on create and list results, and from an older gateway.
    /// </summary>
    public SandboxWebhookInfo? Webhook { get; }

    /// <summary>
    /// The resolved skill-tier policy (ADR 0041) from a create result; its <see cref="SandboxSkillsPolicy.Included"/>
    /// holds gateway skill keys. <see langword="null"/> on GET and list results and from an older gateway.
    /// </summary>
    public SandboxSkillsPolicy? SkillsPolicy { get; }

    /// <summary>Resolved MCP state for this response; null means no readable acknowledgment is available
    /// (omitted, null or uninterpretable echo). A present empty server list is readable acknowledgment.
    /// Current gateways may report it even when the request omitted MCP. Values may contain sensitive configuration.</summary>
    public SandboxMcpResolvedPolicy? Mcp { get; init; }

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
        string? workspaceWorkingDir = null,
        IReadOnlyDictionary<string, string>? properties = null,
        IReadOnlyList<string>? partition = null,
        SandboxWebhookInfo? webhook = null,
        SandboxSkillsPolicy? skillsPolicy = null
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
        WorkspaceWorkingDir = workspaceWorkingDir;
        Properties = properties is null ? null : new Dictionary<string, string>(properties, StringComparer.Ordinal);
        Partition = partition is null ? [] : [.. partition];
        Webhook = webhook;
        SkillsPolicy = skillsPolicy;
    }
}
