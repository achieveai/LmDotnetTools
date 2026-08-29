using System.Text.Json;
using AchieveAi.LmDotnetTools.LmLifecycle.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Tests;

/// <summary>
/// Covers what a build compiled against V1 does when it meets values V1 did not define.
/// </summary>
/// <remarks>
/// Two rules are under test, and they pull in opposite directions on purpose. Descriptive data an
/// old build does not recognize must survive intact so it can still be forwarded and stored. A
/// value that grants permission must not: anything an approver sends that this build cannot prove
/// means "allowed" is treated as not allowed.
/// </remarks>
public class LifecycleForwardCompatibilityTests
{
    private const string FutureEventJson =
        """{"schema_major":1,"event_id":"evt-future","event_type":"quota_exhausted","source_stream_id":"thread:thr-1","source_sequence":9,"producer_epoch":"epoch-1","occurred_at":"2026-07-27T08:30:00.0000000Z","payload":{"kind":"brand_new_kind","limits":{"hard":100,"soft":null},"tags":["a","b"],"nested":{"deep":{"deeper":true}}}}""";

    [Fact]
    public void An_unknown_event_type_is_not_a_contract_violation()
    {
        var decoded = LifecycleSerializer.DeserializeEvent(FutureEventJson);

        decoded.EventType.Should().Be("quota_exhausted");
        decoded.IsKnownEventType.Should().BeFalse();
        decoded.Invoking(e => e.EnsureValid()).Should().NotThrow();
    }

    [Fact]
    public void An_unknown_event_round_trips_byte_identically()
    {
        var decoded = LifecycleSerializer.DeserializeEvent(FutureEventJson);

        LifecycleSerializer.Serialize(decoded).Should().Be(FutureEventJson);
    }

