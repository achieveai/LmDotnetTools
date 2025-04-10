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
}

public class ToolsCallMessageBuilder : IMessageBuilder<ToolsCallMessage, ToolsCallUpdateMessage>
{
    public string? FromAgent { get; init; } = null;

    public Role Role { get; init; } = Role.Assistant;
    
    public JsonObject? Metadata { get; private set; } = null;

    public string? GenerationId { get; init; } = null;

    public Action<ToolCall> OnToolCall { get; init; } = _ => { };

    private ImmutableList<ToolCall> _completedToolCalls = ImmutableList<ToolCall>.Empty;
    
    // Current partial tool call we're building
    private string? _currentFunctionName = null;
    private string _accumulatedArgs = "";
    private string? _currentToolCallId = null;
    private int? _currentIndex = null;

    IMessage IMessageBuilder.Build()
    {
        return this.Build();
    }
    
    public void Add(ToolsCallUpdateMessage streamingMessageUpdate)
    {
        // Process each update
        foreach (var update in streamingMessageUpdate.ToolCallUpdates)
        {
            // Check if this update completes a current tool call based on Id or Index
            bool isNewToolCall = false;
            
            // Rule 0: If we have both IDs (non-null) and they're different, it's a new tool call
            if (_currentToolCallId != null && update.ToolCallId != null && _currentToolCallId != update.ToolCallId)
            {
                CompleteCurrentToolCall();
                isNewToolCall = true;
            }
            // Rule 1: If we have both Indexes (non-null) and they're different, it's a new tool call
            else if (_currentIndex != null && update.Index != null && _currentIndex != update.Index)
            {
                CompleteCurrentToolCall();
                isNewToolCall = true;
            }
            
            // If update contains a function name, it's the start of a new tool call
            if (isNewToolCall || update.FunctionName != null)
            {
                // Start a new tool call
                _currentFunctionName = update.FunctionName;
                _accumulatedArgs = update.FunctionArgs ?? "";
                _currentToolCallId = update.ToolCallId;
                _currentIndex = update.Index;
            }
            // Otherwise, it's an update to the current partial tool call
            else if (_currentFunctionName != null && update.FunctionArgs != null)
            {
                _accumulatedArgs += update.FunctionArgs;
                
                // Update tool call ID if it's now provided
                if (_currentToolCallId == null && update.ToolCallId != null)
                {
                    _currentToolCallId = update.ToolCallId;
                }
                
                // Update index if it's now provided
                if (_currentIndex == null && update.Index != null)
                {
                    _currentIndex = update.Index;
                }
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
    
    private void CompleteCurrentToolCall()
    {
        if (_currentFunctionName != null)
        {
            var toolCall = new ToolCall
            {
                FunctionName = _currentFunctionName,
                FunctionArgs = _accumulatedArgs,
                ToolCallId = _currentToolCallId,
                Index = _currentIndex
            };
            
            // Add to completed tool calls
            _completedToolCalls = _completedToolCalls.Add(toolCall);
            
            // Invoke callback for completed tool call
            OnToolCall(toolCall);
            
            // Reset the current tool call state
            _currentFunctionName = null;
            _accumulatedArgs = "";
            _currentToolCallId = null;
            _currentIndex = null;
        }
    }
    
    private void TryCompletePartialUpdate()
    {
        CompleteCurrentToolCall();
    }
    
    public ToolsCallMessage Build()
    {
        // Rule 2: When build is called, complete any final partial update
        CompleteCurrentToolCall();
        
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