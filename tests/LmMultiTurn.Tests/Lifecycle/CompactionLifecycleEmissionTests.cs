using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Lifecycle;

public class CompactionLifecycleEmissionTests
{
    [Theory]
    [InlineData(LifecycleEventTypes.CompactionDecided)]
    [InlineData(LifecycleEventTypes.CompactionApplied)]
    [InlineData(LifecycleEventTypes.CompactionFailed)]
    public async Task CompactionEvents_AreKnown_CarryTheirPayload_AndCorrelateToTheRun(string eventType)
    {
        var publisher = new RecordingLifecyclePublisher();
        var finalizer = new RunTurnLifecycleFinalizer(
            "thread-1",
            new MultiTurnLifecycleServices { Publisher = publisher }
        );
        var payload = new CompactionPayload
        {
            Decision = "compact",
            Reason = "hard",
            Tokens = 80_000,
            Window = 100_000,
            Reserve = 10_000,
            CacheTemperature = "cold",
            CutSeq = 41,
        };

        await finalizer.CompactionAsync(eventType, "run-1", "gen-1", payload);

        LifecycleEventTypes.IsKnown(eventType).Should().BeTrue();
        publisher.EventTypes.Should().Equal(eventType);
        var read = publisher.PayloadAt<CompactionPayload>(0);
        read.RunId.Should().Be("run-1");
        read.GenerationId.Should().Be("gen-1");
        read.Decision.Should().Be("compact");
        read.Reason.Should().Be("hard");
        read.CutSeq.Should().Be(41);
        var correlation = publisher.CorrelationsFor(eventType).Single();
        correlation.ThreadId.Should().Be("thread-1");
        correlation.RunId.Should().Be("run-1");
        correlation.GenerationId.Should().Be("gen-1");
    }

    [Fact]
    public async Task CompactionEvents_AreNotPublished_WhenLifecycleIsDisabled()
    {
        var finalizer = new RunTurnLifecycleFinalizer("thread-1", MultiTurnLifecycleServices.Disabled);

        var published = await finalizer.CompactionAsync(
            LifecycleEventTypes.CompactionDecided,
            "run-1",
            "gen-1",
            new CompactionPayload { Decision = "warn" }
        );

        published.Should().BeFalse();
    }
}
