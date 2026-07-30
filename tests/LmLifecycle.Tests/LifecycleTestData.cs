using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Tests;

/// <summary>
/// Fixed inputs shared by the contract tests.
/// </summary>
/// <remarks>
/// Every value here is a constant. Golden-fixture assertions compare exact bytes, so a timestamp
/// from the clock or an identifier from <see cref="Guid.NewGuid"/> would make them unrepeatable.
/// </remarks>
internal static class LifecycleTestData
{
    public static readonly DateTimeOffset OccurredAtUtc =
        new(2026, 7, 27, 8, 30, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset OccurredAtWithFraction =
        new DateTimeOffset(2026, 7, 27, 8, 30, 0, TimeSpan.Zero) + TimeSpan.FromTicks(1234567);

    /// <summary>An envelope carrying only the members V1 requires.</summary>
    public static LifecycleEventEnvelope Minimal() =>
        new()
        {
            SchemaMajor = LifecycleProtocol.CurrentMajor,
            EventId = "evt-min",
            EventType = LifecycleEventTypes.RunStarted,
            SourceStreamId = LifecycleSourceStream.ForThread("thr-1"),
            SourceSequence = 1,
            ProducerEpoch = "epoch-1",
            OccurredAt = OccurredAtUtc,
        };

    /// <summary>An envelope populating every optional member V1 defines.</summary>
    public static LifecycleEventEnvelope Maximal() =>
        new()
        {
            SchemaMajor = LifecycleProtocol.CurrentMajor,
            EventId = "evt-max",
            EventType = LifecycleEventTypes.RunStarted,
            SourceStreamId = LifecycleSourceStream.ForThread("thr-1"),
            SourceSequence = 42,
            ProducerEpoch = "epoch-1",
            OccurredAt = OccurredAtWithFraction,
            Correlation = new LifecycleCorrelation
            {
                ThreadId = "thr-1",
                RunId = "run-1",
                ParentRunId = "run-0",
                GenerationId = "gen-1",
                ToolCallId = "tc-1",
                SubAgentId = "sa-1",
                ParentThreadId = "thr-0",
                SpawningToolCallId = "tc-0",
                SandboxSessionId = "sess-1",
                WorkspaceId = "ws-1",
            },
            Payload = Serialization.LifecycleSerializer.ToPayloadElement(RunStarted()),
        };

    public static RunStartedPayload RunStarted() =>
        new()
        {
            RunId = "run-1",
            GenerationId = "gen-1",
            Cause = new LifecycleRunCause
            {
                Kind = LifecycleRunCauseKinds.ToolResult,
                ToolCallId = "tc-0",
            },
            WasForked = false,
            AgentKind = LifecycleAgentKinds.Raw,
            ModelId = "model-x",
        };
}
