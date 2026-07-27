using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

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
}
