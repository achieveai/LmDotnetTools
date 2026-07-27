using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmLifecycle.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Tests;

/// <summary>
/// Pins what absent, null, and empty each mean on the wire.
/// </summary>
/// <remarks>
/// A subscriber deciding whether a producer reported nothing or reported an empty result needs this
/// distinction to be a contract rather than an accident of serializer configuration. The rule:
/// absent and null are the same thing — "not reported" — because null-valued members are omitted;
/// an empty collection is written and means "reported, and there were none."
/// </remarks>
public class LifecycleFieldSemanticsTests
{
    [Fact]
    public void A_null_member_is_omitted_rather_than_written_as_null()
    {
        var json = LifecycleSerializer.Serialize(LifecycleTestData.Minimal());

        json.Should().NotContain("correlation");
        json.Should().NotContain("payload");
        json.Should().NotContain("null");
    }

    [Fact]
    public void An_explicit_null_decodes_the_same_as_an_absent_member()
    {
        const string WithExplicitNulls =
            """{"schema_major":1,"event_id":"evt-min","event_type":"run_started","source_stream_id":"thread:thr-1","source_sequence":1,"producer_epoch":"epoch-1","occurred_at":"2026-07-27T08:30:00.0000000Z","correlation":null,"payload":null}""";

        var decoded = LifecycleSerializer.DeserializeEvent(WithExplicitNulls);

        decoded.Correlation.Should().BeNull();
        decoded.Payload.Should().BeNull();
        LifecycleSerializer
            .Serialize(decoded)
            .Should()
            .Be(LifecycleSerializer.Serialize(LifecycleTestData.Minimal()));
    }

    /// <summary>Encodes a payload exactly as an envelope would carry it.</summary>
    private static string PayloadJson<TPayload>(TPayload payload)
        where TPayload : class => LifecycleSerializer.ToPayloadElement(payload).GetRawText();

    [Fact]
    public void An_empty_collection_is_written_and_is_distinct_from_absent()
    {
        var json = PayloadJson(
            new ContextLoadedPayload
            {
                RunId = "run-1",
                GenerationId = "gen-1",
                RenderedHash = "abc",
                RenderedByteCount = 0,
            }
        );

        json.Should().Contain("\"sources\":[]");
        json.Should().NotContain("rendered_text");
    }

    [Fact]
    public void A_capability_gated_member_is_absent_rather_than_empty_when_not_granted()
    {
        var withoutContent = new ContextLoadedPayload { RenderedHash = "abc" };
        var withContent = withoutContent with { RenderedText = "# CLAUDE.md" };

        PayloadJson(withoutContent).Should().NotContain("rendered_text");
        PayloadJson(withContent).Should().Contain("\"rendered_text\":\"# CLAUDE.md\"");
    }

    [Fact]
    public void A_partially_populated_correlation_omits_only_the_members_that_do_not_apply()
    {
        var json = LifecycleSerializer.Serialize(
            LifecycleTestData.Minimal() with
            {
                Correlation = new LifecycleCorrelation { ThreadId = "thr-1", RunId = "run-1" },
            }
        );

        json.Should().Contain("\"correlation\":{\"thread_id\":\"thr-1\",\"run_id\":\"run-1\"}");
    }

