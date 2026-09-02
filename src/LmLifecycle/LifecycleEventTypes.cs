namespace AchieveAi.LmDotnetTools.LmLifecycle;

/// <summary>
/// The V1 lifecycle event-type discriminators.
/// </summary>
/// <remarks>
/// <para>
/// The discriminator is an <b>open</b> string, not a closed enumeration. A subscriber that meets a
/// value absent from this class must preserve the event and forward it rather than reject it — the
/// raw payload survives a round trip byte-for-byte precisely so that this is possible.
/// </para>
/// <para>
/// These events are deliberately bounded and observational. They are not an audit log: delivery
/// is best-effort with no durable outbox, replay, or backfill.
/// </para>
/// </remarks>
public static class LifecycleEventTypes
{
    /// <summary>A run began. Payload: <see cref="Payloads.RunStartedPayload"/>.</summary>
    public const string RunStarted = "run_started";

    /// <summary>
    /// Rendered context was included in a provider request. Payload:
    /// <see cref="Payloads.ContextLoadedPayload"/>.
    /// </summary>
    public const string ContextLoaded = "context_loaded";

    /// <summary>A turn reached its final state. Payload: <see cref="Payloads.TurnCompletedPayload"/>.</summary>
    public const string TurnCompleted = "turn_completed";

    /// <summary>
    /// A tool call reached its final state, including a delayed result resolving after its
    /// requesting run ended. Payload: <see cref="Payloads.ToolCompletedPayload"/>.
    /// </summary>
    public const string ToolCompleted = "tool_completed";

    /// <summary>A run reached a terminal boundary. Payload: <see cref="Payloads.RunCompletedPayload"/>.</summary>
    public const string RunCompleted = "run_completed";

    /// <summary>
    /// A sandbox session was committed. Payload: <see cref="Payloads.SandboxCreatedPayload"/>.
    /// </summary>
    public const string SandboxCreated = "sandbox_created";

    /// <summary>The just-in-time compaction policy recorded a decision for the request about to be sent (spec 679 §5.5).</summary>
    public const string CompactionDecided = "compaction_decided";

    /// <summary>A compaction checkpoint reached <c>Active</c>; the execution view now hides the rows it covers.</summary>
    public const string CompactionApplied = "compaction_applied";

    /// <summary>A compaction was rejected or rolled back; the payload carries the typed reason.</summary>
    public const string CompactionFailed = "compaction_failed";

    /// <summary>
    /// One agent loop's request was sized against its model's window — estimated before dispatch,
    /// measured once the provider reported usage. Payload:
    /// <see cref="Payloads.ContextMeasuredPayload"/>.
    /// </summary>
    public const string ContextMeasured = "context_measured";

    /// <summary>
    /// The event types this build understands, in no particular order.
    /// </summary>
    /// <remarks>
    /// Membership is informational. It answers "can I map this to a typed payload?" and must never
    /// be used to decide whether an event may be forwarded or stored.
    /// </remarks>
    public static IReadOnlySet<string> Known { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            RunStarted,
            ContextLoaded,
            TurnCompleted,
            ToolCompleted,
            RunCompleted,
            SandboxCreated,
            ContextMeasured,
            CompactionDecided,
            CompactionApplied,
            CompactionFailed,
        };

    /// <summary>
    /// Indicates whether this build can map <paramref name="eventType"/> to a typed payload.
    /// </summary>
    /// <param name="eventType">The discriminator carried by the envelope.</param>
    /// <returns><see langword="true"/> when a typed payload exists for the value.</returns>
    public static bool IsKnown(string? eventType) => eventType is not null && Known.Contains(eventType);
}
