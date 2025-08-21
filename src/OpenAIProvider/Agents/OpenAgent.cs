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
    private readonly ILogger<OpenClientAgent> _logger;

    public OpenClientAgent(
        string name,
        IOpenClient client,
        ILogger<OpenClientAgent>? logger = null)
    {
        _client = client;
        Name = name;
        _logger = logger ?? NullLogger<OpenClientAgent>.Instance;
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
        var startTime = DateTime.UtcNow;

        _logger.LogDebug("Request preparation details: Model={Model}, Temperature={Temperature}, MaxTokens={MaxTokens}, Stream={Stream}, ToolCount={ToolCount}",
            request.Model, request.Temperature, request.MaxTokens, request.Stream, request.Tools?.Count ?? 0);

        _logger.LogInformation("LLM request initiated: Model={Model}, Agent={AgentName}, MessageCount={MessageCount}, Type={RequestType}",
            request.Model, Name, request.Messages.Count, "non-streaming");

        ChatCompletionResponse response;
        try
        {
            response = await _client.CreateChatCompletionsAsync(
                request,
                cancellationToken)!;
        }
        catch (Exception ex)
        {
            var errorDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogError(ex, "API call failed: Model={Model}, Agent={AgentName}, Duration={Duration}ms, ExceptionType={ExceptionType}, Message={Message}",
                request.Model, Name, errorDuration, ex.GetType().Name, ex.Message);
            throw;
        }

        var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

        _logger.LogDebug("Response processing: CompletionId={CompletionId}, ChoiceCount={ChoiceCount}, HasUsage={HasUsage}, ResponseModel={ResponseModel}",
            response.Id, response.Choices?.Count ?? 0, response.Usage != null, response.Model);

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

                _logger.LogDebug("Cost extracted from response - CompletionId: {CompletionId}, EstimatedCost: {EstimatedCost}",
                    response.Id, totalCost);
            }
            else
            {
                _logger.LogDebug("No cost data in response extra properties - CompletionId: {CompletionId}, HasUsage: {HasUsage}, HasExtraProperties: {HasExtraProperties}",
                    response.Id,
                    response.Usage != null,
                    response.Usage?.ExtraProperties != null);
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

            // Calculate tokens per second
            var tokensPerSecond = openUsage.CompletionTokens > 0 && duration > 0
                ? (openUsage.CompletionTokens / (duration / 1000.0))
                : 0.0;

            _logger.LogInformation("LLM request completed: CompletionId={CompletionId}, Model={Model}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalCost={TotalCost:F6}, Duration={Duration}ms, TokensPerSecond={TokensPerSecond:F2}",
                openMessage.CompletionId, openUsage.ModelId, openUsage.PromptTokens, openUsage.CompletionTokens, openUsage.TotalCost ?? 0.0, duration, tokensPerSecond);

            var resultMessages = openMessage.ToMessages();

            _logger.LogDebug("Message conversion details: CompletionId={CompletionId}, ConvertedMessageCount={MessageCount}, HasToolCalls={HasToolCalls}",
                openMessage.CompletionId, resultMessages.Count(), openMessage.ChatMessage.ToolCalls?.Any() == true);

            return resultMessages;
        }
        else
        {
            _logger.LogWarning("Missing usage data: CompletionId={CompletionId}, Model={Model}, Agent={AgentName}, ChoiceCount={ChoiceCount}, ResponseHasId={ResponseHasId}",
                 response.Id, response.Model, Name, response.Choices?.Count ?? 0, !string.IsNullOrEmpty(response.Id));

            var openMessage = new OpenMessage
            {
                CompletionId = response.Id!,
                ChatMessage = response
                    .Choices!
                    .First()
                    .Message!,
            };

            var resultMessages = openMessage.ToMessages();
            _logger.LogDebug("Message conversion details (no usage): CompletionId={CompletionId}, ConvertedMessageCount={MessageCount}, HasToolCalls={HasToolCalls}",
                openMessage.CompletionId, resultMessages.Count(), openMessage.ChatMessage.ToolCalls?.Any() == true);

            return resultMessages;
        }
    }

    public virtual async Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
        IEnumerable<IMessage> messages,
        GenerateReplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var request = ChatCompletionRequest.FromMessages(messages, options) with
        { Stream = true };

        _logger.LogDebug("Streaming request preparation: Model={Model}, Temperature={Temperature}, MaxTokens={MaxTokens}, Stream={Stream}, ToolCount={ToolCount}",
            request.Model, request.Temperature, request.MaxTokens, request.Stream, request.Tools?.Count ?? 0);

        _logger.LogInformation("LLM request initiated: Model={Model}, Agent={AgentName}, MessageCount={MessageCount}, Type={RequestType}",
            request.Model, Name, request.Messages.Count, "streaming");

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
        var startTime = DateTime.UtcNow;
        DateTime? firstTokenTime = null;
        int totalPromptTokens = 0;
        int totalCompletionTokens = 0;
        double? totalCost = null;

        await foreach (var item in response)
        {
            modelId = modelId.Length == 0 ? item.Model ?? modelId : modelId;
            completionId = completionId.Length == 0
                ? item.Id!
                : completionId;

            totalChunks++;

            // Track first token time
            if (firstTokenTime == null
                && item.Choices?.Any(c => c.Delta != null) == true)
            {
                firstTokenTime = DateTime.UtcNow;
            }

            if (item.Usage != null)
            {
                hasUsageData = true;
                totalPromptTokens = item.Usage.PromptTokens;
                totalCompletionTokens = item.Usage.CompletionTokens;

                // Extract cost if available
                if (item.Usage.ExtraProperties?.TryGetValue("estimated_cost", out var costValue) == true)
                {
                    totalCost = costValue switch
                    {
                        JsonElement element => element.GetDouble(),
                        double value => value,
                        _ => totalCost
                    };
                }

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

            var streamingMessages = openMessage.ToStreamingMessage();
            _logger.LogDebug(
                "Streaming message processing: CompletionId={CompletionId}, ChunkNumber={ChunkNumber}, HasContent={HasContent}, MessageCount={MessageCount}",
                completionId,
                totalChunks,
                !string.IsNullOrEmpty(
                    item.Choices?.First()?.Delta?.Content ?? "NULL"),
                streamingMessages.Count());

            foreach (var message in streamingMessages)
            {
                yield return message;
            }
        }

        var totalDuration = (DateTime.UtcNow - startTime).TotalMilliseconds;
        var timeToFirstToken = firstTokenTime.HasValue ? (firstTokenTime.Value - startTime).TotalMilliseconds : 0.0;
        var tokensPerSecond = totalCompletionTokens > 0 && totalDuration > 0
            ? (totalCompletionTokens / (totalDuration / 1000.0))
            : 0.0;

        _logger.LogDebug("Streaming metrics: CompletionId={CompletionId}, TotalChunks={TotalChunks}, TimeToFirstToken={TimeToFirstToken}ms, TokensPerSecond={TokensPerSecond:F2}, HadUsageData={HadUsageData}",
            completionId, totalChunks, timeToFirstToken, tokensPerSecond, hasUsageData);

        if (!hasUsageData)
        {
            _logger.LogWarning("Missing usage data in streaming response: CompletionId={CompletionId}, Model={Model}, Agent={AgentName}, TotalChunks={TotalChunks}",
                completionId, modelId, Name, totalChunks);
        }

        _logger.LogInformation("LLM request completed: CompletionId={CompletionId}, Model={Model}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, TotalCost={TotalCost:F6}, Duration={Duration}ms, TokensPerSecond={TokensPerSecond:F2}, TimeToFirstToken={TimeToFirstToken}ms, TotalChunks={TotalChunks}",
            completionId, modelId, totalPromptTokens, totalCompletionTokens, totalCost ?? 0.0, totalDuration, tokensPerSecond, timeToFirstToken, totalChunks);
    }
}