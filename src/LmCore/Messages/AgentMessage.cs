using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     What one agent is saying to another. A <b>closed</b> set — the receiving agent is told, in the
///     envelope, which of these it is looking at and whether an answer is expected, so adding a member
///     changes a contract the model reads.
/// </summary>
/// <remarks>
///     The attribute-scoped converter pins the wire shape to the member names. Persisted history and
///     the envelope must agree on what a message type is called, and an ordinal would make both
///     unreadable and reorder-fragile.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentMessageType
{
    /// <summary>Asks for an answer and stays open until a <see cref="Response"/> closes it.</summary>
    Question,

    /// <summary>Hands work down and stays open until a <see cref="Response"/> closes it.</summary>
    DelegateTask,

    /// <summary>Reports progress on delegated work. Closes nothing and expects no reply.</summary>
    TaskUpdate,

    /// <summary>Redirects an agent already doing something. Expects no reply.</summary>
    Steer,

    /// <summary>Answers, and closes, an open <see cref="Question"/> or <see cref="DelegateTask"/>.</summary>
    Response,
}

/// <summary>
///     A message sent from one agent to another inside a collaboration, delivered to the receiver as a
///     self-describing envelope naming the sender, the message type, and — when the type expects an
///     answer — exactly how to reply.
/// </summary>
/// <remarks>
///     <para>
///         This is a first-class <see cref="IMessage"/> rather than a formatted string for the same
///         reason <see cref="NotifyMessage"/> is: the envelope the LLM reads and the structured fields
///         the UI, ledger, and persistence read must not be able to disagree. <see cref="Text"/> is a
///         computed projection of the fields, so there is no writable seam through which they could.
///     </para>
///     <para>
///         Sender identity and correlation are stamped by the trusted collaboration layer, never by the
///         sending model. <see cref="Body"/> is the only model-authored part, and it is sanitized so it
///         can neither close the envelope early nor forge a reply instruction — a forged instruction
///         would let a sender redirect the receiver's answer to a third party.
///     </para>
/// </remarks>
[JsonConverter(typeof(AgentMessageJsonConverter))]
public record AgentMessage : IMessage, ICanGetText
{
    /// <summary>Element name of the envelope, and of the marker a hostile body may not close.</summary>
    private const string EnvelopeElement = "agent-message";

    /// <summary>Element name of the appended reply instruction, which a hostile body may not forge.</summary>
    private const string ReplyInstructionElement = "reply-instruction";

    /// <summary>Tool the receiver is told to reply with. Kept here so the envelope names one name.</summary>
    private const string ReplyToolName = "SendMessage";

    /// <summary>
    ///     Trusted, collaboration-minted identifier for this message. Required — it is the correlation
    ///     key a reply carries back, and the key the serializer keys structural inference on.
    /// </summary>
    [JsonPropertyName("message_id")]
    public required string MessageId { get; init; }

    /// <summary>
    ///     What this message is. Required, and load-bearing: it decides whether a reply instruction is
    ///     appended at all.
    /// </summary>
    [JsonPropertyName("agent_message_type")]
    public required AgentMessageType AgentMessageType { get; init; }

    /// <summary>
    ///     Canonical, stable identifier of the sending agent. Required — this, not
    ///     <see cref="FromName"/>, is what a reply is addressed to, because names can collide.
    /// </summary>
    [JsonPropertyName("from_agent_id")]
    public required string FromAgentId { get; init; }

    /// <summary>
    ///     Human-facing name of the sending agent, for the receiver's benefit and the UI's. Required so
    ///     an envelope never presents an anonymous sender.
    /// </summary>
    [JsonPropertyName("from_name")]
    public required string FromName { get; init; }

