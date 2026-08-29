using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Tests;

/// <summary>
/// Covers source-stream naming, ordinal allocation, and envelope validation — the machinery a
/// subscriber relies on to notice it missed something.
/// </summary>
public class LifecycleStreamIdentityTests
{
    [Fact]
    public void A_thread_stream_names_its_thread()
    {
        LifecycleSourceStream.ForThread("thr-1").Should().Be("thread:thr-1");
    }

    [Fact]
    public void A_sandbox_stream_names_its_session()
    {
        LifecycleSourceStream.ForSandbox("sess-1").Should().Be("sandbox:sess-1");
    }

    [Theory]
    [InlineData("thread:thr-1", "thread", "thr-1")]
    [InlineData("sandbox:sess-1", "sandbox", "sess-1")]
    [InlineData("thread:a:b", "thread", "a:b")]
    [InlineData("stream_kind_from_the_future:x", "stream_kind_from_the_future", "x")]
    public void A_well_formed_stream_id_parses_into_its_kind_and_subject(
        string streamId,
        string expectedKind,
        string expectedSubject
    )
    {
        LifecycleSourceStream.TryParse(streamId, out var kind, out var subjectId).Should().BeTrue();

        kind.Should().Be(expectedKind);
        subjectId.Should().Be(expectedSubject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("thread")]
    [InlineData(":thr-1")]
    [InlineData("thread:")]
    [InlineData((string?)null)]
    public void A_malformed_stream_id_does_not_parse(string? streamId)
    {
        LifecycleSourceStream.TryParse(streamId, out var kind, out var subjectId).Should().BeFalse();

        kind.Should().BeNull();
        subjectId.Should().BeNull();
    }

    [Fact]
    public void Ordinals_start_at_one_and_increase_by_one_within_a_stream()
    {
        var allocator = new InMemoryLifecycleSequenceAllocator("epoch-1");

        allocator.Next("thread:thr-1").Should().Be(1);
        allocator.Next("thread:thr-1").Should().Be(2);
        allocator.Next("thread:thr-1").Should().Be(3);
    }

    [Fact]
    public void Streams_are_numbered_independently()
    {
        var allocator = new InMemoryLifecycleSequenceAllocator("epoch-1");

        allocator.Next("thread:thr-1").Should().Be(1);
        allocator.Next("sandbox:sess-1").Should().Be(1);
        allocator.Next("thread:thr-1").Should().Be(2);
        allocator.Next("thread:thr-2").Should().Be(1);
    }

    [Fact]
    public void Stream_names_are_compared_ordinally()
    {
        var allocator = new InMemoryLifecycleSequenceAllocator("epoch-1");

        allocator.Next("thread:Thr-1").Should().Be(1);
        allocator.Next("thread:thr-1").Should().Be(1);
    }

    [Fact]
    public void Concurrent_allocation_issues_every_ordinal_exactly_once()
    {
        const int Callers = 8;
        const int PerCaller = 500;
        var allocator = new InMemoryLifecycleSequenceAllocator("epoch-1");
        var issued = new ConcurrentBag<long>();

        Parallel.For(
            0,
            Callers,
            _ =>
            {
                for (var i = 0; i < PerCaller; i++)
                {
                    issued.Add(allocator.Next("thread:thr-1"));
                }
            }
        );

        issued.Should().HaveCount(Callers * PerCaller);
        issued.Distinct().Should().HaveCount(Callers * PerCaller);
        issued.Min().Should().Be(1);
        issued.Max().Should().Be(Callers * PerCaller);
    }

    [Fact]
    public void A_restarted_producer_is_distinguishable_from_a_gap()
    {
        var first = new InMemoryLifecycleSequenceAllocator();
        var restarted = new InMemoryLifecycleSequenceAllocator();

        first.Next("thread:thr-1");
        first.Next("thread:thr-1");

        restarted.Next("thread:thr-1").Should().Be(1, "a restarted producer's counter starts over");
        restarted.ProducerEpoch.Should().NotBe(first.ProducerEpoch, "so the reset is not mistaken for a gap");
    }

    [Fact]
    public void An_epoch_is_generated_when_the_host_does_not_supply_one()
    {
        new InMemoryLifecycleSequenceAllocator().ProducerEpoch.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_created_envelope_carries_this_builds_major_and_the_allocators_epoch()
    {
        var allocator = new InMemoryLifecycleSequenceAllocator("epoch-1");

        var envelope = LifecycleSerializer.CreateEnvelope(
            LifecycleEventTypes.RunStarted,
            LifecycleTestData.RunStarted(),
            LifecycleSourceStream.ForThread("thr-1"),
            allocator,
            LifecycleTestData.OccurredAtUtc,
            new LifecycleCorrelation { ThreadId = "thr-1" }
        );

        envelope.SchemaMajor.Should().Be(LifecycleProtocol.CurrentMajor);
        envelope.ProducerEpoch.Should().Be("epoch-1");
        envelope.SourceSequence.Should().Be(1);
        envelope.EventId.Should().NotBeNullOrWhiteSpace();
        envelope.Invoking(e => e.EnsureValid()).Should().NotThrow();
    }

    [Fact]
    public void Two_events_from_one_producer_never_share_an_identity()
    {
        var allocator = new InMemoryLifecycleSequenceAllocator("epoch-1");

        var first = NewEnvelope(allocator);
        var second = NewEnvelope(allocator);

        second.EventId.Should().NotBe(first.EventId);
        second.SourceSequence.Should().Be(first.SourceSequence + 1);
    }

    [Fact]
    public void A_valid_envelope_passes_validation()
    {
        LifecycleTestData.Maximal().Invoking(e => e.EnsureValid()).Should().NotThrow();
    }

    [Fact]
    public void A_structurally_unusable_envelope_is_rejected()
    {
        var valid = LifecycleTestData.Minimal();
        var invalid = new (string Because, LifecycleEventEnvelope Envelope)[]
        {
            ("an event without an id cannot be deduplicated", valid with { EventId = "" }),
            ("an event without a type has no payload shape", valid with { EventType = "  " }),
            ("an ordinal without an epoch cannot be interpreted", valid with { ProducerEpoch = "" }),
            ("an unparseable stream id has no ordering", valid with { SourceStreamId = "thread" }),
            ("ordinals start at 1", valid with { SourceSequence = 0 }),
            ("ordinals are never negative", valid with { SourceSequence = -1 }),
            (
                "an unsupported major must be refused at registration, not decoded",
                valid with
                {
                    SchemaMajor = LifecycleProtocol.CurrentMajor + 1,
                }
            ),
        };

        foreach (var (because, envelope) in invalid)
        {
            envelope.Invoking(e => e.EnsureValid()).Should().Throw<LifecycleContractException>(because);
        }
    }

    [Fact]
    public void A_delivery_validates_the_event_it_carries()
    {
        var delivery = new LifecycleDeliveryEnvelope
        {
            DeliveryId = "dlv-1",
            DeliverySequence = 1,
            Event = LifecycleTestData.Minimal() with { SourceSequence = 0 },
        };

        delivery.Invoking(d => d.EnsureValid()).Should().Throw<LifecycleContractException>();
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("dlv-1", 0)]
    [InlineData("dlv-1", -3)]
    public void A_structurally_unusable_delivery_is_rejected(string deliveryId, long sequence)
    {
        var delivery = new LifecycleDeliveryEnvelope
        {
            DeliveryId = deliveryId,
            DeliverySequence = sequence,
            Event = LifecycleTestData.Minimal(),
        };

        delivery.Invoking(d => d.EnsureValid()).Should().Throw<LifecycleContractException>();
    }

    [Fact]
    public void Malformed_json_is_reported_as_a_contract_violation()
    {
        var decode = () => LifecycleSerializer.DeserializeEvent("{ not json");

        decode.Should().Throw<LifecycleContractException>();
    }

    [Fact]
    public void A_body_missing_a_required_member_is_reported_as_a_contract_violation()
    {
        var decode = () => LifecycleSerializer.DeserializeEvent("""{"schema_major":1,"event_id":"evt-1"}""");

        decode.Should().Throw<LifecycleContractException>();
    }

    [Fact]
    public async Task Publishing_to_the_null_publisher_is_a_no_op()
    {
        var publish = async () => await NullLifecyclePublisher.Instance.PublishAsync(LifecycleTestData.Minimal());

        await publish.Should().NotThrowAsync();
    }

    private static LifecycleEventEnvelope NewEnvelope(ILifecycleSequenceAllocator allocator) =>
        LifecycleSerializer.CreateEnvelope(
            LifecycleEventTypes.RunStarted,
            LifecycleTestData.RunStarted(),
            LifecycleSourceStream.ForThread("thr-1"),
            allocator,
            LifecycleTestData.OccurredAtUtc
        );
}
