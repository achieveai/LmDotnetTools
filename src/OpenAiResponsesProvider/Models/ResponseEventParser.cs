using System.Text.Json;
using System.Text.Json.Nodes;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;

/// <summary>
///     Parses raw JSON event objects from the OpenAI Responses API event stream into typed
///     <see cref="ResponseEvent"/> records. Used by the provider on both the SSE and WebSocket
///     transports — the surrounding framing (SSE <c>data:</c> envelope vs. raw WS text frame)
///     is the caller's responsibility.
/// </summary>
public static class ResponseEventParser
{
    /// <summary>
    ///     Parses a single JSON event object. Throws <see cref="JsonException"/> if the
    ///     payload is malformed or missing the <c>type</c> discriminator.
    /// </summary>
    public static ResponseEvent Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(JsonNode.Parse(json) ?? throw new JsonException("Empty JSON payload"));
    }

    /// <summary>Parses a pre-parsed <see cref="JsonNode"/> event payload.</summary>
    public static ResponseEvent Parse(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node is not JsonObject obj)
        {
            throw new JsonException("Response event payload must be a JSON object");
        }

        var type = obj["type"]?.GetValue<string>()
            ?? throw new JsonException("Response event payload missing 'type' discriminator");

        var seq = obj["sequence_number"] is JsonNode s && s.GetValueKind() == JsonValueKind.Number
            ? s.GetValue<int>()
            : (int?)null;

        return type switch
        {
            ResponseEventTypes.ResponseCreated
                or ResponseEventTypes.ResponseInProgress
                or ResponseEventTypes.ResponseCompleted
                or ResponseEventTypes.ResponseFailed =>
                    new ResponseLifecycleEvent
                    {
                        Type = type,
                        SequenceNumber = seq,
                        Response = ToElement(obj["response"]),
                    },

            ResponseEventTypes.OutputItemAdded
                or ResponseEventTypes.OutputItemDone =>
                    new ResponseOutputItemEvent
                    {
                        Type = type,
                        SequenceNumber = seq,
                        OutputIndex = obj["output_index"]?.GetValue<int>() ?? 0,
                        Item = ToElement(obj["item"]),
                    },

            ResponseEventTypes.ContentPartAdded
                or ResponseEventTypes.ContentPartDone =>
                    new ResponseContentPartEvent
                    {
                        Type = type,
                        SequenceNumber = seq,
                        ItemId = obj["item_id"]?.GetValue<string>() ?? string.Empty,
                        OutputIndex = obj["output_index"]?.GetValue<int>() ?? 0,
                        ContentIndex = obj["content_index"]?.GetValue<int>() ?? 0,
                        Part = ToElement(obj["part"]),
                    },

            ResponseEventTypes.OutputTextDelta =>
                new ResponseOutputTextDeltaEvent
                {
                    Type = type,
                    SequenceNumber = seq,
                    ItemId = obj["item_id"]?.GetValue<string>() ?? string.Empty,
                    OutputIndex = obj["output_index"]?.GetValue<int>() ?? 0,
                    ContentIndex = obj["content_index"]?.GetValue<int>() ?? 0,
                    Delta = obj["delta"]?.GetValue<string>() ?? string.Empty,
                },

            ResponseEventTypes.OutputTextDone =>
                new ResponseOutputTextDoneEvent
                {
                    Type = type,
                    SequenceNumber = seq,
                    ItemId = obj["item_id"]?.GetValue<string>() ?? string.Empty,
                    OutputIndex = obj["output_index"]?.GetValue<int>() ?? 0,
                    ContentIndex = obj["content_index"]?.GetValue<int>() ?? 0,
                    Text = obj["text"]?.GetValue<string>() ?? string.Empty,
                },

            ResponseEventTypes.FunctionCallArgumentsDelta =>
                new ResponseFunctionCallArgumentsDeltaEvent
                {
                    Type = type,
                    SequenceNumber = seq,
                    ItemId = obj["item_id"]?.GetValue<string>() ?? string.Empty,
                    OutputIndex = obj["output_index"]?.GetValue<int>() ?? 0,
                    Delta = obj["delta"]?.GetValue<string>() ?? string.Empty,
                },

            ResponseEventTypes.FunctionCallArgumentsDone =>
                new ResponseFunctionCallArgumentsDoneEvent
                {
                    Type = type,
                    SequenceNumber = seq,
                    ItemId = obj["item_id"]?.GetValue<string>() ?? string.Empty,
                    OutputIndex = obj["output_index"]?.GetValue<int>() ?? 0,
                    Arguments = obj["arguments"]?.GetValue<string>() ?? string.Empty,
                },

            _ => new GenericResponseEvent
            {
                Type = type,
                SequenceNumber = seq,
                ExtraProperties = ExtraExcludingKnown(obj),
            },
        };
    }

    /// <summary>
    ///     Converts a typed event back into a <see cref="JsonObject"/> payload suitable for
    ///     emission over either transport. The payload is round-trippable through
    ///     <see cref="Parse(JsonNode)"/>.
    /// </summary>
    public static JsonObject ToJsonObject(ResponseEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        var obj = new JsonObject { ["type"] = ev.Type };
        if (ev.SequenceNumber.HasValue)
        {
            obj["sequence_number"] = ev.SequenceNumber.Value;
        }

        switch (ev)
        {
            case ResponseLifecycleEvent lifecycle:
                obj["response"] = JsonNode.Parse(lifecycle.Response.GetRawText());
                break;
            case ResponseOutputItemEvent item:
                obj["output_index"] = item.OutputIndex;
                obj["item"] = JsonNode.Parse(item.Item.GetRawText());
                break;
            case ResponseContentPartEvent part:
                obj["item_id"] = part.ItemId;
                obj["output_index"] = part.OutputIndex;
                obj["content_index"] = part.ContentIndex;
                obj["part"] = JsonNode.Parse(part.Part.GetRawText());
                break;
            case ResponseOutputTextDeltaEvent delta:
                obj["item_id"] = delta.ItemId;
                obj["output_index"] = delta.OutputIndex;
                obj["content_index"] = delta.ContentIndex;
                obj["delta"] = delta.Delta;
                break;
            case ResponseOutputTextDoneEvent done:
                obj["item_id"] = done.ItemId;
                obj["output_index"] = done.OutputIndex;
                obj["content_index"] = done.ContentIndex;
                obj["text"] = done.Text;
                break;
            case ResponseFunctionCallArgumentsDeltaEvent fcd:
                obj["item_id"] = fcd.ItemId;
                obj["output_index"] = fcd.OutputIndex;
                obj["delta"] = fcd.Delta;
                break;
            case ResponseFunctionCallArgumentsDoneEvent fcdone:
                obj["item_id"] = fcdone.ItemId;
                obj["output_index"] = fcdone.OutputIndex;
                obj["arguments"] = fcdone.Arguments;
                break;
            case GenericResponseEvent generic when generic.ExtraProperties is { } extras:
                foreach (var kvp in extras)
                {
                    obj[kvp.Key] = kvp.Value?.DeepClone();
                }

                break;
            default:
                // No additional properties for unhandled event subtypes; the discriminator and
                // sequence_number have already been written above.
                break;
        }

        return obj;
    }

    private static JsonElement ToElement(JsonNode? node)
    {
        if (node is null)
        {
            using var doc = JsonDocument.Parse("null");
            return doc.RootElement.Clone();
        }

        using var doc2 = JsonDocument.Parse(node.ToJsonString());
        return doc2.RootElement.Clone();
    }

    private static JsonObject? ExtraExcludingKnown(JsonObject obj)
    {
        JsonObject? extras = null;
        foreach (var kvp in obj)
        {
            if (kvp.Key is "type" or "sequence_number")
            {
                continue;
            }

            extras ??= [];
            extras[kvp.Key] = kvp.Value?.DeepClone();
        }

        return extras;
    }
}
