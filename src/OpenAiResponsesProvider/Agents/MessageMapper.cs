using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
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
            switch (message)
            {
                case TextMessage text when text.Role == Role.System:
                    if (instructionsBuilder.Length > 0)
                    {
                        _ = instructionsBuilder.Append('\n');
                    }

                    _ = instructionsBuilder.Append(text.Text);
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
                        inputItems.Add(
                            new ResponseInputItem
                            {
                                Type = "function_call",
                                CallId = call.ToolCallId,
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

                default:
                    // Unsupported message types (e.g. UsageMessage on the input side, or
                    // forwarded provider-specific frames) are dropped from the request — the
                    // Responses API has no input-side analog and including them would cause a
                    // 400.
                    break;
            }
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
                    Parameters = fc.Parameters is null
                        ? null
                        : JsonNode.Parse(JsonSerializer.Serialize(fc.Parameters)),
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
        };
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
