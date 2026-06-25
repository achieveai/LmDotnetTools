using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;

/// <summary>
///     Maps repo-native messages and <see cref="GenerateReplyOptions"/> onto a
///     <see cref="ResponseCreateRequest"/>. Internal so tests can exercise the mapping
///     without spinning up an agent.
/// </summary>
internal static class MessageMapper
{
    internal static ResponseCreateRequest BuildRequest(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options
    )
    {
        var instructionsBuilder = new StringBuilder();
        var inputItems = new List<ResponseInputItem>();

        foreach (var message in messages)
        {
            MapMessage(message, instructionsBuilder, inputItems);
        }

        // Reasoning-capable models only return reasoning summaries when asked. Mirror the Anthropic
        // "Thinking" convention: a ResponseReasoningOptions placed in ExtraProperties["Reasoning"]
        // (e.g. { Summary = "auto" }) is mapped onto the request so thinking blocks come back.
        ResponseReasoningOptions? reasoning = null;
        if (
            options?.ExtraProperties != null
            && options.ExtraProperties.TryGetValue("Reasoning", out var reasoningObj)
            && reasoningObj is ResponseReasoningOptions reasoningValue
        )
        {
            reasoning = reasoningValue;
        }

        IReadOnlyList<ResponseToolSpec>? tools = null;
        if (options?.Functions is { Length: > 0 } functions)
        {
            tools =
            [
                .. functions.Select(fc => new ResponseToolSpec
                {
                    Type = "function",
                    Name = fc.Name,
                    Description = fc.Description,
                    Parameters = BuildParametersSchema(fc),
                }),
            ];
        }

        return new ResponseCreateRequest
        {
            Model = string.IsNullOrEmpty(options?.ModelId) ? null : options.ModelId,
            Instructions = instructionsBuilder.Length == 0 ? null : instructionsBuilder.ToString(),
            Input = inputItems,
            Stream = true,
            Temperature = options?.Temperature,
            TopP = options?.TopP,
            MaxOutputTokens = options?.MaxToken,
            Tools = tools,
            ToolChoice = options?.ToolChoice is null ? null : JsonValue.Create(options.ToolChoice),
            Reasoning = reasoning,
        };
    }

    // Options that know how to serialize JsonSchemaObject (notably its Union-typed "type" field).
    private static readonly JsonSerializerOptions s_schemaSerializerOptions = JsonSerializerOptionsFactory.CreateBase(false);

    /// <summary>
    ///     Builds the JSON Schema object the Responses API expects for a function tool's
    ///     <c>parameters</c> field: <c>{ "type": "object", "properties": { … }, "required": [ … ] }</c>.
    ///     The contract's parameter <em>list</em> must not be serialized directly — that yields a JSON
    ///     array, which the API rejects with "expected an object, but got an array".
    /// </summary>
    private static JsonNode? BuildParametersSchema(FunctionContract contract)
    {
        var properties = new Dictionary<string, JsonSchemaObject>(StringComparer.Ordinal);
        var required = new List<string>();

        if (contract.Parameters is not null)
        {
            foreach (var parameter in contract.Parameters)
            {
                if (string.IsNullOrEmpty(parameter.Name) || parameter.ParameterType is null)
                {
                    continue;
                }

                // Carry the parameter description onto the property schema when it has none of its own.
                var propertySchema = parameter.ParameterType.Description is null && parameter.Description is not null
                    ? parameter.ParameterType with { Description = parameter.Description }
                    : parameter.ParameterType;

                properties[parameter.Name] = propertySchema;
                if (parameter.IsRequired)
                {
                    required.Add(parameter.Name);
                }
            }
        }

        var schema = new JsonSchemaObject
        {
            Type = JsonSchemaTypeHelper.ToType("object"),
            Properties = properties,
            Required = required.Count > 0 ? required : null,
            AdditionalProperties = false,
        };

        return JsonSerializer.SerializeToNode(schema, s_schemaSerializerOptions);
    }

    /// <summary>
    ///     Maps a single repo-native message onto Responses API input items (or instructions),
    ///     appending to the supplied buffers. Recurses into container messages.
    /// </summary>
    /// <remarks>
    ///     The multi-turn loop groups an assistant turn's parts into a <see cref="CompositeMessage"/>
    ///     and bundles a tool call with its result into a <see cref="ToolsCallAggregateMessage"/>.
    ///     Both must be unwrapped — dropping them (the original behaviour) hid the tool call + result
    ///     from the model on continuation turns, so it re-issued the same call indefinitely.
    /// </remarks>
    private static void MapMessage(IMessage message, StringBuilder instructions, List<ResponseInputItem> inputItems)
    {
        switch (message)
        {
            case CompositeMessage composite:
                foreach (var inner in composite.Messages)
                {
                    MapMessage(inner, instructions, inputItems);
                }

                break;

            case ToolsCallAggregateMessage aggregate:
                MapMessage(aggregate.ToolsCallMessage, instructions, inputItems);
                MapMessage(aggregate.ToolsCallResult, instructions, inputItems);
                break;

            case TextMessage text when text.Role == Role.System:
                if (instructions.Length > 0)
                {
                    _ = instructions.Append('\n');
                }

                _ = instructions.Append(text.Text);
                break;

            case TextMessage text:
                inputItems.Add(
                    new ResponseInputItem
                    {
                        Type = "message",
                        Role = MapRole(text.Role),
                        Content =
                        [
                            new ResponseInputContent
                            {
                                Type = text.Role == Role.Assistant ? "output_text" : "input_text",
                                Text = text.Text,
                            },
                        ],
                    }
                );
                break;

            case ToolsCallMessage toolsCall:
                foreach (var call in toolsCall.ToolCalls)
                {
                    // A replayed assistant tool call must carry name + arguments, not just call_id —
                    // the Responses API rejects a function_call input item that omits them.
                    inputItems.Add(
                        new ResponseInputItem
                        {
                            Type = "function_call",
                            CallId = call.ToolCallId,
                            Name = call.FunctionName,
                            Arguments = string.IsNullOrEmpty(call.FunctionArgs) ? "{}" : call.FunctionArgs,
                            Content = null,
                        }
                    );
                }

                break;

            case ToolsCallResultMessage results:
                foreach (var result in results.ToolCallResults)
                {
                    inputItems.Add(
                        new ResponseInputItem
                        {
                            Type = "function_call_output",
                            CallId = result.ToolCallId,
                            Output = result.Result,
                        }
                    );
                }

                break;

            case ToolCallMessage singleCall:
                inputItems.Add(
                    new ResponseInputItem
                    {
                        Type = "function_call",
                        CallId = singleCall.ToolCallId,
                        Name = singleCall.FunctionName,
                        Arguments = string.IsNullOrEmpty(singleCall.FunctionArgs) ? "{}" : singleCall.FunctionArgs,
                        Content = null,
                    }
                );
                break;

            case ToolCallResultMessage singleResult:
                inputItems.Add(
                    new ResponseInputItem
                    {
                        Type = "function_call_output",
                        CallId = singleResult.ToolCallId,
                        Output = singleResult.Result,
                    }
                );
                break;

            default:
                // Unsupported message types (e.g. UsageMessage on the input side) have no Responses
                // input-side analog and are intentionally dropped.
                break;
        }
    }

    private static string MapRole(Role role)
    {
        return role switch
        {
            Role.User => "user",
            Role.Assistant => "assistant",
            Role.System => "system",
            Role.Tool => "tool",
            _ => "user",
        };
    }
}
