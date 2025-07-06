using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Agents;

public class OpenClientAgent : IStreamingAgent, IDisposable
{
    private IOpenClient _client;

    public OpenClientAgent(
        string name,
        IOpenClient client)
    {
        _client = client;
        Name = name;
    }
    public string Name { get; }

    public virtual void Dispose()
    {
        _client.Dispose();
    }

    public virtual async Task<IEnumerable<IMessage>> GenerateReplyAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = ChatCompletionRequest.FromMessages(messages, options);

        var response = await _client.CreateChatCompletionsAsync(
            request,
            cancellationToken)!;

        double? totalCost = null;
        if (response.Usage != null
            && response.Usage.ExtraProperties != null
            && response.Usage.ExtraProperties.ContainsKey("estimated_cost"))
        {
            totalCost = response.Usage.ExtraProperties["estimated_cost"] switch
            {
                JsonElement element => element.GetDouble(),
                double value => value,
                _ => null
            };

            response.Usage = response.Usage.SetExtraProperty("estimated_cost", totalCost);
        }

        var openMessage = new OpenMessage
        {
            CompletionId = response.Id!,
            ChatMessage = response
                .Choices!
                .First()
                .Message!,
            Usage = new OpenUsage
            {
                ModelId = response.Model,
                PromptTokens = response.Usage?.PromptTokens ?? 0,
                CompletionTokens = response.Usage?.CompletionTokens ?? 0,
                TotalCost = totalCost,
            }
        };

        return openMessage.ToMessages();
    }

    public virtual async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = ChatCompletionRequest.FromMessages(messages, options)
            with
        { Stream = true };

        // Return the streaming response as an IAsyncEnumerable
        return await Task.FromResult(GenerateStreamingMessages(request, cancellationToken));
    }

    private async IAsyncEnumerable<IMessage> GenerateStreamingMessages(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = _client.StreamingChatCompletionsAsync(
            request,
            cancellationToken)!;

        string completionId = string.Empty;
        string modelId = string.Empty;
        await foreach (var item in response)
        {
            modelId = modelId.Length == 0 ? item.Model ?? modelId : modelId;
            completionId = completionId.Length == 0
                ? item.Id!
                : completionId;

            var openMessage = new OpenMessage
            {
                CompletionId = completionId,
                ChatMessage = item.Choices != null && item.Choices.Count > 0
                    ? item.Choices!.First().Message ?? item.Choices!.First().Delta!
                    : new ChatMessage()
                    {
                        Content = new(string.Empty)
                    },
                Usage = new OpenUsage
                {
                    ModelId = item.Model,
                    PromptTokens = item.Usage?.PromptTokens ?? 0,
                    CompletionTokens = item.Usage?.CompletionTokens ?? 0,
                }
            };

            foreach (var message in openMessage.ToStreamingMessage())
            {
                yield return message;
            }
        }
    }
}