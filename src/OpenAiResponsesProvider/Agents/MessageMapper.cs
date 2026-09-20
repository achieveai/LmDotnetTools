using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;

/// <summary>
///     Maps repo-native messages and <see cref="GenerateReplyOptions"/> onto a
///     <see cref="ResponseCreateRequest"/>. Internal so tests can exercise the mapping
///     without spinning up an agent.
/// </summary>
internal static class MessageMapper
{
    /// <summary>
    ///     Hard cap the Responses API enforces on <c>function_call_output.output</c>: anything
    ///     longer is rejected with <c>400 "string too long. Expected maximum length 10485760"</c>
    ///     and fails the whole turn. Results are normally bounded much earlier by
    ///     <see cref="ToolResultLimits.Default"/>; this clamp is the last line of defence for
    ///     history persisted before that bound existed (#694).
    /// </summary>
    private static readonly ToolResultLimits s_functionCallOutputLimit = new() { MaxResultBytes = 10_485_760 };

    internal static ResponseCreateRequest BuildRequest(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options,
        ILogger? logger = null
    )
    {
        var instructionsBuilder = new StringBuilder();
        var inputItems = new List<ResponseInputItem>();

        foreach (var message in messages)
        {
            MapMessage(message, instructionsBuilder, inputItems);
        }

        DropUnpairedFunctionCallOutputs(inputItems, logger);

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

    /// <summary>
    ///     Removes every <c>function_call_output</c> whose <c>call_id</c> has no <c>function_call</c>
    ///     with that id in the SAME request, keeping the relative order of everything else.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     This is a backstop, not the fix for any particular upstream defect. The API rejects the
    ///     whole request with <c>400 "No tool call found for function call output with call_id …"</c>,
    ///     and because the offending history replays on every turn the conversation is wedged
    ///     permanently — a caller can never talk its way out. Anything that edits history (a
    ///     compaction cut, a projection that removes rows by seq) can widow an output, so the mapper
    ///     degrades the request rather than letting one defect become unrecoverable.
    ///     </para>
    ///     <para>
    ///     Only the output side is swept. A <c>function_call</c> with no output is LEGAL — the model
    ///     is allowed to be mid-turn — and dropping it would delete real history to fix a problem
    ///     that does not exist. That asymmetry is also why one pass suffices here, unlike
    ///     <c>MessagePersistenceConverter.DropUnpairedToolMessages</c> (LmMultiTurn), which drops both
    ///     sides and must therefore iterate to a fixed point: removing an output here can never orphan
    ///     anything, because nothing is paired against outputs. That sweep is deliberately NOT reused
    ///     — the provider must not take a dependency on the multi-turn assembly.
    ///     </para>
    ///     <para>
    ///     An output carrying no id at all takes no part in pairing and is kept: it cannot be matched
    ///     either way, and inventing a verdict would silently drop a tool result on a guess.
    ///     </para>
    /// </remarks>
    private static void DropUnpairedFunctionCallOutputs(List<ResponseInputItem> inputItems, ILogger? logger)
    {
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in inputItems)
        {
            if (item.Type == "function_call" && !string.IsNullOrEmpty(item.CallId))
            {
                _ = callIds.Add(item.CallId);
            }
        }

        _ = inputItems.RemoveAll(item =>
        {
            if (
                item.Type != "function_call_output"
                || string.IsNullOrEmpty(item.CallId)
                || callIds.Contains(item.CallId)
            )
            {
                return false;
            }

            logger?.LogWarning(
                "Dropping orphaned function_call_output call_id={CallId}: no matching function_call in this "
                    + "request. The Responses API would reject the whole request with HTTP 400, wedging the "
                    + "conversation; the tool result is omitted instead.",
                item.CallId
            );
            return true;
        });
    }

    // Options that know how to serialize JsonSchemaObject (notably its Union-typed "type" field).
    private static readonly JsonSerializerOptions s_schemaSerializerOptions = JsonSerializerOptionsFactory.CreateBase(
        false
    );

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
                var propertySchema =
                    parameter.ParameterType.Description is null && parameter.Description is not null
                        ? parameter.ParameterType with
                        {
                            Description = parameter.Description,
                        }
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
                            Output = ClampFunctionCallOutput(result.Result),
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
                        Output = ClampFunctionCallOutput(singleResult.Result),
                    }
                );
                break;

            case NotifyMessage notify:
                inputItems.Add(
                    new ResponseInputItem
                    {
                        Type = "message",
                        Role = MapRole(notify.Role),
                        Content =
                        [
                            new ResponseInputContent
                            {
                                Type = notify.Role == Role.Assistant ? "output_text" : "input_text",
                                Text = notify.Text,
                            },
                        ],
                    }
                );
                break;

            case AgentMessage agentMessage:
                // Typed agent-to-agent message: the envelope is the model-facing text. Falling through to
                // the default arm dropped it silently (#688).
                inputItems.Add(
                    new ResponseInputItem
                    {
                        Type = "message",
                        Role = MapRole(agentMessage.Role),
                        Content =
                        [
                            new ResponseInputContent
                            {
                                Type = agentMessage.Role == Role.Assistant ? "output_text" : "input_text",
                                Text = agentMessage.Text,
                            },
                        ],
                    }
                );
                break;

            default:
                // Unsupported message types (e.g. UsageMessage on the input side) have no Responses
                // input-side analog and are intentionally dropped.
                break;
        }
    }

    /// <summary>
    ///     Clamps a persisted tool result to the Responses API's <c>function_call_output.output</c>
    ///     limit. A result the LmCore bound already handled is far below this and passes through
    ///     unchanged; only legacy oversized history gets cut, with the same marker text.
    /// </summary>
    private static string? ClampFunctionCallOutput(string? output) =>
        output == null ? null : s_functionCallOutputLimit.BoundText(output);

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
