using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
/// Combines a tool call message and its result into a single message
/// </summary>
public record ToolsCallAggregateMessage : IMessage
{
    /// <summary>
    /// The original tool call message
    /// </summary>
    [JsonPropertyName("tool_call_message")]
    public ICanGetToolCalls ToolCallMessage { get; }
    
    /// <summary>
    /// The result of the tool call
    /// </summary>
    [JsonPropertyName("tool_call_result")]
    public ToolsCallResultMessage ToolCallResult { get; }
    
    /// <summary>
    /// The agent that processed this aggregate message
    /// </summary>
    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; }
    
    /// <summary>
    /// The role of this message (typically Assistant)
    /// </summary>
    [JsonPropertyName("role")]
    public Role Role => Role.Assistant;
    
    /// <summary>
    /// Combined metadata from the tool call and its result
    /// </summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Metadata { get; init; }
    
    /// <summary>
    /// Generation ID from the original tool call
    /// </summary>
    [JsonPropertyName("generation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId => ToolCallMessage.GenerationId;
    
    public ToolsCallAggregateMessage(ICanGetToolCalls toolCallMessage, ToolsCallResultMessage toolCallResult, string? fromAgent = null)
    {
        ToolCallMessage = toolCallMessage;
        ToolCallResult = toolCallResult;
        FromAgent = fromAgent;
        
        // Combine metadata from both messages
        if (toolCallMessage.Metadata != null || toolCallResult.Metadata != null)
        {
            Metadata = new JsonObject();
            
            if (toolCallMessage.Metadata != null)
            {
                foreach (var prop in toolCallMessage.Metadata)
                {
                    Metadata[prop.Key] = prop.Value?.DeepClone();
                }
            }
            
            if (toolCallResult.Metadata != null)
            {
                foreach (var prop in toolCallResult.Metadata)
                {
                    Metadata[prop.Key] = prop.Value?.DeepClone();
                }
            }
        }
    }
    
    // IMessage implementation
    
    public string? GetText()
    {
        // Use text from the result if available
        var resultText = ToolCallResult.GetText();
        if (resultText != null)
        {
            return resultText;
        }
        
        // Otherwise, delegate to the tool call message
        return null;
    }
    
    public BinaryData? GetBinary()
    {
        // Use binary from the result if available
        var resultBinary = ToolCallResult.GetBinary();
        if (resultBinary != null)
        {
            return resultBinary;
        }
        
        // Otherwise, delegate to the tool call message
        return null;
    }
} 