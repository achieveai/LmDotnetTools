using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

/// <summary>
/// What the gateway confirmed it loaded into a sandbox, reported with the create result.
/// </summary>
/// <remarks>
/// <para>
/// Confirmed means loaded, not requested and not available. A create request naming three
/// marketplaces may load none of them, and a marketplace catalog describes what a session
/// <i>could</i> load. Neither may be reported here: a subscriber acting on this event treats it as a
/// statement about the session that now exists.
/// </para>
/// <para>
/// Reporting is fail-closed. <see cref="Status"/> is
/// <see cref="LifecycleInventoryStatuses.Confirmed"/> only when the gateway said so; otherwise it is
/// <see cref="LifecycleInventoryStatuses.Unavailable"/> with a reason and no items, so an absent
/// inventory is always distinguishable from an empty one.
/// </para>
/// </remarks>
public sealed record SandboxInventorySummary
{
    /// <summary>
    /// Whether <see cref="Items"/> can be read as what the session loaded. See
    /// <see cref="LifecycleInventoryStatuses"/>. Open vocabulary.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = LifecycleInventoryStatuses.Unavailable;

    /// <summary>
    /// Why the inventory could not be confirmed. Present whenever <see cref="Status"/> is not
    /// <see cref="LifecycleInventoryStatuses.Confirmed"/>, so "unavailable" is never a bare
    /// assertion.
    /// </summary>
    [JsonPropertyName("unavailable_reason")]
    public string? UnavailableReason { get; set; }

    /// <summary>
    /// The confirmed items. Empty — written as <c>[]</c>, never absent — when the status is not
    /// confirmed.
    /// </summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<SandboxInventoryEntry> Items { get; set; } = [];
}

/// <summary>
/// One plugin, skill, or sub-agent confirmed loaded in a sandbox.
/// </summary>
/// <remarks>
/// Identity and version only. Manifests, descriptions, install paths, source repositories, and
/// publisher metadata are deliberately excluded: the event stream has a different audience from the
/// sandbox, and knowing <i>what</i> is loaded never requires shipping the content of it.
/// </remarks>
public sealed record SandboxInventoryEntry
{
    /// <summary>
    /// What kind of thing this is. See <see cref="LifecycleInventoryKinds"/>. Open vocabulary.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>The gateway's identifier for the item, unique within its <see cref="Kind"/>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>The loaded version, when the gateway tracks one for this kind.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>
/// Payload for <see cref="LifecycleEventTypes.SandboxCreated"/>.
/// </summary>
/// <remarks>
/// <para>
/// Emitted only after the create or recreate has been committed — never at the point the request is
/// issued. A create that is later rolled back must not leave a subscriber believing a session
/// exists, so the event follows the commit rather than preceding it.
/// </para>
/// <para>
/// The payload carries identifiers and status only. Session secrets, gateway credentials, and
/// authorization headers are never included, and the event is emitted outside the registry's locks
/// so that a slow subscriber cannot stall session creation.
/// </para>
/// </remarks>
public sealed record SandboxCreatedPayload
{
    /// <summary>The committed session.</summary>
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>The workspace the session belongs to.</summary>
    [JsonPropertyName("workspace_id")]
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>
    /// Whether this replaced an earlier session for the same workspace rather than being the
    /// first.
    /// </summary>
    [JsonPropertyName("was_recreated")]
    public bool WasRecreated { get; set; }

    /// <summary>
    /// The session this one replaced, when <see cref="WasRecreated"/> is <see langword="true"/>.
    /// </summary>
    [JsonPropertyName("replaced_session_id")]
    public string? ReplacedSessionId { get; set; }

    /// <summary>The gateway-reported session status at commit time. Open vocabulary.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// The image the session was created from, when the host chooses to report it. Absent by
    /// default.
    /// </summary>
    [JsonPropertyName("image_reference")]
    public string? ImageReference { get; set; }

    /// <summary>
    /// What the gateway confirmed it loaded into the session. Always written — a gateway that
    /// reports nothing yields an <see cref="LifecycleInventoryStatuses.Unavailable"/> summary
    /// carrying the reason, never an absent field, so a subscriber cannot mistake an old gateway for
    /// an empty session.
    /// </summary>
    [JsonPropertyName("inventory")]
    public SandboxInventorySummary Inventory { get; set; } = new();
}
