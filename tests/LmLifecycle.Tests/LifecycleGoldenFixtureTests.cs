using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmLifecycle.Approval;
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

    /// <summary>The hash the approval fixtures are pinned against, in the lowercase hex the contract requires.</summary>
    private const string ArgumentsHash =
        "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    /// <summary>The copy that goes to one approver: every field populated, <c>subscription_id</c> included.</summary>
    private const string AddressedApprovalRequestJson =
        """{"request_id":"req-1","subscription_id":"sub-1","thread_id":"thr-1","run_id":"run-1","generation_id":"gen-1","tool_call_id":"tc-1","tool_name":"write_file","arguments_hash":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","arguments":"{}","expires_at":"2026-07-27T08:35:00.0000000Z"}""";

    /// <summary>The host's own copy, which is never sent: no <c>subscription_id</c>, no arguments.</summary>
    private const string HostApprovalRequestJson =
        """{"request_id":"req-1","thread_id":"thr-1","run_id":"run-1","generation_id":"gen-1","tool_call_id":"tc-1","tool_name":"write_file","arguments_hash":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","expires_at":"2026-07-27T08:35:00.0000000Z"}""";

    private const string FullApprovalDecisionJson =
        """{"request_id":"req-1","subscription_id":"sub-1","decision":"allowed","arguments_hash":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","reason":"reviewed","decided_at":"2026-07-27T08:31:00.0000000Z"}""";

    private const string MinimalApprovalDecisionJson =
        """{"request_id":"req-1","subscription_id":"sub-1","decision":"denied","arguments_hash":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"}""";

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

    [Fact]
    public void Approval_request_addressed_to_an_approver_serializes_to_the_pinned_bytes()
    {
        // Pinned in full, field order included. `subscription_id` sits second — immediately after the
        // request it belongs to — because the pair is what an approver echoes back, and a reader
        // scanning a delivery for "who was this asked of" should find it beside the request id rather
        // than somewhere in the middle of the correlation fields.
        Encode(ApprovalRequest()).Should().Be(AddressedApprovalRequestJson);
    }

    [Fact]
    public void The_hosts_own_approval_request_omits_the_subscription_it_was_never_addressed_to()
    {
        // The host keeps one copy of the request that is not addressed to anybody; only the fan-out
        // copies name an approver. So the field is optional on this type, and absent rather than
        // empty: `""` would read as "addressed to a subscription whose id is blank", which is a claim,
        // where absence is the truth. Note that the other unset members are still written as `""` —
        // omission here is `subscription_id` being genuinely null, not a general empty-string rule.
        var hostCopy = ApprovalRequest() with { SubscriptionId = null, Arguments = null };

        var encoded = Encode(hostCopy);
        encoded.Should().Be(HostApprovalRequestJson);
        encoded.Should().NotContain("subscription_id");
    }

    [Fact]
    public void Approval_decision_serializes_to_the_pinned_bytes()
    {
        Encode(ApprovalDecision()).Should().Be(FullApprovalDecisionJson);

        // The optional halves drop out; `subscription_id` does not, because it is not optional here.
        var terse = ApprovalDecision() with
        {
            Decision = ToolApprovalOutcomes.Denied,
            Reason = null,
            DecidedAt = null,
        };

        Encode(terse).Should().Be(MinimalApprovalDecisionJson);
    }

    [Fact]
    public void A_request_may_arrive_without_a_subscription_id_but_a_decision_may_not()
    {
        // The asymmetry is the contract, and it is the reason `subscription_id` is worth pinning at
        // all: a request without one is the host's own copy, while a decision without one cannot be
        // checked against the approver set the gate froze — an approval nobody is accountable for.
        // So the request type tolerates the omission and the decision type refuses to decode at all,
        // which fails the submission closed rather than crediting it to an unnamed approver.
        Decode<ToolApprovalRequest>(HostApprovalRequestJson).SubscriptionId.Should().BeNull();

        var withoutSubscription = FullApprovalDecisionJson.Replace(
            "\"subscription_id\":\"sub-1\",",
            string.Empty,
            StringComparison.Ordinal
        );

        var decode = () => Decode<ToolApprovalDecision>(withoutSubscription);
        decode.Should().Throw<JsonException>();
    }

    [Fact]
    public void Pinned_approval_bytes_round_trip_and_match_their_utf8_encoding()
    {
        // The same two properties the event fixtures assert, on the approval types: re-encoding a
        // decoded value reproduces the bytes, and the UTF-8 encoder agrees with the text one. Both are
        // asserted once per target framework, so a net8.0/net9.0 divergence in the in-box
        // System.Text.Json fails here rather than corrupting a signature in production.
        Encode(Decode<ToolApprovalRequest>(AddressedApprovalRequestJson))
            .Should()
            .Be(AddressedApprovalRequestJson);
        Encode(Decode<ToolApprovalDecision>(FullApprovalDecisionJson))
            .Should()
            .Be(FullApprovalDecisionJson);

        JsonSerializer
            .SerializeToUtf8Bytes(ApprovalRequest(), LifecycleSerializer.Options)
            .Should()
            .Equal(Encoding.UTF8.GetBytes(AddressedApprovalRequestJson));
    }

    /// <summary>
    /// Encoded exactly as <c>LifecycleApprovalRequestPublisher</c> encodes a request: through
    /// <see cref="LifecycleSerializer.Options"/>. Going via <see cref="JsonSerializer"/> here rather
    /// than adding overloads to the serializer keeps the fixture honest — it pins the bytes the
    /// shipping call site actually produces.
    /// </summary>
    private static string Encode<T>(T value) =>
        JsonSerializer.Serialize(value, LifecycleSerializer.Options);

    private static T Decode<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, LifecycleSerializer.Options)!;

    private static ToolApprovalRequest ApprovalRequest() =>
        new()
        {
            RequestId = "req-1",
            SubscriptionId = "sub-1",
            ThreadId = "thr-1",
            RunId = "run-1",
            GenerationId = "gen-1",
            ToolCallId = "tc-1",
            ToolName = "write_file",
            ArgumentsHash = ArgumentsHash,
            Arguments = "{}",
            ExpiresAt = new DateTimeOffset(2026, 7, 27, 8, 35, 0, TimeSpan.Zero),
        };

    private static ToolApprovalDecision ApprovalDecision() =>
        new()
        {
            RequestId = "req-1",
            SubscriptionId = "sub-1",
            Decision = ToolApprovalOutcomes.Allowed,
            ArgumentsHash = ArgumentsHash,
            Reason = "reviewed",
            DecidedAt = new DateTimeOffset(2026, 7, 27, 8, 31, 0, TimeSpan.Zero),
        };
}
