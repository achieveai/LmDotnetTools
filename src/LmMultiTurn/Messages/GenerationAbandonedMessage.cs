using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Non-terminal, content-free control telling subscribers that a generation was abandoned mid-stream
/// after a recoverable provider-transport failure: every block of that generation which is still
/// unfinalized must be discarded, because the replacement work arrives under a new generation id and
/// would otherwise render alongside the orphaned partial. Canonical messages already delivered whole
/// under the abandoned generation are unaffected and stay.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT <see cref="StreamRecoveryMessage"/>. That control is terminal — it means "your
/// stream ended, reconnect" and consumers act on it by tearing the connection down. Here the run is
/// alive and continuing on the same stream, so reusing that type would disconnect every client on
/// every transient blip.
/// </para>
/// <para>
/// Carries only identifiers — no prompt, tool, or transcript content — so it is safe to log or
/// forward to a client verbatim (WebSocket discriminator: <c>generation_abandoned</c>).
/// </para>
/// <para>
/// It is never added to conversation history, so it is never persisted and never reappears when a
/// conversation is loaded from the store. It <i>is</i>, however, replayable: resilient stream
/// delivery classifies it as canonical/control (see
/// <see cref="Delivery.ReplayMessagePolicy.IsCanonicalOrControl" />), so a consumer that
/// resynchronizes mid-run receives it again — which is the correct behaviour, since a consumer that
/// missed it the first time still holds the orphaned partial. Handlers must therefore be idempotent:
/// discarding the unfinalized blocks of a generation that has none left is a no-op, and the
/// canonical blocks a second delivery must not touch are exactly the ones it already leaves alone.
/// </para>
/// </remarks>
public sealed record GenerationAbandonedMessage(
    [property: JsonPropertyName("threadId")] string? ThreadId,
    [property: JsonPropertyName("runId")] string? RunId,
    [property: JsonPropertyName("generationId")] string? GenerationId
) : IMessage
{
    // IMessage-only members: not part of this message's content-free wire contract (see class
    // summary). JsonIgnore keeps them out of serialization regardless of which JsonSerializerOptions
    // a caller uses, rather than relying on a specific options instance's ignore-null configuration.
    [JsonIgnore]
    public Role Role => Role.System;

    [JsonIgnore]
    public string? FromAgent { get; init; }

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }
}
