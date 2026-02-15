using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

[JsonConverter(typeof(ToolCallMessageJsonConverter))]
public record ToolCallMessage : ToolCall, IMessage
{
    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    [JsonPropertyName("generation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }
}

public class ToolCallMessageJsonConverter : ShadowPropertiesJsonConverter<ToolCallMessage>
{
    protected override ToolCallMessage CreateInstance()
    {
        return new ToolCallMessage();
    }
}

/// <summary>
/// Builder for constructing a ToolCallMessage from streaming ToolCallUpdateMessage updates.
/// Accumulates function arguments and metadata from multiple update chunks.
/// </summary>
public class ToolCallMessageBuilder : IMessageBuilder<ToolCallMessage, ToolCallUpdateMessage>
{
    /// <summary>
    /// The agent that generated this message.
    /// </summary>
    public string? FromAgent { get; init; } = null;

    /// <summary>
    /// The role of this message (typically Assistant for tool calls).
    /// </summary>
    public Role Role { get; init; } = Role.Assistant;

    /// <summary>
    /// Combined metadata from all updates.
    /// </summary>
    public ImmutableDictionary<string, object>? Metadata { get; private set; } = null;

    /// <summary>
    /// The generation ID for this tool call.
    /// </summary>
    public string? GenerationId { get; set; } = null;

    /// <summary>
    /// Callback invoked when the tool call is completed.
    /// </summary>
    public Action<ToolCall> OnToolCall { get; init; } = _ => { };

    /// <summary>
    /// The current function name being accumulated.
    /// </summary>
    public string? CurrentFunctionName { get; private set; } = null;

    /// <summary>
    /// The accumulated function arguments as a JSON string.
    /// </summary>
    public string AccumulatedArgs { get; private set; } = "";

    /// <summary>
    /// The tool call ID for this tool call.
    /// </summary>
    public string? CurrentToolCallId { get; private set; } = null;

    /// <summary>
    /// The index of this tool call.
    /// </summary>
    public int? CurrentIndex { get; private set; } = null;

    /// <summary>
    /// The execution target of this tool call.
    /// </summary>
    public ExecutionTarget CurrentExecutionTarget { get; private set; } = ExecutionTarget.LocalFunction;

    /// <summary>
    /// Thread identifier for conversation continuity.
    /// </summary>
    public string? ThreadId { get; set; }

    /// <summary>
    /// Run identifier for this specific execution.
    /// </summary>
    public string? RunId { get; set; }

    /// <summary>
    /// Parent run identifier for branching/time travel.
    /// </summary>
    public string? ParentRunId { get; set; }

    /// <summary>
    /// Message order index within the generation.
    /// </summary>
    public int? MessageOrderIdx { get; set; }

    /// <summary>
    /// Order index of this tool call within its containing message/stream.
    /// Enables deterministic reconstruction of tool call order for KV cache optimization.
    /// </summary>
    public int ToolCallIdx { get; set; }

    /// <summary>
    /// List to accumulate all JSON fragment updates across all streaming chunks.
    /// </summary>
    public List<JsonFragmentUpdate> AccumulatedJsonFragments { get; private set; } = [];

    /// <summary>
    /// Adds a streaming update to the builder, accumulating function arguments and metadata.
    /// </summary>
    /// <param name="streamingMessageUpdate">The streaming update to add.</param>
    public void Add(ToolCallUpdateMessage streamingMessageUpdate)
    {
        ArgumentNullException.ThrowIfNull(streamingMessageUpdate);

        // Update function name if provided
        if (streamingMessageUpdate.FunctionName != null)
        {
            CurrentFunctionName = streamingMessageUpdate.FunctionName;
        }

        // Accumulate function arguments
        if (streamingMessageUpdate.FunctionArgs != null)
        {
            AccumulatedArgs += streamingMessageUpdate.FunctionArgs;
        }

        // Update tool call ID if provided
        if (streamingMessageUpdate.ToolCallId != null)
        {
            CurrentToolCallId = streamingMessageUpdate.ToolCallId;
        }

        // Update index if provided
        if (streamingMessageUpdate.Index != null)
        {
            CurrentIndex = streamingMessageUpdate.Index;
        }

        // Update execution target
        CurrentExecutionTarget = streamingMessageUpdate.ExecutionTarget;

        // Accumulate JSON fragment updates
        if (streamingMessageUpdate.JsonFragmentUpdates != null)
        {
            AccumulatedJsonFragments.AddRange(streamingMessageUpdate.JsonFragmentUpdates);
        }

        // Merge metadata from the update
        if (streamingMessageUpdate.Metadata != null)
        {
            if (Metadata == null)
            {
                Metadata = streamingMessageUpdate.Metadata;
            }
            else
            {
                var builder = Metadata.ToBuilder();
                foreach (var kvp in streamingMessageUpdate.Metadata)
                {
                    builder[kvp.Key] = kvp.Value;
                }
                Metadata = builder.ToImmutable();
            }
        }

        // Update generation ID if provided
        GenerationId ??= streamingMessageUpdate.GenerationId;

        // Update thread/run IDs if provided
        ThreadId ??= streamingMessageUpdate.ThreadId;
        RunId ??= streamingMessageUpdate.RunId;
        ParentRunId ??= streamingMessageUpdate.ParentRunId;
        MessageOrderIdx ??= streamingMessageUpdate.MessageOrderIdx;
    }

    /// <summary>
    /// Builds the final ToolCallMessage from all accumulated updates.
    /// </summary>
    /// <returns>A complete ToolCallMessage with accumulated data.</returns>
    public ToolCallMessage Build()
    {
        // Create the final tool call
        var toolCall = new ToolCallMessage
        {
            FunctionName = CurrentFunctionName,
            FunctionArgs = AccumulatedArgs,
            ToolCallId = CurrentToolCallId,
            Index = CurrentIndex,
            ToolCallIdx = ToolCallIdx,
            ExecutionTarget = CurrentExecutionTarget,
            Role = Role,
            FromAgent = FromAgent,
            GenerationId = GenerationId,
            Metadata = Metadata,
            ThreadId = ThreadId,
            RunId = RunId,
            ParentRunId = ParentRunId,
            MessageOrderIdx = MessageOrderIdx,
        };

        // Invoke the callback
        OnToolCall?.Invoke(toolCall);

        return toolCall;
    }

    /// <summary>
    /// Builds the message (non-generic interface implementation).
    /// </summary>
    IMessage IMessageBuilder.Build()
    {
        return Build();
    }
}
