using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Models;

public class ChatMessage
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

    public IMessage ToStreamingMessage(
        string? name,
        RoleEnum? role = null)
    {
        role = Role ?? role ?? RoleEnum.Assistant;
        if (ToolCalls?.Count > 0)
        {
            var toolCalls = ToolCalls.Select(tc =>
                new ToolCallUpdate{
                    FunctionName = tc.Function.Name,
                    FunctionArgs = tc.Function.Arguments,
                    ToolCallId = tc.Id,
                    Index = tc.Index,
                }
            ).ToArray();

            return new ToolsCallUpdateMessage
            {
                Role = ToRole(role!.Value),
                ToolCallUpdates = toolCalls.ToImmutableList(),
                FromAgent = name,
                GenerationId = Id,
            };
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

            return new TextMessage
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
            if (content.Length == 1)
            {
                var item = content[0];
                return item.Is<TextContent>()
                    ? new TextMessage
                    {
                        Role = ToRole(role!.Value),
                        Text = item.Get<TextContent>().Text,
                        FromAgent = name,
                        GenerationId = Id
                    }
                    : new ImageMessage
                    {
                        Role = ToRole(role!.Value),
                        ImageData = BinaryData.FromString(item.Get<ImageContent>().Url.Url),
                        FromAgent = name,
                        GenerationId = Id
                    };
            }

            var messages = content.Select(item =>
                item.Is<TextContent>()
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
                    }
            ).ToArray();

            return new CompositeMessage
            {
                Role = ToRole(role!.Value),
                Contents = messages.Select(m => 
                    m is TextMessage tm ? new Union<string, BinaryData, ToolCallResult>(tm.Text) :
                    m is ImageMessage im ? new Union<string, BinaryData, ToolCallResult>(im.ImageData) :
                    throw new InvalidOperationException("Unexpected message type")
                ).ToImmutableList(),
                FromAgent = name,
                GenerationId = Id
            };
        }

        return ToMessage(name, role);
    }

    public IMessage ToMessage(string? name, RoleEnum? role = null)
    {
        role = Role ?? role ?? RoleEnum.Assistant;
        if (ToolCalls?.Count > 0)
        {
            var toolCalls = ToolCalls.Select(tc =>
                new ToolCall(tc.Function.Name, tc.Function.Arguments) { ToolCallId = tc.Id }
            ).ToArray();

            return new ToolsCallMessage
            {
                Role = ToRole(role!.Value),
                ToolCalls = toolCalls.ToImmutableList(),
                FromAgent = name,
                GenerationId = Id
            };
        }

        if (Content == null)
        {
            throw new InvalidOperationException("Content is null");
        }

        if (Content.Is<string>())
        {
            return new TextMessage
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
            if (content.Length == 1)
            {
                var item = content[0];
                return item.Is<TextContent>()
                    ? new TextMessage
                    {
                        Role = ToRole(role!.Value),
                        Text = item.Get<TextContent>().Text,
                        FromAgent = name,
                        GenerationId = Id
                    }
                    : new ImageMessage
                    {
                        Role = ToRole(role!.Value),
                        ImageData = BinaryData.FromString(item.Get<ImageContent>().Url.Url),
                        FromAgent = name,
                        GenerationId = Id
                    };
            }

            var messages = content.Select(item =>
                item.Is<TextContent>()
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
                    }
            ).ToArray();

            return new CompositeMessage
            {
                Role = ToRole(role!.Value),
                Contents = messages.Select(m => 
                    m is TextMessage tm ? new Union<string, BinaryData, ToolCallResult>(tm.Text) :
                    m is ImageMessage im ? new Union<string, BinaryData, ToolCallResult>(im.ImageData) :
                    throw new InvalidOperationException("Unexpected message type")
                ).ToImmutableList(),
                FromAgent = name,
                GenerationId = Id
            };
        }

        throw new InvalidOperationException("Invalid content type");
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

public class FunctionContent
{
    public FunctionContent(
        string id,
        FunctionCall function)
    {
        Id = id;
        Function = function;
    }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; } = "function";

    [JsonPropertyName("function")]
    public FunctionCall Function { get; set; }

    public class FunctionCall
    {
        public FunctionCall(string name, string arguments)
        {
            Name = name;
            Arguments = arguments;
        }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("arguments")]
        public string Arguments { get; set; }

        public string ComputeToolCallId()
        {
            return "call_" + ((uint)$"tool_{Name}_{Arguments}".GetHashCode()).ToString();
        }
    }
}

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