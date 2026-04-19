using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
/// Represents the current todo/task context that should be forwarded to the next tool call.
/// This helps maintain awareness of active tasks across tool interactions.
/// </summary>
[JsonConverter(typeof(TodoContextMessageJsonConverter))]
public record TodoContextMessage : IMessage, ICanGetText
{
  /// <summary>
  /// The current task context information.
  /// </summary>
  [JsonPropertyName("todoContext")]
  public required string TodoContext { get; init; }

  public string? GetText() => TodoContext;

  [JsonPropertyName("fromAgent")]
  public string? FromAgent { get; init; }

  [JsonPropertyName("role")]
  public Role Role { get; init; } = Role.System;

  [JsonIgnore]
  public ImmutableDictionary<string, object>? Metadata { get; init; }

  [JsonPropertyName("generationId")]
  public string? GenerationId { get; init; }

  public BinaryData? GetBinary() => null;
  public ToolCall? GetToolCalls() => null;
  public IEnumerable<IMessage>? GetMessages() => null;
}

public class TodoContextMessageJsonConverter : ShadowPropertiesJsonConverter<TodoContextMessage>
{
  protected override TodoContextMessage CreateInstance()
  {
    return new TodoContextMessage { TodoContext = string.Empty };
  }
}