    [Fact]
    public void An_unknown_payload_is_preserved_verbatim_including_nulls_and_nesting()
    {
        var payload = LifecycleSerializer.DeserializeEvent(FutureEventJson).Payload;

        payload.Should().NotBeNull();
        var element = payload!.Value;
        element.GetProperty("kind").GetString().Should().Be("brand_new_kind");
        element.GetProperty("limits").GetProperty("hard").GetInt32().Should().Be(100);
        element.GetProperty("limits").GetProperty("soft").ValueKind.Should().Be(JsonValueKind.Null);
        element.GetProperty("tags").GetArrayLength().Should().Be(2);
        element.GetProperty("nested").GetProperty("deep").GetProperty("deeper").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Reading_an_unknown_payload_as_a_known_type_reports_failure_rather_than_throwing()
    {
        var decoded = LifecycleSerializer.DeserializeEvent(FutureEventJson);

        LifecycleSerializer.TryReadPayload<RunStartedPayload>(decoded, out var payload).Should().BeFalse();
        payload.Should().BeNull();
    }

    [Fact]
    public void A_payload_is_never_read_as_a_type_its_event_does_not_carry()
    {
        // Payload records have no required members, so this object would deserialize into any of
        // them with every property defaulted. The event type, not the payload's shape, decides.
        var mislabelled = LifecycleTestData.Maximal() with
        {
            EventType = LifecycleEventTypes.SandboxCreated,
        };

        LifecycleSerializer.TryReadPayload<RunStartedPayload>(mislabelled, out var asRunStarted).Should().BeFalse();
        asRunStarted.Should().BeNull();

        LifecycleSerializer
            .TryReadPayload<SandboxCreatedPayload>(mislabelled, out var asSandboxCreated)
            .Should()
            .BeTrue("the event type is what selects the payload type");
        asSandboxCreated!.SessionId.Should().BeEmpty();
    }

    [Fact]
    public void An_unknown_event_type_maps_to_no_payload_type()
    {
        LifecycleSerializer.GetPayloadType("quota_exhausted").Should().BeNull();
        LifecycleSerializer.GetPayloadType(null).Should().BeNull();
        LifecycleSerializer.GetPayloadType(LifecycleEventTypes.RunStarted).Should().Be(typeof(RunStartedPayload));
    }

    [Fact]
    public void An_unknown_value_in_an_open_vocabulary_field_survives_decoding()
    {
        var envelope = LifecycleTestData.Maximal() with
        {
            Payload = LifecycleSerializer.ToPayloadElement(
                LifecycleTestData.RunStarted() with
                {
                    AgentKind = "agent_kind_from_the_future",
                    Cause = new LifecycleRunCause { Kind = "cause_from_the_future" },
                }
            ),
        };

        var decoded = LifecycleSerializer.DeserializeEvent(LifecycleSerializer.Serialize(envelope));

        LifecycleSerializer.TryReadPayload<RunStartedPayload>(decoded, out var payload).Should().BeTrue();
        payload!.AgentKind.Should().Be("agent_kind_from_the_future");
        payload.Cause.Kind.Should().Be("cause_from_the_future");
    }

    [Theory]
    [InlineData(ToolApprovalOutcomes.Denied)]
    [InlineData(ToolApprovalOutcomes.Timeout)]
    [InlineData(ToolApprovalOutcomes.MissingApprover)]
    [InlineData(ToolApprovalOutcomes.Overload)]
    [InlineData(ToolApprovalOutcomes.HookError)]
    [InlineData(ToolApprovalOutcomes.Revoked)]
    [InlineData(ToolApprovalOutcomes.Cancelled)]
    [InlineData(ToolApprovalOutcomes.HostPolicyDenied)]
    [InlineData(ToolApprovalOutcomes.ProviderPolicyDenied)]
    [InlineData("allowed_by_some_future_policy")]
    [InlineData("ALLOWED")]
    [InlineData("allowed ")]
    [InlineData("")]
    [InlineData((string?)null)]
    public void Only_the_exact_allowed_code_permits_execution(string? outcome)
    {
        ToolApprovalOutcomes.IsAllowed(outcome).Should().BeFalse();
    }

    [Fact]
    public void The_allowed_code_permits_execution()
    {
        ToolApprovalOutcomes.IsAllowed(ToolApprovalOutcomes.Allowed).Should().BeTrue();
    }

    [Theory]
    [InlineData(ToolApprovalOutcomes.Allowed, true)]
    [InlineData(ToolApprovalOutcomes.Denied, true)]
    [InlineData(ToolApprovalOutcomes.Timeout, false)]
    [InlineData(ToolApprovalOutcomes.HostPolicyDenied, false)]
    [InlineData("decided_by_a_future_approver", false)]
    public void Only_allow_and_deny_may_arrive_from_an_approver(string outcome, bool expected)
    {
        ToolApprovalOutcomes.IsApproverSubmittable(outcome).Should().Be(expected);
    }

    [Fact]
    public void A_decision_carrying_an_unrecognized_code_does_not_match_its_request()
    {
        var request = NewRequest();
        var decision = new ToolApprovalDecision
        {
            RequestId = request.RequestId,
            Decision = "allowed_v2",
            ArgumentsHash = request.ArgumentsHash,
        };

        decision.Matches(request).Should().BeFalse();
    }

    [Fact]
    public void A_decision_for_different_arguments_does_not_match_its_request()
    {
        var request = NewRequest();
        var decision = new ToolApprovalDecision
        {
            RequestId = request.RequestId,
            Decision = ToolApprovalOutcomes.Allowed,
            ArgumentsHash = "a-hash-of-some-other-arguments",
        };

        decision.Matches(request).Should().BeFalse();
    }

    [Fact]
    public void A_decision_for_a_different_request_does_not_match()
    {
        var request = NewRequest();
        var decision = new ToolApprovalDecision
        {
            RequestId = "req-other",
            Decision = ToolApprovalOutcomes.Allowed,
            ArgumentsHash = request.ArgumentsHash,
        };

        decision.Matches(request).Should().BeFalse();
    }

    [Fact]
    public void A_decision_matches_only_its_own_request_and_arguments()
    {
        var request = NewRequest();
        var decision = new ToolApprovalDecision
        {
            RequestId = request.RequestId,
            Decision = ToolApprovalOutcomes.Allowed,
            ArgumentsHash = request.ArgumentsHash,
        };

        decision.Matches(request).Should().BeTrue();
    }

    private static ToolApprovalRequest NewRequest() =>
        new()
        {
            RequestId = "req-1",
            ThreadId = "thr-1",
            RunId = "run-1",
            GenerationId = "gen-1",
            ToolCallId = "tc-1",
            ToolName = "delete_everything",
            ArgumentsHash = "sha256-of-the-frozen-argument-bytes",
            ExpiresAt = LifecycleTestData.OccurredAtUtc.AddMinutes(5),
        };
}
