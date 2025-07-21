using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Agents;

public class OpenClientAgent : IStreamingAgent, IDisposable
{
    private IOpenClient _client;
    private readonly ILogger _logger;

    public OpenClientAgent(
        string name,
        IOpenClient client,
        ILogger? logger = null)
    {
        _client = client;
        Name = name;
        _logger = logger ?? NullLogger.Instance;
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
        
        _logger.LogDebug("OpenAgent generating reply - Model: {Model}, Agent: {AgentName}, MessageCount: {MessageCount}", 
            request.Model, Name, request.Messages.Count);

        var response = await _client.CreateChatCompletionsAsync(
            request,
            cancellationToken)!;

        double? totalCost = null;
        Usage? coreUsage = null;
        
        if (response.Usage != null)
        {
            // Convert to core usage for ExtraProperties operations
            coreUsage = response.Usage.ToCoreUsage();
            
            if (coreUsage.ExtraProperties != null
                && coreUsage.ExtraProperties.ContainsKey("estimated_cost"))
            {
                totalCost = coreUsage.ExtraProperties["estimated_cost"] switch
                {
                    JsonElement element => element.GetDouble(),
                    double value => value,
                    _ => null
                };

            response.Usage = response.Usage.SetExtraProperty("estimated_cost", totalCost);
            
            _logger.LogDebug("Cost extracted from response - CompletionId: {CompletionId}, EstimatedCost: {EstimatedCost}", 
                response.Id, totalCost);
        }
        else
        {
            _logger.LogDebug("No cost data in response extra properties - CompletionId: {CompletionId}, HasUsage: {HasUsage}, HasExtraProperties: {HasExtraProperties}", 
                response.Id, response.Usage != null, response.Usage?.ExtraProperties != null);
        }

        var openUsage = new OpenUsage
        {
            ModelId = response.Model,
            PromptTokens = response.Usage?.PromptTokens ?? 0,
            CompletionTokens = response.Usage?.CompletionTokens ?? 0,
            TotalCost = totalCost,
        };

        var openMessage = new OpenMessage
        {
            CompletionId = response.Id!,
            ChatMessage = response
                .Choices!
                .First()
                .Message!,
            Usage = openUsage
        };

        _logger.LogInformation("OpenMessage created - CompletionId: {CompletionId}, Model: {Model}, PromptTokens: {PromptTokens}, CompletionTokens: {CompletionTokens}, TotalCost: {TotalCost}, Agent: {AgentName}", 
            openMessage.CompletionId, openUsage.ModelId, openUsage.PromptTokens, openUsage.CompletionTokens, openUsage.TotalCost, Name);

        var resultMessages = openMessage.ToMessages();
        
        _logger.LogDebug("OpenMessage converted to {MessageCount} IMessage objects - CompletionId: {CompletionId}", 
            resultMessages.Count(), openMessage.CompletionId);

        return resultMessages;
    }

    public virtual async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = ChatCompletionRequest.FromMessages(messages, options)
            with
        { Stream = true };

        _logger.LogDebug("OpenAgent generating streaming reply - Model: {Model}, Agent: {AgentName}, MessageCount: {MessageCount}", 
            request.Model, Name, request.Messages.Count);

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
        int totalChunks = 0;
        bool hasUsageData = false;
        
        await foreach (var item in response)
        {
            modelId = modelId.Length == 0 ? item.Model ?? modelId : modelId;
            completionId = completionId.Length == 0
                ? item.Id!
                : completionId;
            
            totalChunks++;
            
            if (item.Usage != null)
            {
                hasUsageData = true;
                _logger.LogDebug("Usage data in streaming chunk - CompletionId: {CompletionId}, PromptTokens: {PromptTokens}, CompletionTokens: {CompletionTokens}", 
                    completionId, item.Usage.PromptTokens, item.Usage.CompletionTokens);
            }

            var openMessage = new OpenMessage
            {
                CompletionId = completionId,
                ChatMessage = item.Choices!.First().Delta!,
                Usage = item.Usage != null ? new OpenUsage
                {
                    ModelId = modelId,
                    PromptTokens = item.Usage.PromptTokens,
                    CompletionTokens = item.Usage.CompletionTokens,
                    TotalCost = item.Usage.ExtraProperties?.TryGetValue("estimated_cost", out var cost) == true 
                        ? cost as double? 
                        : null,
                } : null
            };

            foreach (var message in openMessage.ToStreamingMessage())
            {
                yield return message;
            }
        }
        
        _logger.LogInformation("Streaming completed - CompletionId: {CompletionId}, Model: {Model}, TotalChunks: {TotalChunks}, HadUsageData: {HadUsageData}, Agent: {AgentName}", 
            completionId, modelId, totalChunks, hasUsageData, Name);
    }
}