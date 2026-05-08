using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

/// <summary>
///     JSONL input message wrapper for claude-agent-sdk CLI stdin
///     Format: {"type":"user","message":{"role":"user","content":[...]}}
/// </summary>
public class InputMessageWrapper
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "user";

    [JsonPropertyName("message")]
    public InputMessage Message { get; set; } = new();
}

/// <summary>
///     Input message following Claude Messages API format
/// </summary>
public class InputMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public List<InputContentBlock> Content { get; set; } = [];
}

/// <summary>
///     Base class for input content blocks in message
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(InputTextContentBlock), "text")]
[JsonDerivedType(typeof(InputImageContentBlock), "image")]
public abstract class InputContentBlock
{
    // Note: The 'type' property is handled by JsonPolymorphic discriminator,
    // so we don't need an explicit property here. The discriminator will
    // automatically write "type":"text" or "type":"image" during serialization.
}

/// <summary>
///     Text content block for input messages
/// </summary>
public class InputTextContentBlock : InputContentBlock
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

/// <summary>
///     Image content block with base64 source for input messages
/// </summary>
public class InputImageContentBlock : InputContentBlock
{
    [JsonPropertyName("source")]
    public ImageSource Source { get; set; } = new();
}

/// <summary>
///     Image source with base64 data
///     Supported media types: image/jpeg, image/png, image/gif, image/webp
/// </summary>
public class ImageSource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "base64";

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "image/jpeg";

    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;
}
