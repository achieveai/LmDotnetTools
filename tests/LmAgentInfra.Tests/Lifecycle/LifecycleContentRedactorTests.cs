using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;
using AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;
using AchieveAi.LmDotnetTools.LmLifecycle;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Lifecycle;

/// <summary>
/// ADR 0005 — the <c>lifecycle.content.full</c> gate. These tests are written against the
/// allow-list's defining property rather than against its current contents: a field nobody
/// classified must not be delivered, and neither must a payload nobody classified. Asserting only
/// "rendered_text is absent" would keep passing on the day a new content field is added beside it.
/// </summary>
public sealed class LifecycleContentRedactorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    private readonly LifecycleContentRedactor _redactor = new();

    [Fact]
    public void A_subscription_granted_content_full_receives_the_payload_untouched()
    {
        var lifecycleEvent = EventWith(
            LifecycleEventTypes.ContextLoaded,
            """{"rendered_hash":"h","rendered_text":"the user's system prompt"}"""
        );

        var visible = _redactor.Redact(lifecycleEvent, Subscription(contentFull: true));

        visible.Should().BeSameAs(lifecycleEvent);
    }

    [Fact]
    public void A_field_nobody_classified_is_dropped_even_though_its_neighbours_survive()
    {
        // `not_yet_classified` stands in for the field somebody adds next quarter. A deny-list would
        // ship it; the allow-list withholds it until it is classified.
        var lifecycleEvent = EventWith(
            LifecycleEventTypes.ContextLoaded,
            """
            {
              "run_id": "run-1",
              "rendered_hash": "sha256:abc",
              "rendered_byte_count": 4096,
              "rendered_text": "You are a helpful assistant working in C:/secret/project",
              "not_yet_classified": "whatever a future producer decides to put here"
            }
            """
        );

        var payload = PayloadOf(_redactor.Redact(lifecycleEvent, Subscription(contentFull: false)));

        PropertyNames(payload).Should().Equal("run_id", "rendered_hash", "rendered_byte_count");
    }

    [Fact]
    public void A_nested_object_is_projected_rather_than_copied_wholesale()
    {
        // The dangerous shape: an object whose own name is on the allow-list but whose contents are
        // mixed. `code` is a constant; `message` routinely carries a path or a prompt fragment.
        var lifecycleEvent = EventWith(
            LifecycleEventTypes.TurnCompleted,
            """
            {
              "turn_index": 3,
              "outcome": "failed",
              "error": { "code": "provider_error", "message": "429 for key sk-live-...", "detail": "x" }
            }
            """
        );

        var payload = PayloadOf(_redactor.Redact(lifecycleEvent, Subscription(contentFull: false)));

        PropertyNames(payload).Should().Equal("turn_index", "outcome", "error");
        PropertyNames(payload.GetProperty("error")).Should().Equal("code");
    }

    [Fact]
    public void Every_element_of_an_array_is_projected_individually()
    {
        var lifecycleEvent = EventWith(
            LifecycleEventTypes.ContextLoaded,
            """
            {
              "sources": [
                { "discovery_kind": "claude_md", "name": "CLAUDE.md", "normalized_path": "B:/secret/CLAUDE.md" },
                { "discovery_kind": "agents_md", "rendered_byte_count": 12, "dedup_identity": "B:/secret" }
              ]
            }
            """
        );

        var payload = PayloadOf(_redactor.Redact(lifecycleEvent, Subscription(contentFull: false)));
        var sources = payload.GetProperty("sources").EnumerateArray().ToArray();

        PropertyNames(sources[0]).Should().Equal("discovery_kind");
        PropertyNames(sources[1]).Should().Equal("discovery_kind", "rendered_byte_count");
    }

    [Fact]
    public void An_environment_inventory_survives_only_as_a_shape()
    {
        // "Was anything provisioned" is answerable; "what exactly is installed on this host" is not.
        var lifecycleEvent = EventWith(
            LifecycleEventTypes.SandboxCreated,
            """
            {
              "session_id": "sess-1",
              "image_reference": "registry.internal.example/sandbox@sha256:deadbeef",
              "inventory": {
                "status": "available",
                "unavailable_reason": "gateway said no",
                "items": [{ "kind": "skill", "id": "egress-auth", "version": "1.2.0" }]
              }
            }
            """
        );

        var payload = PayloadOf(_redactor.Redact(lifecycleEvent, Subscription(contentFull: false)));

        PropertyNames(payload).Should().Equal("session_id", "inventory");
        PropertyNames(payload.GetProperty("inventory")).Should().Equal("status", "items");
        PropertyNames(payload.GetProperty("inventory").GetProperty("items")[0]).Should().Equal("kind");
    }

    [Fact]
    public void An_unknown_event_type_has_its_whole_payload_withheld()
    {
        // A newer producer emitting an event this build has never heard of. "We do not know what is
        // in it" is not a reason to forward it to a subscriber that was not granted content.
        var lifecycleEvent = EventWith(
            "approval_requested_by_some_future_build",
            """{"tool_name":"Bash","arguments":{"command":"cat ~/.ssh/id_rsa"}}"""
        );

        var visible = _redactor.Redact(lifecycleEvent, Subscription(contentFull: false));

        visible.Payload.Should().BeNull();
        visible.EventType.Should().Be("approval_requested_by_some_future_build");
    }

    [Theory]
    [MemberData(nameof(KnownEventTypes))]
    public void Every_known_event_type_has_an_allow_list(string eventType)
    {
        // The counterweight to the test above: withholding is the safe default, but a known event
        // type whose payload is withheld entirely is a classification somebody forgot, not a policy.
        var lifecycleEvent = EventWith(eventType, """{"run_id":"run-1"}""");

        var visible = _redactor.Redact(lifecycleEvent, Subscription(contentFull: false));

        visible
            .Payload.Should()
            .NotBeNull("a known event type without an allow-list silently degrades to withholding everything");
    }

    [Fact]
    public void The_envelopes_own_identity_is_never_redacted()
    {
        // Identity is not content. A subscriber that cannot see source_sequence cannot detect the
        // gaps this pipeline deliberately produces.
        var lifecycleEvent = EventWith(LifecycleEventTypes.RunCompleted, """{"outcome":"completed"}""");
        lifecycleEvent.Correlation = new LifecycleCorrelation { ThreadId = "thread-1", RunId = "run-1" };

        var visible = _redactor.Redact(lifecycleEvent, Subscription(contentFull: false));

        visible.EventId.Should().Be(lifecycleEvent.EventId);
        visible.SourceStreamId.Should().Be(lifecycleEvent.SourceStreamId);
        visible.SourceSequence.Should().Be(lifecycleEvent.SourceSequence);
        visible.ProducerEpoch.Should().Be(lifecycleEvent.ProducerEpoch);
        visible.OccurredAt.Should().Be(lifecycleEvent.OccurredAt);
        visible.Correlation.Should().BeSameAs(lifecycleEvent.Correlation);
    }

    [Fact]
    public void An_event_without_a_payload_is_returned_as_it_is()
    {
        var lifecycleEvent = new LifecycleEventEnvelope
        {
            EventId = "evt-1",
            EventType = LifecycleEventTypes.RunStarted,
        };

        var visible = _redactor.Redact(lifecycleEvent, Subscription(contentFull: false));

        visible.Should().BeSameAs(lifecycleEvent);
    }

    [Fact]
    public void A_payload_whose_shape_contradicts_its_classification_is_withheld()
    {
        // `error` is classified as an object. Arriving as a string means the payload does not have
        // the shape this classification was written against, and an unverified shape is exactly where
        // "it is probably fine" has never been checked.
        var lifecycleEvent = EventWith(
            LifecycleEventTypes.RunCompleted,
            """{"outcome":"failed","error":"Bash failed: cat /etc/shadow"}"""
        );

        var payload = PayloadOf(_redactor.Redact(lifecycleEvent, Subscription(contentFull: false)));

        payload.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
    }

    public static TheoryData<string> KnownEventTypes()
    {
        var data = new TheoryData<string>();
        foreach (var eventType in LifecycleEventTypes.Known)
        {
            data.Add(eventType);
        }

        return data;
    }

    private static LifecycleEventEnvelope EventWith(string eventType, string payloadJson) =>
        new()
        {
            EventId = "evt-1",
            EventType = eventType,
            SourceStreamId = "thread-1",
            SourceSequence = 7,
            ProducerEpoch = "epoch-1",
            OccurredAt = Now,
            Payload = JsonDocument.Parse(payloadJson).RootElement.Clone(),
        };

    private static JsonElement PayloadOf(LifecycleEventEnvelope lifecycleEvent)
    {
        lifecycleEvent.Payload.Should().NotBeNull();
        return lifecycleEvent.Payload!.Value;
    }

    private static IReadOnlyList<string> PropertyNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name)];

    private static LifecycleSubscription Subscription(bool contentFull) =>
        new(
            "sub-a",
            LifecycleOwnerKey.ForAppId("app-a"),
            "app-a",
            new Uri("https://subscriber.invalid/hook"),
            new WebhookSigningSecret("test-signing-secret-0123456789"),
            contentFull ? [LifecycleCapabilities.ContentFull] : [],
            [],
            Now
        );
}
