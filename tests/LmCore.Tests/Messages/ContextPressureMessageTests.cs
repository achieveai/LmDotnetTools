using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

/// <summary>
///     The live per-agent pressure frame (#681; spec 679 §7.2): a transient, content-free projection of one
///     <see cref="ContextObservation" /> with the <c>context_pressure</c> discriminator and a fixed camelCase
///     wire shape, serialized through the production message converter.
/// </summary>
public class ContextPressureMessageTests
{
    private static ContextObservation Observation() =>
        new()
        {
            ThreadId = "subagent-agent-2",
            AgentId = "agent-2",
            RunId = "run-1",
            GenerationId = "gen-7",
            GenerationOrdinal = 7,
            ObservedAtUtc = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero),
            EffectiveModelId = "model-x",
            EstimatedInputTokens = 40_000,
            MeasuredInputTokens = 42_000,
            Provenance = MeasurementProvenance.Measured,
            WindowTokens = 200_000,
            ReserveTokens = 8_000,
            ActiveCheckpointId = "cp-1",
            RowsInView = 33,
        };

    [Fact]
    public void FromObservation_CopiesEveryContentFreeField()
    {
        var frame = ContextPressureMessage.FromObservation(Observation());

        Assert.Equal("subagent-agent-2", frame.ThreadId);
        Assert.Equal("agent-2", frame.AgentId);
        Assert.Equal("run-1", frame.RunId);
        Assert.Equal("gen-7", frame.GenerationId);
        Assert.Equal(7, frame.GenerationOrdinal);
        Assert.Equal("model-x", frame.EffectiveModelId);
        Assert.Equal(40_000, frame.EstimatedInputTokens);
        Assert.Equal(42_000, frame.MeasuredInputTokens);
        Assert.Equal("Measured", frame.Provenance);
        Assert.Equal(200_000, frame.WindowTokens);
        Assert.Equal(8_000, frame.ReserveTokens);
        Assert.Equal(42_000d / 192_000d, frame.Utilization!.Value, 9);
        Assert.Equal("cp-1", frame.ActiveCheckpointId);
        Assert.Equal(33, frame.RowsInView);
        Assert.True(frame is ITransientMessage, "a pressure frame is never buffered, persisted, or added to history");
    }

    [Fact]
    public void Serialize_AsIMessage_UsesContextPressureDiscriminator_AndCamelCaseFields()
    {
        IMessage frame = ContextPressureMessage.FromObservation(Observation());
        var options = JsonSerializerOptionsFactory.CreateForProduction();

        var json = JsonSerializer.Serialize(frame, options);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("context_pressure", root.GetProperty("$type").GetString());
        Assert.Equal("agent-2", root.GetProperty("agentId").GetString());
        Assert.Equal("subagent-agent-2", root.GetProperty("threadId").GetString());
        Assert.Equal(42_000, root.GetProperty("measuredInputTokens").GetInt64());
        Assert.Equal(200_000, root.GetProperty("windowTokens").GetInt64());
        Assert.Equal("Measured", root.GetProperty("provenance").GetString());
        Assert.False(json.Contains("\"text\"", StringComparison.Ordinal), "the frame carries no rendered content");
    }

    [Fact]
    public void RoundTrips_ThroughIMessageConverter()
    {
        IMessage original = ContextPressureMessage.FromObservation(Observation());
        var options = JsonSerializerOptionsFactory.CreateForProduction();

        var restored = JsonSerializer.Deserialize<IMessage>(JsonSerializer.Serialize(original, options), options);

        var frame = Assert.IsType<ContextPressureMessage>(restored);
        Assert.Equal(42_000, frame.MeasuredInputTokens);
        Assert.Equal("cp-1", frame.ActiveCheckpointId);
    }

    [Fact]
    public void UnknownWindow_LeavesUtilizationAndWindowNull()
    {
        var frame = ContextPressureMessage.FromObservation(Observation() with { WindowTokens = null });

        Assert.Null(frame.WindowTokens);
        Assert.Null(frame.Utilization);
    }
}
