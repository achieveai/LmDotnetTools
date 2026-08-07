using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Reason a subscriber was signalled to resynchronize: its stream ended early for a cause other
/// than the run completing, so the client must reconnect/resync rather than treat the end as
/// terminal.
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
    /// was dropped from fan-out so the run and every other subscriber remain unblocked. See
    /// <see cref="MultiTurnAgentBase.PublishToSubscriber"/>.
    /// </summary>
    [JsonPropertyName("slow_consumer")]
    SlowConsumer,
}

/// <summary>
/// Terminal, content-free control yielded to a subscriber whose stream ended because it was
/// dropped — never because the run completed. Lets the client distinguish "you were disconnected;
/// reconnect and resync" from an ordinary <see cref="RunCompletedMessage"/> or a clean unsubscribe.
/// Carries only identifiers (thread/run/generation) and the drop reason — no prompt, tool, or
/// transcript content — so it is always safe to log or forward to a client verbatim (WebSocket
/// discriminator: <c>stream_recovery</c>).
/// </summary>
public sealed record StreamRecoveryMessage(
    [property: JsonPropertyName("threadId")] string? ThreadId,
    [property: JsonPropertyName("runId")] string? RunId,
    [property: JsonPropertyName("generationId")] string? GenerationId,
    [property: JsonPropertyName("reason")] StreamRecoveryReason Reason) : IMessage
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
