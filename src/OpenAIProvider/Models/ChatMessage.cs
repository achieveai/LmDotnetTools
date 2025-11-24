using System.Diagnostics;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

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

    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }

    // Some providers (e.g., OpenAI o-series) return a duplicate field "reasoning_content"
    // that mirrors "reasoning". We need to *read* it during deserialization but must **not**
    // emit it when serializing outbound requests (it would duplicate `reasoning`).
    [JsonPropertyName("reasoning_content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ReasoningContent
    {
        // Write-only for deserialization; getter intentionally returns null so the
        // serializer skips the property.
        get => null;
        set => Reasoning = value;
    }

    // Some providers (Claude, OpenAI o-series) return encrypted or structured reasoning inside an array.
    // We model the minimal shape we need: a `type` discriminator and `data` payload.
    [JsonPropertyName("reasoning_details")]
    public List<ReasoningDetail>? ReasoningDetails { get; set; }

    public record ReasoningDetail
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }

        // Some providers (OpenAI o-series) put summary text under a "summary" field instead of "data".
        [JsonPropertyName("summary")]
        public string? Summary { get; set; }
    }

    private IEnumerable<IMessage> ToMessages(string? name, RoleEnum? role = null, bool isStreaming = false)
    {
        role = Role ?? role ?? RoleEnum.Assistant;
        if (ToolCalls?.Count > 0)
        {
            if (isStreaming)
            {
                var toolCallUpdates = ToolCalls
                    .Select(tc => new ToolCallUpdate
                    {
                        FunctionName = tc.Function.Name,
                        FunctionArgs = tc.Function.Arguments,
                        ToolCallId = tc.Id,
                        Index = tc.Index,
                    })
                    .ToArray();

                yield return new ToolsCallUpdateMessage
                {
                    Role = ToRole(role!.Value),
                    ToolCallUpdates = [.. toolCallUpdates],
                    FromAgent = name,
                    GenerationId = Id,
                };

                yield break;
            }
            else
            {
                var toolCalls = ToolCalls
                    .Select((tc, idx) => new ToolCall
                    {
                        FunctionName = tc.Function.Name,
                        FunctionArgs = tc.Function.Arguments,
                        ToolCallId = tc.Id,
                        ToolCallIdx = idx // Assign sequential tool call index
                    })
                    .ToArray();

                yield return new ToolsCallMessage
                {
                    Role = ToRole(role!.Value),
                    ToolCalls = [.. toolCalls],
                    FromAgent = name,
                    GenerationId = Id,
                };
            }
        }

        // Reasoning handling – emit all reasoning blocks, but prioritize reasoning_details visibility when text matches.
        var processedReasoningTexts = new HashSet<string>(StringComparer.Ordinal);

        // First, process reasoning_details to capture proper visibility
        if (ReasoningDetails?.Count > 0)
        {
            foreach (
                var detail in ReasoningDetails.Where(d =>
                    d.Type != null && d.Type.StartsWith("reasoning", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                var detailText = detail.Data ?? detail.Summary;
                if (string.IsNullOrEmpty(detailText))
                {
                    continue;
                }

                var visibility =
                    detail.Type!.EndsWith("encrypted", StringComparison.OrdinalIgnoreCase)
                        ? ReasoningVisibility.Encrypted
                    : detail.Type.EndsWith("summary", StringComparison.OrdinalIgnoreCase) ? ReasoningVisibility.Summary
                    : ReasoningVisibility.Plain;

                yield return isStreaming && visibility != ReasoningVisibility.Encrypted
                    ? new ReasoningUpdateMessage
                    {
                        Role = ToRole(role!.Value),
                        Reasoning = detailText!,
                        FromAgent = name,
                        GenerationId = Id,
                        Visibility = visibility,
                    }
                    : new ReasoningMessage
                    {
                        Role = ToRole(role!.Value),
                        Reasoning = detailText!,
                        FromAgent = name,
                        GenerationId = Id,
                        Visibility = visibility,
                    };

                _ = processedReasoningTexts.Add(detailText);
            }
        }

        // Then, emit top-level reasoning only if it wasn't already processed with proper visibility
        if (!string.IsNullOrEmpty(Reasoning) && !processedReasoningTexts.Contains(Reasoning))
        {
            yield return new ReasoningUpdateMessage
            {
                Role = ToRole(role!.Value),
                Reasoning = Reasoning!,
                FromAgent = name,
                GenerationId = Id,
                Visibility = ReasoningVisibility.Plain,
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

            var contentText = Content.Get<string>()!;

            // For streaming messages, skip empty content to reduce noise
            if (isStreaming && string.IsNullOrEmpty(contentText))
            {
                yield break; // Don't emit anything for empty streaming content
            }

            yield return new TextMessage
            {
                Role = ToRole(role!.Value),
                Text = contentText,
                FromAgent = name,
                GenerationId = Id,
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
                        GenerationId = Id,
                    }
                    : new ImageMessage
                    {
                        Role = ToRole(role!.Value),
                        ImageData = BinaryData.FromString(item.Get<ImageContent>().Url.Url),
                        FromAgent = name,
                        GenerationId = Id,
                    };
            }
        }
        else if (!isStreaming) // Only throw for non-streaming path
        {
            throw new InvalidOperationException("Invalid content type");
        }
    }

    public IEnumerable<IMessage> ToStreamingMessages(string? name, RoleEnum? role = null)
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
        return role == LmCore.Messages.Role.System ? RoleEnum.System
            : role == LmCore.Messages.Role.User ? RoleEnum.User
            : role == LmCore.Messages.Role.Assistant ? RoleEnum.Assistant
            : role == LmCore.Messages.Role.Tool ? RoleEnum.Tool
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
    public ImageContent(string url, string? altText = null)
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
                var formattedUrl =
                    Url.Length <= 50 ? Url : $"{Url[..23]}...{Url[^24..]}";

                return AltText != null ? $"Url = {formattedUrl}, AltText = {AltText}" : $"Url = {formattedUrl}";
            }
        }
    };
}

public record FunctionContent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("function")] FunctionCall Function
)
{
    [JsonPropertyName("index")]
    public int? Index { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; } = "function";
}

public record FunctionCall(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("arguments")] string? Arguments
)
{ }

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
