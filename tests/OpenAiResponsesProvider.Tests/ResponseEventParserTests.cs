using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
/// Round-trip and discriminator-shape tests for the JSON↔record event parser. Covers every
/// modeled subtype plus the generic fallback so the parser can never silently drop fields when
/// the wire grows.
/// </summary>
public sealed class ResponseEventParserTests
{
    [Fact]
    public void Parse_response_created_lifecycle_event()
    {
        const string json = """
            {
              "type": "response.created",
              "sequence_number": 0,
              "response": { "id": "resp_abc", "status": "in_progress", "model": "gpt-test" }
            }
            """;

        var ev = ResponseEventParser.Parse(json);

        var lifecycle = ev.Should().BeOfType<ResponseLifecycleEvent>().Subject;
        lifecycle.Type.Should().Be(ResponseEventTypes.ResponseCreated);
        lifecycle.SequenceNumber.Should().Be(0);
        lifecycle.Response.GetProperty("id").GetString().Should().Be("resp_abc");
    }

    [Fact]
    public void Parse_output_text_delta_carries_index_and_delta()
    {
        const string json = """
            {
              "type": "response.output_text.delta",
              "sequence_number": 7,
              "item_id": "msg_1",
              "output_index": 0,
              "content_index": 0,
              "delta": "hello"
            }
            """;

        var ev = ResponseEventParser.Parse(json);

        var delta = ev.Should().BeOfType<ResponseOutputTextDeltaEvent>().Subject;
        delta.ItemId.Should().Be("msg_1");
        delta.OutputIndex.Should().Be(0);
        delta.ContentIndex.Should().Be(0);
        delta.Delta.Should().Be("hello");
    }

    [Fact]
    public void Parse_function_call_arguments_done_carries_args_string()
    {
        const string json = """
            {
              "type": "response.function_call_arguments.done",
              "sequence_number": 12,
              "item_id": "fc_1",
              "output_index": 1,
              "arguments": "{\"x\":1}"
            }
            """;

        var ev = ResponseEventParser.Parse(json);

        var done = ev.Should().BeOfType<ResponseFunctionCallArgumentsDoneEvent>().Subject;
        done.ItemId.Should().Be("fc_1");
        done.OutputIndex.Should().Be(1);
        done.Arguments.Should().Be("{\"x\":1}");
    }

    [Fact]
    public void Parse_unknown_type_yields_generic_event_preserving_extras()
    {
        const string json = """
            {
              "type": "response.web_search_call.in_progress",
              "sequence_number": 3,
              "item_id": "ws_1",
              "output_index": 0
            }
            """;

        var ev = ResponseEventParser.Parse(json);

        var generic = ev.Should().BeOfType<GenericResponseEvent>().Subject;
        generic.Type.Should().Be("response.web_search_call.in_progress");
        generic.SequenceNumber.Should().Be(3);
        generic.ExtraProperties.Should().NotBeNull();
        generic.ExtraProperties!.Should().ContainKey("item_id");
        generic.ExtraProperties.Should().ContainKey("output_index");
    }

    [Fact]
    public void Parse_throws_on_missing_type_discriminator()
    {
        const string json = """{"sequence_number": 0}""";

        var act = () => ResponseEventParser.Parse(json);

        _ = act.Should().Throw<JsonException>()
            .WithMessage("*type*");
    }

    [Theory]
    [InlineData(ResponseEventTypes.OutputItemAdded)]
    [InlineData(ResponseEventTypes.OutputItemDone)]
    public void OutputItem_events_round_trip_through_ToJsonObject(string type)
    {
        var original = new ResponseOutputItemEvent
        {
            Type = type,
            SequenceNumber = 5,
            OutputIndex = 2,
            Item = JsonDocument.Parse("""{"id":"msg_x","type":"message"}""").RootElement,
        };

        var jsonObj = ResponseEventParser.ToJsonObject(original);
        var roundTripped = ResponseEventParser.Parse(jsonObj);

        var roundtripItem = roundTripped.Should().BeOfType<ResponseOutputItemEvent>().Subject;
        roundtripItem.Type.Should().Be(type);
        roundtripItem.OutputIndex.Should().Be(2);
        roundtripItem.Item.GetProperty("id").GetString().Should().Be("msg_x");
    }

    [Fact]
    public void TextDelta_round_trips_via_ToJsonObject()
    {
        var original = new ResponseOutputTextDeltaEvent
        {
            Type = ResponseEventTypes.OutputTextDelta,
            SequenceNumber = 8,
            ItemId = "msg_a",
            OutputIndex = 0,
            ContentIndex = 0,
            Delta = "world",
        };

        var roundTripped = ResponseEventParser.Parse(ResponseEventParser.ToJsonObject(original));

        roundTripped.Should().BeOfType<ResponseOutputTextDeltaEvent>().Which.Delta.Should().Be("world");
    }

    [Fact]
    public void GenericEvent_round_trips_extras_via_ToJsonObject()
    {
        var original = new GenericResponseEvent
        {
            Type = "codex.rate_limits",
            SequenceNumber = 99,
            ExtraProperties = new JsonObject
            {
                ["limits"] = JsonNode.Parse("""{"remaining":10}"""),
            },
        };

        var roundTripped = ResponseEventParser.Parse(ResponseEventParser.ToJsonObject(original));

        var generic = roundTripped.Should().BeOfType<GenericResponseEvent>().Subject;
        generic.Type.Should().Be("codex.rate_limits");
        generic.ExtraProperties.Should().NotBeNull();
        generic.ExtraProperties!["limits"]!["remaining"]!.GetValue<int>().Should().Be(10);
    }
}
