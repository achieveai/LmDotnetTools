using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     Well-known <see cref="NotifyMessage.NotifyKind" /> values. This is an <b>open</b> set — additional
///     kinds (monitor/bash/timer/cron) are introduced by their producing features without changing LmCore.
/// </summary>
public static class NotifyKinds
{
    /// <summary>A background sub-agent finished and reported its result to the parent.</summary>
    public const string SubAgentCompletion = "subagent-completion";

    /// <summary>A context file discovered by the sandbox was injected into the conversation.</summary>
    public const string ContextDiscovery = "context-discovery";
}

/// <summary>
///     An out-of-band notification pushed into a running conversation from an asynchronous source
///     (async sub-agent completion, monitors, async bash, timers/cron). It is delivered like any other
///     input — injected into the next turn when a run is in progress, or starting a turn when the agent
///     is idle — maps to a <see cref="Role.User" /> message for the LLM whose content is a self-describing
///     envelope naming the originating tool call, and renders as a distinct notification pill (not a user
///     bubble) in the UI.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="Text" /> is a computed projection of the structured fields via the envelope builder,
///         so the envelope shown to the LLM and the structured fields read by the UI can never desync. The
///         <see cref="ShadowPropertiesJsonConverter{T}" /> writes the computed value and skips it on read
///         (get-only), recomputing from the fields — a lossless round-trip.
///     </para>
///     <para>
///         <see cref="Detail" /> is dropped verbatim into the envelope body and is treated as opaque —
///         consumers must not parse it. The only structured fields the UI/observers should key on are
///         <see cref="NotifyKind" />, <see cref="SourceToolName" />, <see cref="SourceToolCallId" /> and
///         <see cref="Label" />.
///     </para>
/// </remarks>
[JsonConverter(typeof(NotifyMessageJsonConverter))]
public record NotifyMessage : IMessage, ICanGetText
{
    /// <summary>
    ///     Discriminating kind of notification (see <see cref="NotifyKinds" />). Required — a notification
    ///     without a kind is not a valid <see cref="NotifyMessage" />; the serializer keys structural
    ///     inference on this field.
    /// </summary>
    [JsonPropertyName("notify_kind")]
    public required string NotifyKind { get; init; }

    /// <summary>
    ///     Id of the tool call this notification responds to, if any. Null for sources with no originating
    ///     tool call (timer/cron/context-discovery); the envelope then omits <c>in-response-to</c>.
    /// </summary>
    [JsonPropertyName("source_tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceToolCallId { get; init; }

    /// <summary>Name of the tool call this notification responds to, if any.</summary>
    [JsonPropertyName("source_tool_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceToolName { get; init; }

    /// <summary>Short human/UI label (e.g. sub-agent template name, discovered file path).</summary>
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }

    /// <summary>Pre-rendered payload body dropped verbatim into the envelope. Opaque — do not parse.</summary>
    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    private string? _cachedText;

    /// <summary>
    ///     The self-describing envelope the LLM reads. Computed once from the structured fields (all
    ///     <c>init</c>-only, so the value is immutable) and cached — a <see cref="NotifyMessage" /> lives in
    ///     history and is re-mapped by the active provider on every subsequent turn, so this avoids
    ///     rebuilding the envelope each access. Never set directly, so it cannot drift from the fields.
    /// </summary>
    [JsonPropertyName("text")]
    public string Text => _cachedText ??= BuildEnvelope(NotifyKind, SourceToolName, SourceToolCallId, Label, Detail);

    /// <inheritdoc />
    public string? GetText()
    {
        return Text;
    }

    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.User;

    [JsonPropertyName("fromAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    [JsonPropertyName("generationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }

    /// <summary>
    ///     Creates a notification and stamps a unique <see cref="GenerationId" /> so that multiple
    ///     notifications within a single run keep distinct client merge keys. Tests may pass an explicit
    ///     <paramref name="generationId" /> for determinism.
    /// </summary>
    public static NotifyMessage Create(
        string notifyKind,
        string? detail = null,
        string? sourceToolName = null,
        string? sourceToolCallId = null,
        string? label = null,
        string? generationId = null,
        Role role = Role.User
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notifyKind);

        return new NotifyMessage
        {
            NotifyKind = notifyKind,
            Detail = detail,
            SourceToolName = sourceToolName,
            SourceToolCallId = sourceToolCallId,
            Label = label,
            Role = role,
            GenerationId = generationId ?? $"notify:{Guid.NewGuid():N}",
        };
    }

    /// <summary>
    ///     Builds the self-describing envelope. The <c>in-response-to</c> attribute is omitted when there is
    ///     no originating tool call. Attribute values are XML-escaped and the payload body is sanitized so a
    ///     hostile payload cannot break out of / close the envelope early.
    /// </summary>
    private static string BuildEnvelope(
        string kind,
        string? sourceToolName,
        string? sourceToolCallId,
        string? label,
        string? detail
    )
    {
        var sb = new StringBuilder();
        _ = sb.Append("<notification kind=\"").Append(EscapeAttribute(kind)).Append('"');

        if (!string.IsNullOrEmpty(sourceToolCallId))
        {
            var target = string.IsNullOrEmpty(sourceToolName)
                ? sourceToolCallId
                : $"{sourceToolName}:{sourceToolCallId}";
            _ = sb.Append(" in-response-to=\"").Append(EscapeAttribute(target)).Append('"');
        }

        if (!string.IsNullOrEmpty(label))
        {
            _ = sb.Append(" label=\"").Append(EscapeAttribute(label)).Append('"');
        }

        _ = sb.Append('>');

        if (!string.IsNullOrEmpty(detail))
        {
            _ = sb.Append('\n').Append(SanitizeBody(detail)).Append('\n');
        }

        _ = sb.Append("</notification>");
        return sb.ToString();
    }

    private static string EscapeAttribute(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    /// <summary>
    ///     Neutralizes any attempt to close the envelope early from within the (otherwise raw) payload body.
    ///     The body is left intact for readability apart from the exact closing marker.
    /// </summary>
    private static string SanitizeBody(string detail)
    {
        return detail.Replace("</notification>", "&lt;/notification&gt;", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     JSON converter for <see cref="NotifyMessage" /> using the shadow-properties pattern. Mirrors
///     <c>TextMessageJsonConverter</c>; the computed get-only <see cref="NotifyMessage.Text" /> is written
///     on serialize and skipped on read (recomputed from the structured fields).
/// </summary>
public class NotifyMessageJsonConverter : ShadowPropertiesJsonConverter<NotifyMessage>
{
    protected override NotifyMessage CreateInstance()
    {
        // NotifyKind is required (see Create), but the converter fills it from the wire's notify_kind.
        // A hand-crafted/corrupt $type:"notify" payload with the field missing would deserialize to an
        // empty kind rather than throwing. We deliberately do NOT throw here: this converter also runs on
        // the unguarded history-rehydration path, so a hard failure would brick recovery of an entire
        // conversation over one bad row. The gap is unreachable via our own serializer (Create always sets
        // the field, and structural inference requires notify_kind to route here at all).
        return new NotifyMessage { NotifyKind = string.Empty };
    }
}
