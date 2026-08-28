using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Reason a subscriber was signalled to resynchronize. Whether the signal is TERMINAL for the
/// stream is a property of the reason, not of this message: see each member.
/// </summary>
// Type-level converter (mirrors Role.cs's own convention) so this enum serializes correctly with
// ANY JsonSerializerOptions - including a bare JsonSerializerOptionsFactory.CreateForProduction() -
// rather than depending on a caller (e.g. ChatWebSocketManager) remembering to register the
// converter on its own private options.
[JsonConverter(typeof(JsonPropertyNameEnumConverter<StreamRecoveryReason>))]
public enum StreamRecoveryReason
{
    /// <summary>
    /// The subscriber's bounded output channel filled (it could not keep up with the live run) and
    /// was dropped from fan-out so the run and every other subscriber remain unblocked. TERMINAL:
    /// this subscriber receives nothing further and must reconnect. See
    /// <see cref="MultiTurnAgentBase.PublishToSubscriber"/>.
    /// </summary>
    [JsonPropertyName("slow_consumer")]
    SlowConsumer,

    /// <summary>
    /// The in-flight run's replay buffer hit its count/byte cap, so the buffered prefix no longer
    /// covers the whole run. Replaying it would hand a joining subscriber a silently incomplete
    /// stream, so the buffer is withheld and this is issued instead — the client reloads
    /// authoritative history. NOT terminal: only the run's already-published PREFIX is missing, so
    /// this LEADS the stream and the live tail follows on the same subscription. A consumer that
    /// tore the stream down here would have to reconnect, land on the same still-truncated buffer,
    /// and be advised again for the rest of the run. See
    /// <see cref="MultiTurnAgentBase.SubscribeAsync"/>.
    /// </summary>
    [JsonPropertyName("replay_truncated")]
    ReplayTruncated,
}

/// <summary>
/// Content-free control telling a subscriber to resynchronize from authoritative history. Lets the
/// client distinguish "your view of this run has a hole in it" from an ordinary
/// <see cref="RunCompletedMessage"/> or a clean unsubscribe. <see cref="Reason"/> says whether the
/// stream also ENDS here (<see cref="StreamRecoveryReason.SlowConsumer"/>) or continues with the
/// live tail (<see cref="StreamRecoveryReason.ReplayTruncated"/>). Carries only identifiers
/// (thread/run/generation) and the reason — no prompt, tool, or transcript content — so it is always
/// safe to log or forward to a client verbatim (WebSocket discriminator: <c>stream_recovery</c>).
/// </summary>
public sealed record StreamRecoveryMessage(
    [property: JsonPropertyName("threadId")] string? ThreadId,
    [property: JsonPropertyName("runId")] string? RunId,
    [property: JsonPropertyName("generationId")] string? GenerationId,
    [property: JsonPropertyName("reason")] StreamRecoveryReason Reason
) : IMessage
{
    // IMessage-only members: not part of this message's content-free wire contract (see class
    // summary). JsonIgnore keeps them out of serialization regardless of which JsonSerializerOptions
    // a caller uses (e.g. a bare JsonSerializerOptionsFactory.CreateForProduction()), rather than
    // relying on a specific options instance's ignore-null configuration.
    [JsonIgnore]
    public Role Role => Role.System;

    [JsonIgnore]
    public string? FromAgent { get; init; }

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }
}
