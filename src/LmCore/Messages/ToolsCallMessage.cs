using System.Collections.Immutable;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;

public record ToolsCallMessage : IMessage, ICanGetToolCalls
{
    public string? FromAgent { get; init; } = null;

    public Role Role { get; init; } = Role.Assistant;
    
    public JsonObject? Metadata { get; init; } = null;

    public string? GenerationId { get; init; } = null;

    public ImmutableList<ToolCall> ToolCalls { get; init; } = ImmutableList<ToolCall>.Empty;

    public ToolCall? GetToolCalls() => ToolCalls.Count > 0 ? ToolCalls[0] : null;
    
    IEnumerable<ToolCall>? ICanGetToolCalls.GetToolCalls() => ToolCalls.Count > 0 ? ToolCalls : null;
    
    public string? GetText() => null;
    
    public BinaryData? GetBinary() => null;
    
    public IEnumerable<IMessage>? GetMessages() => null;
}

public record ToolsCallUpdateMessage : IMessage
{
    public string? FromAgent { get; init; } = null;

    public Role Role { get; init; } = Role.Assistant;
    
    public JsonObject? Metadata { get; init; } = null;

    public string? GenerationId { get; init; } = null;

    public ImmutableList<ToolCallUpdate> ToolCallUpdates { get; init; } = ImmutableList<ToolCallUpdate>.Empty;
    
    public string? GetText() => null;
    
    public BinaryData? GetBinary() => null;
    
    public ToolCall? GetToolCalls() => null;
    
    public IEnumerable<IMessage>? GetMessages() => null;
}

public class ToolsCallMessageBuilder : IMessageBuilder<ToolsCallMessage, ToolsCallUpdateMessage>
{
    public string? FromAgent { get; init; } = null;

    public Role Role { get; init; } = Role.Assistant;
    
    public JsonObject? Metadata { get; private set; } = null;

    public string? GenerationId { get; init; } = null;

    private ImmutableList<ToolCall> _completedToolCalls = ImmutableList<ToolCall>.Empty;
    
    // Current partial tool call we're building
    private string? _currentFunctionName = null;
    private string _accumulatedArgs = "";
    
    public void Add(ToolsCallUpdateMessage streamingMessageUpdate)
    {
        // Process each update
        foreach (var update in streamingMessageUpdate.ToolCallUpdates)
        {
            // If update contains a function name, it's the start of a new tool call
            if (update.FunctionName != null)
            {
                // Complete any existing partial update first
                if (_currentFunctionName != null)
                {
                    TryCompletePartialUpdate();
                }
                
                // Start a new partial update
                _currentFunctionName = update.FunctionName;
                _accumulatedArgs = update.FunctionArgs ?? "";
            }
            // Otherwise, it's an update to the current partial tool call
            else if (_currentFunctionName != null && update.FunctionArgs != null)
            {
                _accumulatedArgs += update.FunctionArgs;
            }
        }
        
        // Merge metadata from the update
        if (streamingMessageUpdate.Metadata != null)
        {
            if (Metadata == null)
            {
                Metadata = streamingMessageUpdate.Metadata.DeepClone() as JsonObject;
            }
            else
            {
                // Merge metadata, with update's metadata taking precedence
                foreach (var prop in streamingMessageUpdate.Metadata)
                {
                    Metadata[prop.Key] = prop.Value?.DeepClone();
                }
            }
        }
    }
    
    private void TryCompletePartialUpdate()
    {
        if (_currentFunctionName != null)
        {
            var toolCall = new ToolCall
            {
                FunctionName = _currentFunctionName,
                FunctionArgs = _accumulatedArgs
            };
            _completedToolCalls = _completedToolCalls.Add(toolCall);
            _currentFunctionName = null;
            _accumulatedArgs = "";
        }
    }
    
    public ToolsCallMessage Build()
    {
        // Complete any final partial update
        TryCompletePartialUpdate();
        
        return new ToolsCallMessage
        {
            FromAgent = FromAgent,
            Role = Role,
            Metadata = Metadata,
            GenerationId = GenerationId,
            ToolCalls = _completedToolCalls
        };
    }
}