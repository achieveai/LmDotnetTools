using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;

/// <summary>
/// Converts AG-UI protocol messages to LmCore format (inbound conversion)
/// </summary>
public class AgUiToLmCoreConverter : IAgUiToLmCoreConverter
{
    private readonly ILogger<AgUiToLmCoreConverter> _logger;

    public AgUiToLmCoreConverter(ILogger<AgUiToLmCoreConverter>? logger = null)
    {
        _logger = logger ?? NullLogger<AgUiToLmCoreConverter>.Instance;
    }

    /// <inheritdoc/>
    public IMessage ConvertMessage(DataObjects.DTOs.Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Determine message type based on AG-UI message properties
        if (message.Role == "tool")
        {
            return ConvertToolResultMessage(message);
        }
        else
        {
            return message.ToolCalls?.Count > 0 ? ConvertToolsCallMessage(message) : ConvertTextMessage(message);
        }
    }

    /// <inheritdoc/>
    public ImmutableList<IMessage> ConvertMessageHistory(ImmutableList<DataObjects.DTOs.Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var result = ImmutableList.CreateBuilder<IMessage>();

        foreach (var message in messages)
        {
            try
            {
                result.Add(ConvertMessage(message));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert AG-UI message {MessageId}", message.Id);
            }
        }

        return result.ToImmutable();
    }

    /// <inheritdoc/>
    public ToolCall ConvertToolCall(DataObjects.DTOs.ToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        // Serialize JsonElement to JSON string
        var argumentsJson = JsonSerializer.Serialize(toolCall.Function.Arguments);

        return new ToolCall
        {
            FunctionName = toolCall.Function.Name,
            FunctionArgs = argumentsJson,
            ToolCallId = toolCall.Id,
        };
    }

    /// <inheritdoc/>
    public (IEnumerable<IMessage> messages, GenerateReplyOptions options) ConvertRunAgentInput(
        DataObjects.DTOs.RunAgentInput input,
        IEnumerable<FunctionContract>? availableFunctions = null
    )
    {
        ArgumentNullException.ThrowIfNull(input);

        // Convert history
        var messages = input.History != null ? ConvertMessageHistory(input.History).ToList() : [];

        // Create new user message from input.Message
        var userMessage = new TextMessage
        {
            Text = input.Message,
            Role = Role.User,
            GenerationId = Guid.NewGuid().ToString(),
            Metadata = input.Context, // Merge context as metadata
        };
        messages.Add(userMessage);

        // Convert configuration to options
        var options = ConvertConfiguration(input.Configuration, availableFunctions);

        return (messages, options);
    }

    private static TextMessage ConvertTextMessage(DataObjects.DTOs.Message message)
    {
        return new TextMessage
        {
            Text = message.Content ?? string.Empty,
            Role = ConvertRole(message.Role),
            GenerationId = message.Id,
            FromAgent = message.Name,
        };
    }

    private ToolsCallMessage ConvertToolsCallMessage(DataObjects.DTOs.Message message)
    {
        var toolCalls = message.ToolCalls!.Select(ConvertToolCall).ToImmutableList();

        return new ToolsCallMessage
        {
            Role = ConvertRole(message.Role),
            GenerationId = message.Id,
            FromAgent = message.Name,
            ToolCalls = toolCalls,
        };
    }

    private static ToolsCallResultMessage ConvertToolResultMessage(DataObjects.DTOs.Message message)
    {
        var result = new ToolCallResult(ToolCallId: message.ToolCallId, Result: message.Content ?? string.Empty);

        return new ToolsCallResultMessage
        {
            Role = ConvertRole(message.Role),
            GenerationId = message.Id,
            FromAgent = message.Name,
            ToolCallResults = [result],
        };
    }

    private static GenerateReplyOptions ConvertConfiguration(
        DataObjects.DTOs.RunConfiguration? config,
        IEnumerable<FunctionContract>? availableFunctions
    )
    {
        if (config == null)
        {
            return new GenerateReplyOptions();
        }

        var extraProps = ImmutableDictionary.CreateBuilder<string, object?>();

        // Handle ModelParameters
        if (config.ModelParameters != null)
        {
            foreach (var param in config.ModelParameters)
            {
                // These will be handled as first-class properties, skip from extra
                if (param.Key is "top_p" or "seed" or "stop")
                {
                    continue;
                }

                extraProps.Add(param.Key, param.Value);
            }
        }

        // Filter functions by EnabledTools
        FunctionContract[]? functions = null;
        if (config.EnabledTools != null && availableFunctions != null)
        {
            var enabledSet = config.EnabledTools.ToHashSet();
            functions = [.. availableFunctions.Where(f => enabledSet.Contains(f.Name))];
        }

        return new GenerateReplyOptions
        {
            ModelId = config.Model ?? string.Empty,
            Temperature = config.Temperature.HasValue ? (float)config.Temperature.Value : null,
            MaxToken = config.MaxTokens,
            TopP = ExtractFloat(config.ModelParameters, "top_p"),
            RandomSeed = ExtractInt(config.ModelParameters, "seed"),
            StopSequence = ExtractStringArray(config.ModelParameters, "stop"),
            Functions = functions,
            ExtraProperties = extraProps.ToImmutable(),
        };
    }

    private static Role ConvertRole(string role)
    {
        return role?.ToLowerInvariant() switch
        {
            "system" => Role.System,
            "user" => Role.User,
            "assistant" => Role.Assistant,
            "tool" => Role.Tool,
            _ => Role.Assistant,
        };
    }

    private static float? ExtractFloat(Dictionary<string, object>? dict, string key)
    {
        return dict == null || !dict.TryGetValue(key, out var value)
            ? null
            : value switch
            {
                float f => f,
                double d => (float)d,
                int i => (float)i,
                _ => null,
            };
    }

    private static int? ExtractInt(Dictionary<string, object>? dict, string key)
    {
        return dict == null || !dict.TryGetValue(key, out var value)
            ? null
            : value switch
            {
                int i => i,
                long l => (int)l,
                _ => null,
            };
    }

    private static string[]? ExtractStringArray(Dictionary<string, object>? dict, string key)
    {
        return dict == null || !dict.TryGetValue(key, out var value) ? null : value as string[];
    }
}