    /// <summary>
    ///     Identifier of the message this one answers, when it answers one. Null for messages that open
    ///     a conversation rather than continue it; the envelope then omits <c>in-response-to</c>.
    /// </summary>
    [JsonPropertyName("in_response_to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InResponseTo { get; init; }

    /// <summary>
    ///     The model-authored payload, dropped into the envelope body after sanitization. Opaque —
    ///     consumers must not parse it.
    /// </summary>
    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; init; }

    private string? _cachedText;

    /// <summary>
    ///     The self-describing envelope the receiving LLM reads. Computed once from the structured
    ///     fields (all <c>init</c>-only, so the value is immutable) and cached, because an agent message
    ///     lives in history and is re-mapped by the active provider on every subsequent turn. Never set
    ///     directly, so it cannot drift from the fields.
    /// </summary>
    [JsonPropertyName("text")]
    public string Text => _cachedText ??= BuildEnvelope(this);

    /// <inheritdoc />
    public string? GetText()
    {
        return Text;
    }

    /// <summary>
    ///     Whether this message type asks for an answer, and therefore carries a reply instruction.
    /// </summary>
    [JsonIgnore]
    public bool ExpectsReply =>
        AgentMessageType is AgentMessageType.Question or AgentMessageType.DelegateTask;

    /// <inheritdoc />
    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.User;

    /// <inheritdoc />
    [JsonPropertyName("fromAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    /// <inheritdoc />
    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <inheritdoc />
    [JsonPropertyName("generationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    /// <inheritdoc />
    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    /// <inheritdoc />
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <inheritdoc />
    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    /// <inheritdoc />
    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }

    /// <summary>
    ///     Creates an agent message and stamps a unique <see cref="GenerationId"/> so that several
    ///     agent messages within a single run keep distinct client merge keys. Tests may pass an
    ///     explicit <paramref name="generationId"/> for determinism.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     An identity field is blank. Identity is stamped by the trusted layer, so a blank one is a
    ///     caller bug rather than untrusted input, and failing loudly is correct.
    /// </exception>
    public static AgentMessage Create(
        string messageId,
        AgentMessageType agentMessageType,
        string fromAgentId,
        string fromName,
        string? body = null,
        string? inResponseTo = null,
        string? generationId = null,
        Role role = Role.User
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fromName);

        return new AgentMessage
        {
            MessageId = messageId,
            AgentMessageType = agentMessageType,
            FromAgentId = fromAgentId,
            FromName = fromName,
            Body = body,
            InResponseTo = inResponseTo,
            Role = role,
            GenerationId = generationId ?? $"agentmsg:{Guid.NewGuid():N}",
        };
    }

    /// <summary>
    ///     Builds the envelope. Attribute values are XML-escaped and the body is sanitized, so a hostile
    ///     payload can neither close the envelope early nor emit a reply instruction of its own.
    /// </summary>
    private static string BuildEnvelope(AgentMessage message)
    {
        var sb = new StringBuilder();
        _ = sb.Append('<')
            .Append(EnvelopeElement)
            .Append(" message-id=\"")
            .Append(EscapeAttribute(message.MessageId))
            .Append("\" from=\"")
            .Append(EscapeAttribute(message.FromName))
            .Append("\" from-agent-id=\"")
            .Append(EscapeAttribute(message.FromAgentId))
            .Append("\" type=\"")
            .Append(EscapeAttribute(message.AgentMessageType.ToString()))
            .Append('"');

        if (!string.IsNullOrEmpty(message.InResponseTo))
        {
            _ = sb.Append(" in-response-to=\"")
                .Append(EscapeAttribute(message.InResponseTo))
                .Append('"');
        }

        _ = sb.Append('>');

        if (!string.IsNullOrEmpty(message.Body))
        {
            _ = sb.Append('\n').Append(SanitizeBody(message.Body)).Append('\n');
        }

        AppendReplyInstruction(sb, message);

        _ = sb.Append("</").Append(EnvelopeElement).Append('>');
        return sb.ToString();
    }

    /// <summary>
    ///     Appends the reply instruction for the two types that expect an answer, and nothing at all for
    ///     the three that do not. A <see cref="AgentMessageType.Steer"/>,
    ///     <see cref="AgentMessageType.TaskUpdate"/>, or <see cref="AgentMessageType.Response"/> that
    ///     carried one would invite an unbounded ping-pong between two agents.
    /// </summary>
    private static void AppendReplyInstruction(StringBuilder sb, AgentMessage message)
    {
        if (!message.ExpectsReply)
        {
            return;
        }

        _ = sb.Append('\n')
            .Append('<')
            .Append(ReplyInstructionElement)
            .Append(" in-reply-to=\"")
            .Append(EscapeAttribute(message.MessageId))
            .Append("\" reply-to=\"")
            .Append(EscapeAttribute(message.FromAgentId))
            .Append("\" reply-label=\"")
            .Append(EscapeAttribute(message.FromName))
            .Append("\" reply-tool-name=\"")
            .Append(ReplyToolName)
            .Append('"');

        // A question wants one answer; a delegated task wants progress along the way and one final
        // answer, so it names both types rather than only the closing one.
        _ =
            message.AgentMessageType == AgentMessageType.DelegateTask
                ? sb.Append(" progress-msg-type=\"")
                    .Append(nameof(AgentMessageType.TaskUpdate))
                    .Append("\" final-msg-type=\"")
                    .Append(nameof(AgentMessageType.Response))
                    .Append('"')
                : sb.Append(" reply-msg-type=\"")
                    .Append(nameof(AgentMessageType.Response))
                    .Append('"');

        _ = sb.Append("/>\n");
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
    ///     Neutralizes the two structural markers a body must never produce: the envelope's closing tag,
    ///     and the opening of a reply instruction. Everything else is left intact for readability —
    ///     escaping the whole body would make ordinary code and markup unreadable to the receiver for no
    ///     security gain, since only these two markers are interpreted.
    /// </summary>
    private static string SanitizeBody(string body)
    {
        return body.Replace(
                $"</{EnvelopeElement}>",
                $"&lt;/{EnvelopeElement}&gt;",
                StringComparison.OrdinalIgnoreCase
            )
            .Replace(
                $"<{ReplyInstructionElement}",
                $"&lt;{ReplyInstructionElement}",
                StringComparison.OrdinalIgnoreCase
            );
    }
}

/// <summary>
///     JSON converter for <see cref="AgentMessage"/> using the shadow-properties pattern. Mirrors
///     <see cref="NotifyMessageJsonConverter"/>; the computed get-only <see cref="AgentMessage.Text"/>
///     is written on serialize and skipped on read, so a stale persisted envelope can never win over the
///     structured fields it was built from.
/// </summary>
public class AgentMessageJsonConverter : ShadowPropertiesJsonConverter<AgentMessage>
{
    /// <inheritdoc />
    protected override AgentMessage CreateInstance()
    {
        // The required members are filled from the wire by the base reader. A hand-crafted or corrupt
        // payload missing them deserializes to empty identity rather than throwing: this converter also
        // runs on the unguarded history-rehydration path, where a hard failure would brick recovery of a
        // whole conversation over one bad row.
        return new AgentMessage
        {
            MessageId = string.Empty,
            AgentMessageType = AgentMessageType.Response,
            FromAgentId = string.Empty,
            FromName = string.Empty,
        };
    }
}
