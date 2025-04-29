using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Json.Schema.Generation;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public record ChatMessage
{
    public ChatMessage() { }

    [JsonPropertyName("role")]
    public RoleEnum? Role { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<FunctionContent>? ToolCalls { get; set; }

    [JsonPropertyName("content")]
    public Union<string, Union<TextContent, ImageContent>[]>? Content { get; set; }

    private IEnumerable<IMessage> ToMessages(
        string? name,
        RoleEnum? role = null,
        bool isStreaming = false)
    {
        role = Role ?? role ?? RoleEnum.Assistant;
        if (ToolCalls?.Count > 0)
        {
            if (isStreaming)
            {
                var toolCallUpdates = ToolCalls.Select(tc =>
                    new ToolCallUpdate
                    {
                        FunctionName = tc.Function.Name,
                        FunctionArgs = tc.Function.Arguments,
                        ToolCallId = tc.Id,
                        Index = tc.Index,
                    }
                ).ToArray();

                yield return new ToolsCallUpdateMessage
                {
                    Role = ToRole(role!.Value),
                    ToolCallUpdates = toolCallUpdates.ToImmutableList(),
                    FromAgent = name,
                    GenerationId = Id,
                };

                yield break;
            }
            else
            {
                var toolCalls = ToolCalls.Select(tc =>
                    new ToolCall(tc.Function.Name, tc.Function.Arguments) { ToolCallId = tc.Id }
                ).ToArray();

                yield return new ToolsCallMessage
                {
                    Role = ToRole(role!.Value),
                    ToolCalls = toolCalls.ToImmutableList(),
                    FromAgent = name,
                    GenerationId = Id
                };
            }
        }

        if (Content == null)
        {
            throw new InvalidOperationException("Content is null");
        }

        if (Content.Is<string>())
        {
            if (Content.Get<string>() == null || role == null)
            {
                throw new InvalidOperationException("Content is null");
            }

            yield return new TextMessage
            {
                Role = ToRole(role!.Value),
                Text = Content.Get<string>()!,
                FromAgent = name,
                GenerationId = Id
            };
        }
        else if (Content.Is<Union<TextContent, ImageContent>[]>())
        {
            var content = Content.Get<Union<TextContent, ImageContent>[]>()!;
            foreach (var item in content)
            {
                yield return item.Is<TextContent>()
                    ? new TextMessage
                    {
                        Role = ToRole(role!.Value),
                        Text = item.Get<TextContent>().Text,
                        FromAgent = name,
                        GenerationId = Id
                    } as IMessage
                    : new ImageMessage
                    {
                        Role = ToRole(role!.Value),
                        ImageData = BinaryData.FromString(item.Get<ImageContent>().Url.Url),
                        FromAgent = name,
                        GenerationId = Id
                    };
            }
        }
        else if (!isStreaming) // Only throw for non-streaming path
        {
            throw new InvalidOperationException("Invalid content type");
        }
    }

    public IEnumerable<IMessage> ToStreamingMessages(
        string? name,
        RoleEnum? role = null)
    {
        return ToMessages(name, role, isStreaming: true);
    }

    // Keeping original non-streaming method as a wrapper for backward compatibility
    public IEnumerable<IMessage> ToMessages(string? name, RoleEnum? role = null)
    {
        return ToMessages(name, role, isStreaming: false);
    }

    public static Role ToRole(RoleEnum role)
    {
        return role switch
        {
            RoleEnum.System => LmCore.Messages.Role.System,
            RoleEnum.User => LmCore.Messages.Role.User,
            RoleEnum.Assistant => LmCore.Messages.Role.Assistant,
            RoleEnum.Tool => LmCore.Messages.Role.Tool,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
        };
    }

    public static RoleEnum ToRoleEnum(Role role)
    {
        return role == LmCore.Messages.Role.System
            ? RoleEnum.System
            : role == LmCore.Messages.Role.User
            ? RoleEnum.User
            : role == LmCore.Messages.Role.Assistant
            ? RoleEnum.Assistant
            : role == LmCore.Messages.Role.Tool
            ? RoleEnum.Tool
            : throw new ArgumentOutOfRangeException(nameof(role), role, null);
    }

    public static Union<string, Union<TextContent, ImageContent>[]> CreateContent(string text)
    {
        return new Union<string, Union<TextContent, ImageContent>[]>(text);
    }
}

[DebuggerDisplay("Text = {Text}")]
public record TextContent
{
    public TextContent(string text)
    {
        Text = text;
    }

    [JsonPropertyName("type")]
    public string Type { get; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; }
}

[DebuggerDisplay("{ImageUrl.DebuggerDisplay,nq}")]
public record ImageContent
{
    public ImageContent(
        string url,
        string? altText = null)
    {
        Url = new ImageUrl(url, altText);
    }

    [JsonPropertyName("type")]
    public string Type { get; } = "image";

    [JsonPropertyName("image_url")]
    public ImageUrl Url { get; }

    [DebuggerDisplay("{DebuggerDisplay,nq}")]
    public record ImageUrl(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("detail")] string? AltText
    )
    {
        private string DebuggerDisplay
        {
            get
            {
                var formattedUrl = Url.Length <= 50
                    ? Url : $"{Url.Substring(0, 23)}...{Url.Substring(Url.Length - 24)}";

                return AltText != null
                    ? $"Url = {formattedUrl}, AltText = {AltText}"
                    : $"Url = {formattedUrl}";
            }
        }
    };
}

public record FunctionContent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("function")] FunctionCall Function)
{
    [JsonPropertyName("index")]
    public int? Index { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; } = "function";
}

public record FunctionCall(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("arguments")] string Arguments) { }

[JsonConverter(typeof(JsonPropertyNameEnumConverter<RoleEnum>))]
public enum RoleEnum
{
    [JsonPropertyName("system")]
    System = 1,

    [JsonPropertyName("user")]
    User = 2,

    [JsonPropertyName("assistant")]
    Assistant = 3,

    [JsonPropertyName("tool")]
    Tool = 4,
}