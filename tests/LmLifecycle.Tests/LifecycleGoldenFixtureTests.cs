using System.Text;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Tests;

/// <summary>
/// Pins the exact bytes a V1 envelope serializes to.
/// </summary>
/// <remarks>
/// <para>
/// These fixtures are the wire format. A change here is a change to what every subscriber parses and
/// to the bytes a signature is computed over, so a failing assertion in this class is a protocol
/// decision, not a test that needs updating.
/// </para>
/// <para>
/// The literals are written out in full rather than generated, so a diff shows the format change
/// directly.
/// </para>
/// </remarks>
public class LifecycleGoldenFixtureTests
{
    private const string MinimalEventJson =
        """{"schema_major":1,"event_id":"evt-min","event_type":"run_started","source_stream_id":"thread:thr-1","source_sequence":1,"producer_epoch":"epoch-1","occurred_at":"2026-07-27T08:30:00.0000000Z"}""";

    private const string MaximalEventJson =
        """{"schema_major":1,"event_id":"evt-max","event_type":"run_started","source_stream_id":"thread:thr-1","source_sequence":42,"producer_epoch":"epoch-1","occurred_at":"2026-07-27T08:30:00.1234567Z","correlation":{"thread_id":"thr-1","run_id":"run-1","parent_run_id":"run-0","generation_id":"gen-1","tool_call_id":"tc-1","sub_agent_id":"sa-1","parent_thread_id":"thr-0","spawning_tool_call_id":"tc-0","sandbox_session_id":"sess-1","workspace_id":"ws-1"},"payload":{"run_id":"run-1","generation_id":"gen-1","cause":{"kind":"tool_result","tool_call_id":"tc-0"},"was_forked":false,"agent_kind":"raw","model_id":"model-x"}}""";

    private const string DeliveryJson =
        "{\"delivery_id\":\"dlv-1\",\"delivery_sequence\":7,\"event\":" + MinimalEventJson + "}";

    [Fact]
    public void Minimal_event_serializes_to_the_pinned_bytes()
    {
        LifecycleSerializer.Serialize(LifecycleTestData.Minimal()).Should().Be(MinimalEventJson);
    }

    [Fact]
    public void Maximal_event_serializes_to_the_pinned_bytes()
    {
        LifecycleSerializer.Serialize(LifecycleTestData.Maximal()).Should().Be(MaximalEventJson);
    }

    [Fact]
    public void Delivery_wraps_the_event_without_altering_its_bytes()
    {
        var delivery = new LifecycleDeliveryEnvelope
        {
            DeliveryId = "dlv-1",
            DeliverySequence = 7,
            Event = LifecycleTestData.Minimal(),
        };

        LifecycleSerializer.Serialize(delivery).Should().Be(DeliveryJson);
    }

    [Fact]
    public void Pinned_bytes_round_trip_back_to_the_same_bytes()
    {
        foreach (var json in new[] { MinimalEventJson, MaximalEventJson })
        {
            var decoded = LifecycleSerializer.DeserializeEvent(json);
            LifecycleSerializer.Serialize(decoded).Should().Be(json);
        }
    }

    [Fact]
    public void Utf8_encoding_matches_the_text_encoding()
    {
        var envelope = LifecycleTestData.Maximal();

        LifecycleSerializer
            .SerializeToUtf8Bytes(envelope)
            .Should()
            .Equal(Encoding.UTF8.GetBytes(LifecycleSerializer.Serialize(envelope)));
    }

    /// <summary>
    /// The fixtures above are asserted once per target framework, because the whole test project is
    /// built and run for each. That is the byte-symmetry check: net8.0 and net9.0 ship different
    /// in-box <c>System.Text.Json</c> versions, and this class fails on whichever one drifts.
    /// </summary>
    [Fact]
    public void Serialization_is_stable_across_repeated_calls()
    {
        var envelope = LifecycleTestData.Maximal();
        var first = LifecycleSerializer.Serialize(envelope);

        for (var i = 0; i < 5; i++)
        {
            LifecycleSerializer.Serialize(envelope).Should().Be(first);
        }
    }

    [Fact]
    public void Timestamps_are_normalized_to_utc_before_encoding()
    {
        var utc = LifecycleTestData.Minimal();
        var sameInstantInAnotherOffset = utc with
        {
            OccurredAt = utc.OccurredAt.ToOffset(TimeSpan.FromHours(5.5)),
        };

        LifecycleSerializer
            .Serialize(sameInstantInAnotherOffset)
            .Should()
            .Be(LifecycleSerializer.Serialize(utc));
    }

    [Fact]
    public void Timestamps_keep_sub_millisecond_precision()
    {
        LifecycleSerializer
            .Serialize(LifecycleTestData.Maximal())
            .Should()
            .Contain("\"occurred_at\":\"2026-07-27T08:30:00.1234567Z\"");
    }

    [Fact]
    public void Decoding_a_timestamp_in_a_non_utc_offset_yields_the_same_instant()
    {
        var json = MinimalEventJson.Replace(
            "\"occurred_at\":\"2026-07-27T08:30:00.0000000Z\"",
            "\"occurred_at\":\"2026-07-27T14:00:00+05:30\"",
            StringComparison.Ordinal
        );

        var decoded = LifecycleSerializer.DeserializeEvent(json);

        decoded.OccurredAt.Should().Be(LifecycleTestData.OccurredAtUtc);
        decoded.OccurredAt.Offset.Should().Be(TimeSpan.Zero);
        LifecycleSerializer.Serialize(decoded).Should().Be(MinimalEventJson);
    }
}