    [Fact]
    public void An_owner_key_is_never_carried_on_an_envelope()
    {
        var json = LifecycleSerializer.Serialize(LifecycleTestData.Maximal());

        json.Should().NotContain("owner");
        json.Should().NotContain("app_id");
        typeof(LifecycleCorrelation)
            .GetProperties()
            .Should()
            .NotContain(p => p.Name.Contains("Owner", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Numbers_that_are_zero_and_flags_that_are_false_are_still_written()
    {
        var json = PayloadJson(
            new TurnCompletedPayload
            {
                RunId = "run-1",
                GenerationId = "gen-1",
                TurnIndex = 0,
                Outcome = LifecycleTurnOutcomes.Completed,
                MessageCount = 0,
                ToolCallCount = 0,
            }
        );

        json.Should().Contain("\"turn_index\":0");
        json.Should().Contain("\"message_count\":0");
        json.Should().NotContain("usage");
        json.Should().NotContain("error");
    }

    [Fact]
    public void A_payload_projects_back_onto_its_typed_form_unchanged()
    {
        var original = LifecycleTestData.RunStarted();
        var envelope = LifecycleTestData.Maximal();

        LifecycleSerializer.TryReadPayload<RunStartedPayload>(envelope, out var roundTripped)
            .Should()
            .BeTrue();
        roundTripped.Should().Be(original);
    }

    [Fact]
    public void An_envelope_without_a_payload_reports_no_payload_rather_than_throwing()
    {
        LifecycleSerializer
            .TryReadPayload<RunStartedPayload>(LifecycleTestData.Minimal(), out var payload)
            .Should()
            .BeFalse();
        payload.Should().BeNull();
    }

    [Fact]
    public void A_non_object_payload_reports_no_payload_rather_than_throwing()
    {
        var envelope = LifecycleTestData.Minimal() with
        {
            Payload = JsonDocument.Parse("\"not-an-object\"").RootElement.Clone(),
        };

        LifecycleSerializer.TryReadPayload<RunStartedPayload>(envelope, out var payload)
            .Should()
            .BeFalse();
        payload.Should().BeNull();
    }

    [Fact]
    public void A_member_absent_from_a_payload_decodes_to_its_declared_default()
    {
        var envelope = LifecycleTestData.Minimal() with
        {
            EventType = LifecycleEventTypes.ContextLoaded,
            Payload = JsonDocument.Parse("{}").RootElement.Clone(),
        };

        LifecycleSerializer.TryReadPayload<ContextLoadedPayload>(envelope, out var payload)
            .Should()
            .BeTrue();
        payload!.RunId.Should().BeEmpty();
        payload.RenderedHash.Should().BeEmpty();
        payload.Sources.Should().NotBeNull().And.BeEmpty();
        payload.RenderedText.Should().BeNull("it is genuinely optional");
    }

    /// <summary>
    /// Guards the <c>set</c>-not-<c>init</c> rule that <see cref="LifecycleJsonContext"/> documents.
    /// </summary>
    /// <remarks>
    /// Source generation assigns every init-only property from a constructor argument list, so a
    /// member the JSON omits is overwritten with <c>default</c> — and a non-nullable string comes
    /// back null, in defiance of its own annotation. This test decodes a body carrying nothing but
    /// the required members into every contract type, and fails the moment any of them starts lying
    /// about nullability — whether from a member reverting to <c>init</c> or a new type declaring one.
    /// </remarks>
    [Fact]
    public void No_member_declared_non_nullable_ever_decodes_to_null()
    {
        var nullability = new NullabilityInfoContext();
        var types = LifecycleEventTypes
            .Known.Select(LifecycleSerializer.GetPayloadType)
            .Append(typeof(ToolApprovalRequest))
            .Append(typeof(ToolApprovalDecision))
            .Append(typeof(LifecycleCorrelation))
            .ToList();

        types.Should().NotContainNulls().And.HaveCountGreaterThan(6);

        foreach (var type in types)
        {
            // A member the contract marks required can never be absent, so the body carries exactly
            // those and nothing else. Everything left is optional, and that is what is under test.
            var decoded = JsonSerializer.Deserialize(
                OnlyRequiredMembers(type!),
                type!,
                LifecycleSerializer.Options
            );
            decoded.Should().NotBeNull();

            foreach (var property in type!.GetProperties())
            {
                if (
                    nullability.Create(property).ReadState == NullabilityState.Nullable
                    || property.PropertyType.IsValueType
                    || property.IsDefined(typeof(JsonRequiredAttribute), inherit: true)
                )
                {
                    continue;
                }

                property
                    .GetValue(decoded)
                    .Should()
                    .NotBeNull(
                        "{0}.{1} is declared non-nullable and must survive an absent member",
                        type.Name,
                        property.Name
                    );
            }
        }
    }

    /// <summary>Builds the smallest body a type accepts: its required members and nothing else.</summary>
    private static string OnlyRequiredMembers(Type type)
    {
        var members = type.GetProperties()
            .Where(p => p.IsDefined(typeof(JsonRequiredAttribute), inherit: true))
            .Select(p =>
                $"\"{p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name}\":{Placeholder(p.PropertyType)}"
            );

        return "{" + string.Join(",", members) + "}";
    }

    private static string Placeholder(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target == typeof(string))
        {
            return "\"placeholder\"";
        }

        if (target == typeof(DateTimeOffset))
        {
            return "\"2026-07-27T08:30:00.0000000Z\"";
        }

        if (target == typeof(bool))
        {
            return "false";
        }

        if (target.IsPrimitive)
        {
            return "0";
        }

        throw new NotSupportedException(
            $"A newly required member of type {target.Name} needs a placeholder here."
        );
    }

    [Fact]
    public void Usage_counts_carry_their_completeness_so_a_partial_total_is_not_read_as_final()
    {
        var json = PayloadJson(
            new RunCompletedPayload
            {
                RunId = "run-1",
                GenerationId = "gen-1",
                Outcome = LifecycleRunOutcomes.Completed,
                TurnCount = 3,
                Usage = new LifecycleUsage
                {
                    PromptTokens = 10,
                    CompletionTokens = 20,
                    TotalTokens = 30,
                    Completeness = LifecycleUsageCompleteness.Partial,
                },
            }
        );

        json.Should().Contain("\"completeness\":\"partial\"");
        json.Should().NotContain("cached_prompt_tokens");
        json.Should().NotContain("reasoning_tokens");
    }
}
