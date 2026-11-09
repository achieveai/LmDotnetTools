using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Http;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     A live-only frame for one provider retry: the model call failed with a retryable status (a rate
///     limit, an overload) or a transport error, and the client is waiting before it tries again. Lets a
///     user see why a turn has gone quiet instead of guessing that it hung (#930 P1 item 1).
///     Implements <see cref="ITransientMessage" />: never buffered, added to history, or persisted — a
///     retry is over by the time anyone reconnects, so there is nothing to restore.
/// </summary>
/// <remarks>
///     Content-free by construction: counts, a status code, a delay and ids. Field names are fixed
///     camelCase via <see cref="JsonPropertyNameAttribute" />, matching <c>context_pressure</c>.
/// </remarks>
public sealed record ProviderRetryMessage : IMessage, ITransientMessage
{
    /// <summary>The wire discriminator.</summary>
    public const string TypeDiscriminator = "provider_retry";

    /// <summary>The thread whose model call is being retried.</summary>
    [JsonPropertyName("threadId")]
    public string? ThreadId { get; init; }

    /// <summary>The run the model call belongs to.</summary>
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <summary>The generation being retried.</summary>
    [JsonPropertyName("generationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    /// <summary>The retry number, 1-based.</summary>
    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }

    /// <summary>Total attempts allowed, the first request included.</summary>
    [JsonPropertyName("maxAttempts")]
    public int MaxAttempts { get; init; }

    /// <summary>The failing HTTP status, or null for a transport failure.</summary>
    [JsonPropertyName("statusCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StatusCode { get; init; }

    /// <summary>How long the client waits before the retry, in milliseconds.</summary>
    [JsonPropertyName("delayMs")]
    public long DelayMs { get; init; }

    /// <summary>True when the delay is the server's own Retry-After rather than backoff.</summary>
    [JsonPropertyName("fromRetryAfter")]
    public bool FromRetryAfter { get; init; }

    /// <summary>The role associated with this frame (assistant, matching other loop-emitted messages).</summary>
    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    /// <summary>The name or identifier of the agent whose call is retried.</summary>
    [JsonPropertyName("fromAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    /// <summary>Not carried on transient frames.</summary>
    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>Projects a retry notice into the frame.</summary>
    public static ProviderRetryMessage FromNotice(HttpRetryNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        return new ProviderRetryMessage
        {
            Attempt = notice.Attempt,
            MaxAttempts = notice.MaxAttempts,
            StatusCode = notice.StatusCode is { } status ? (int)status : null,
            DelayMs = (long)notice.Delay.TotalMilliseconds,
            FromRetryAfter = notice.FromRetryAfter,
        };
    }
}
