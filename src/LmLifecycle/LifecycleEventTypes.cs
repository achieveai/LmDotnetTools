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
/// These six events are deliberately bounded and observational. They are not an audit log: delivery
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
        };

    /// <summary>
    /// Indicates whether this build can map <paramref name="eventType"/> to a typed payload.
    /// </summary>
    /// <param name="eventType">The discriminator carried by the envelope.</param>
    /// <returns><see langword="true"/> when a typed payload exists for the value.</returns>
    public static bool IsKnown(string? eventType) =>
        eventType is not null && Known.Contains(eventType);
}
