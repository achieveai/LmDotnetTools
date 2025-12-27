using System.Text.Json;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using LmModels = AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Parsers;

/// <summary>
///     Parser for JSONL stream events from claude-agent-sdk CLI
///     Converts JSONL events to IMessage types for the LmDotnetTools framework
/// </summary>
public class JsonlStreamParser
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger? _logger;

    public JsonlStreamParser(ILogger? logger = null)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };
    }

    /// <summary>
    ///     Parse a single JSONL line into a JsonlEventBase
    /// </summary>
    public JsonlEventBase? ParseLine(string jsonLine)
    {
        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            return null;
        }

        try
        {
            var eventBase = JsonSerializer.Deserialize<JsonlEventBase>(jsonLine, _jsonOptions);
            return eventBase;
        }
        catch (JsonException ex)
        {
            _logger?.LogWarning(ex, "Failed to parse JSONL line: {Line}", jsonLine);
            return null;
        }
    }

    /// <summary>
    ///     Convert an AssistantMessageEvent to IMessage instances
    ///     Returns multiple messages: content messages + usage message
    /// </summary>
    public IEnumerable<IMessage> ConvertToMessages(AssistantMessageEvent assistantEvent)
    {
        ArgumentNullException.ThrowIfNull(assistantEvent);

        _logger?.LogTrace(
            "ConvertToMessages(AssistantMessageEvent) entry: Uuid={Uuid}, ContentBlockCount={ContentBlockCount}",
            assistantEvent.Uuid,
            assistantEvent.Message.Content.Count);

        var messages = new List<IMessage>();

        // Map event properties to message properties
        var runId = assistantEvent.Uuid;
        var parentRunId = assistantEvent.ParentToolUseId;
        var threadId = assistantEvent.SessionId ?? string.Empty; // Provide default if session_id is missing
        var generationId = assistantEvent.Message.Id;
        var role = ParseRole(assistantEvent.Message.Role);

        // Process each content block
        foreach (var contentBlock in assistantEvent.Message.Content)
        {
            _logger?.LogTrace(
                "Processing content block: Type={Type}, HasId={HasId}, HasName={HasName}",
                contentBlock.Type,
                contentBlock.Id != null,
                contentBlock.Name != null);

            var message = ConvertContentBlock(contentBlock, role, generationId, runId, parentRunId, threadId);
            if (message != null)
            {
                messages.Add(message);
                _logger?.LogDebug(
                    "Converted content block to message: Type={MessageType}, Role={Role}",
                    message.GetType().Name,
                    role);
            }
        }

        // Add usage message if available
        if (assistantEvent.Message.Usage != null)
        {
            var usageMessage = ConvertUsage(
                assistantEvent.Message.Usage,
                role,
                generationId,
                runId,
                parentRunId,
                threadId
            );
            messages.Add(usageMessage);
            _logger?.LogDebug(
                "Added usage message: InputTokens={InputTokens}, OutputTokens={OutputTokens}",
                assistantEvent.Message.Usage.InputTokens,
                assistantEvent.Message.Usage.OutputTokens);
        }

        _logger?.LogTrace(
            "ConvertToMessages(AssistantMessageEvent) exit: MessageCount={MessageCount}",
            messages.Count);

        return messages;
    }

    /// <summary>
    ///     Convert a UserMessageEvent to IMessage instances
    ///     Returns messages for tool results or other user content
    /// </summary>
    public IEnumerable<IMessage> ConvertToMessages(UserMessageEvent userEvent)
    {
        ArgumentNullException.ThrowIfNull(userEvent);

        _logger?.LogTrace(
            "ConvertToMessages(UserMessageEvent) entry: Uuid={Uuid}, ContentValueKind={ContentValueKind}",
            userEvent.Uuid,
            userEvent.Message.Content.ValueKind);

        var messages = new List<IMessage>();

        // Map event properties to message properties
        var runId = userEvent.Uuid;
        var threadId = userEvent.SessionId ?? string.Empty;
        var role = ParseRole(userEvent.Message.Role);

        // For user events, we don't have a message.id like assistant events
        // Use the UUID as the generation ID
        var generationId = userEvent.Uuid;

        // Parse content - it could be a string or array of content blocks
        if (userEvent.Message.Content.ValueKind == JsonValueKind.Array)
        {
            var contentBlocks = JsonSerializer.Deserialize<ContentBlock[]>(
                userEvent.Message.Content.GetRawText(),
                _jsonOptions
            );

            _logger?.LogDebug(
                "UserMessageEvent has array content with {BlockCount} blocks",
                contentBlocks?.Length ?? 0);

            if (contentBlocks != null)
            {
                foreach (var contentBlock in contentBlocks)
                {
                    var message = ConvertContentBlock(contentBlock, role, generationId, runId, null, threadId);
                    if (message != null)
                    {
                        messages.Add(message);
                    }
                }
            }
        }
        else if (userEvent.Message.Content.ValueKind == JsonValueKind.String)
        {
            // Simple text content
            var text = userEvent.Message.Content.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                _logger?.LogDebug("UserMessageEvent has simple text content, length={Length}", text.Length);
                messages.Add(
                    new TextMessage
                    {
                        Text = text,
                        Role = role,
                        GenerationId = generationId,
                        RunId = runId,
                        ThreadId = threadId,
                        IsThinking = false,
                    }
                );
            }
        }

        _logger?.LogTrace(
            "ConvertToMessages(UserMessageEvent) exit: MessageCount={MessageCount}",
            messages.Count);

        return messages;
    }

    /// <summary>
    ///     Convert a single content block to an IMessage
    /// </summary>
    private IMessage? ConvertContentBlock(
        ContentBlock contentBlock,
        Role role,
        string generationId,
        string runId,
        string? parentRunId,
        string threadId
    )
    {
        _logger?.LogTrace(
            "ConvertContentBlock: Type={Type}, ToolUseId={ToolUseId}",
            contentBlock.Type,
            contentBlock.ToolUseId);

        return contentBlock.Type switch
        {
            "text" when contentBlock.Text != null => new TextMessage
            {
                Text = contentBlock.Text,
                Role = role,
                GenerationId = generationId,
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
                IsThinking = false,
            },

            "thinking" when contentBlock.Thinking != null => new ReasoningMessage
            {
                Reasoning = contentBlock.Thinking,
                Role = role,
                GenerationId = generationId,
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
                Visibility = ReasoningVisibility.Plain,
            },

            "tool_use" when contentBlock.Id != null && contentBlock.Name != null => new ToolCallMessage
            {
                FunctionName = contentBlock.Name,
                FunctionArgs = contentBlock.Input?.GetRawText() ?? "{}",
                ToolCallId = contentBlock.Id,
                Role = role,
                GenerationId = generationId,
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
            },

            "tool_result" when contentBlock.ToolUseId != null => ConvertToolResultContentBlock(
                contentBlock,
                generationId,
                runId,
                threadId
            ),

            "image" when contentBlock.Source != null => ConvertImageContentBlock(
                contentBlock.Source,
                role,
                generationId,
                runId,
                parentRunId,
                threadId
            ),

            _ => null,
        };
    }

    /// <summary>
    ///     Convert an image source block to an ImageMessage
    /// </summary>
    private ImageMessage? ConvertImageContentBlock(
        ImageSourceBlock source,
        Role role,
        string generationId,
        string runId,
        string? parentRunId,
        string threadId
    )
    {
        _logger?.LogTrace(
            "ConvertImageContentBlock: SourceType={SourceType}, HasData={HasData}, HasUrl={HasUrl}",
            source.Type,
            !string.IsNullOrEmpty(source.Data),
            !string.IsNullOrEmpty(source.Url));

        // Handle base64 encoded images
        if (source.Type == "base64" && !string.IsNullOrEmpty(source.Data))
        {
            try
            {
                var imageBytes = Convert.FromBase64String(source.Data);
                var declaredMediaType = source.MediaType ?? "application/octet-stream";

                // Detect actual MIME type from bytes
                var detectedMediaType = DetectImageMimeType(imageBytes, declaredMediaType);
                if (detectedMediaType != declaredMediaType)
                {
                    _logger?.LogInformation(
                        "Image MIME type corrected: Declared={DeclaredType}, Detected={DetectedType}, ByteLength={ByteLength}",
                        declaredMediaType,
                        detectedMediaType,
                        imageBytes.Length);
                }
                else
                {
                    _logger?.LogDebug(
                        "Image block converted: MimeType={MimeType}, ByteLength={ByteLength}",
                        detectedMediaType,
                        imageBytes.Length);
                }

                var binaryData = BinaryData.FromBytes(imageBytes, detectedMediaType);

                return new ImageMessage
                {
                    ImageData = binaryData,
                    Role = role,
                    GenerationId = generationId,
                    RunId = runId,
                    ParentRunId = parentRunId,
                    ThreadId = threadId,
                };
            }
            catch (FormatException ex)
            {
                _logger?.LogWarning(ex, "Invalid base64 data in image content block");
                return null;
            }
        }

        // Handle URL-based images - store URL as data URI placeholder
        if (source.Type == "url" && !string.IsNullOrEmpty(source.Url))
        {
            // For URL sources, we create a BinaryData with the URL as content
            // The consumer can then fetch the image if needed
            var mediaType = source.MediaType ?? "text/uri-list";
            var binaryData = BinaryData.FromString(source.Url, mediaType);

            _logger?.LogDebug("URL-based image block: Url={Url}, MediaType={MediaType}", source.Url, mediaType);

            return new ImageMessage
            {
                ImageData = binaryData,
                Role = role,
                GenerationId = generationId,
                RunId = runId,
                ParentRunId = parentRunId,
                ThreadId = threadId,
            };
        }

        _logger?.LogWarning(
            "Unsupported image source type: Type={Type}",
            source.Type);
        return null;
    }

    /// <summary>
    ///     Convert a tool_result content block to a ToolCallResultMessage.
    ///     Handles both simple string content and multimodal content arrays (text + images).
    /// </summary>
    private ToolCallResultMessage ConvertToolResultContentBlock(
        ContentBlock contentBlock,
        string generationId,
        string runId,
        string threadId
    )
    {
        _logger?.LogTrace(
            "ConvertToolResultContentBlock entry: ToolUseId={ToolUseId}, HasContent={HasContent}",
            contentBlock.ToolUseId,
            contentBlock.Content.HasValue);

        var textParts = new List<string>();
        var contentBlocks = new List<ToolResultContentBlock>();
        var imageCount = 0;
        var textBlockCount = 0;

        // Check if content is an array (multimodal) or simple value
        if (contentBlock.Content.HasValue)
        {
            var content = contentBlock.Content.Value;

            _logger?.LogDebug(
                "tool_result content: ValueKind={ValueKind}, ToolUseId={ToolUseId}",
                content.ValueKind,
                contentBlock.ToolUseId);

            if (content.ValueKind == JsonValueKind.Array)
            {
                var arrayLength = content.GetArrayLength();
                _logger?.LogDebug(
                    "Multimodal tool_result detected: ArrayLength={ArrayLength}, ToolUseId={ToolUseId}",
                    arrayLength,
                    contentBlock.ToolUseId);

                // Multimodal content - parse each element
                foreach (var element in content.EnumerateArray())
                {
                    var type = element.TryGetProperty("type", out var typeElement)
                        ? typeElement.GetString()
                        : null;

                    _logger?.LogTrace("Processing tool_result element: Type={Type}", type);

                    switch (type)
                    {
                        case "text":
                            if (element.TryGetProperty("text", out var textElement))
                            {
                                var text = textElement.GetString() ?? "";
                                textParts.Add(text);
                                contentBlocks.Add(new TextToolResultBlock { Text = text });
                                textBlockCount++;
                                _logger?.LogTrace("Added text block: Length={Length}", text.Length);
                            }
                            break;

                        case "image":
                            // Anthropic format: { type: "image", source: { type: "base64", media_type: "...", data: "..." } }
                            if (element.TryGetProperty("source", out var sourceElement))
                            {
                                var sourceType = sourceElement.TryGetProperty("type", out var st)
                                    ? st.GetString()
                                    : null;

                                _logger?.LogTrace("Image source type: {SourceType}", sourceType);

                                if (sourceType == "base64" &&
                                    sourceElement.TryGetProperty("data", out var dataElement) &&
                                    sourceElement.TryGetProperty("media_type", out var mediaTypeElement))
                                {
                                    var data = dataElement.GetString();
                                    var mediaType = mediaTypeElement.GetString();

                                    if (!string.IsNullOrEmpty(data) && !string.IsNullOrEmpty(mediaType))
                                    {
                                        // Detect MIME type from actual bytes for safety
                                        try
                                        {
                                            var bytes = Convert.FromBase64String(data);
                                            var detectedMimeType = DetectImageMimeType(bytes, mediaType);

                                            if (detectedMimeType != mediaType)
                                            {
                                                _logger?.LogInformation(
                                                    "MIME type corrected from header: Header={HeaderMimeType}, Detected={DetectedMimeType}, ByteLength={ByteLength}",
                                                    mediaType,
                                                    detectedMimeType,
                                                    bytes.Length);
                                            }
                                            else
                                            {
                                                _logger?.LogDebug(
                                                    "Image parsed successfully: MimeType={MimeType}, ByteLength={ByteLength}",
                                                    detectedMimeType,
                                                    bytes.Length);
                                            }

                                            contentBlocks.Add(new ImageToolResultBlock
                                            {
                                                Data = data,
                                                MimeType = detectedMimeType
                                            });
                                            imageCount++;
                                        }
                                        catch (FormatException ex)
                                        {
                                            _logger?.LogWarning(
                                                ex,
                                                "Invalid base64 data in tool_result image: ToolUseId={ToolUseId}, DataLength={DataLength}",
                                                contentBlock.ToolUseId,
                                                data?.Length ?? 0);
                                        }
                                    }
                                    else
                                    {
                                        _logger?.LogWarning(
                                            "Image block missing data or media_type: ToolUseId={ToolUseId}, HasData={HasData}, HasMediaType={HasMediaType}",
                                            contentBlock.ToolUseId,
                                            !string.IsNullOrEmpty(data),
                                            !string.IsNullOrEmpty(mediaType));
                                    }
                                }
                                else
                                {
                                    _logger?.LogDebug(
                                        "Image source not base64 or missing properties: SourceType={SourceType}",
                                        sourceType);
                                }
                            }
                            else
                            {
                                _logger?.LogWarning(
                                    "Image block missing 'source' property: ToolUseId={ToolUseId}",
                                    contentBlock.ToolUseId);
                            }
                            break;

                        default:
                            _logger?.LogTrace("Skipping unknown content type in tool_result: Type={Type}", type);
                            break;
                    }
                }
            }
            else if (content.ValueKind == JsonValueKind.String)
            {
                // Simple string content
                var stringContent = content.GetString() ?? "";
                textParts.Add(stringContent);
                _logger?.LogDebug(
                    "tool_result has simple string content: Length={Length}, ToolUseId={ToolUseId}",
                    stringContent.Length,
                    contentBlock.ToolUseId);
            }
            else
            {
                // Other JSON value - use raw text
                var rawText = content.GetRawText();
                textParts.Add(rawText);
                _logger?.LogDebug(
                    "tool_result has non-string/non-array content: ValueKind={ValueKind}, RawLength={RawLength}",
                    content.ValueKind,
                    rawText.Length);
            }
        }
        else
        {
            _logger?.LogDebug("tool_result has no content: ToolUseId={ToolUseId}", contentBlock.ToolUseId);
        }

        // Build the Result string (for backward compatibility)
        var result = textParts.Count > 0
            ? string.Join(Environment.NewLine, textParts)
            : contentBlock.Content?.GetRawText() ?? "";

        // Only include ContentBlocks if we have image content
        var hasImages = contentBlocks.OfType<ImageToolResultBlock>().Any();

        _logger?.LogInformation(
            "ConvertToolResultContentBlock complete: ToolUseId={ToolUseId}, TextBlocks={TextBlocks}, ImageBlocks={ImageBlocks}, HasContentBlocks={HasContentBlocks}, ResultLength={ResultLength}",
            contentBlock.ToolUseId,
            textBlockCount,
            imageCount,
            hasImages,
            result.Length);

        return new ToolCallResultMessage
        {
            ToolCallId = contentBlock.ToolUseId,
            Result = result,
            ContentBlocks = hasImages ? contentBlocks : null,
            Role = Role.User, // Tool results are from user/system
            GenerationId = generationId,
            RunId = runId,
            ThreadId = threadId,
        };
    }

    /// <summary>
    ///     Detects the MIME type of an image from its byte content.
    ///     Uses magic bytes to identify common image formats.
    /// </summary>
    private static string DetectImageMimeType(byte[] bytes, string fallbackMimeType)
    {
        if (bytes.Length >= 8)
        {
            // PNG: 89 50 4E 47
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            {
                return "image/png";
            }

            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }

            // GIF: 47 49 46 38
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            {
                return "image/gif";
            }

            // WebP: 52 49 46 46 ... 57 45 42 50
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes.Length >= 12 && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            {
                return "image/webp";
            }
        }

        return fallbackMimeType ?? "application/octet-stream";
    }

    /// <summary>
    ///     Convert usage information to UsageMessage
    /// </summary>
    private UsageMessage ConvertUsage(
        UsageInfo usageInfo,
        Role role,
        string generationId,
        string runId,
        string? parentRunId,
        string threadId
    )
    {
        _logger?.LogTrace(
            "ConvertUsage: InputTokens={InputTokens}, OutputTokens={OutputTokens}",
            usageInfo.InputTokens,
            usageInfo.OutputTokens);

        var usage = new LmModels.Usage
        {
            PromptTokens = usageInfo.InputTokens,
            CompletionTokens = usageInfo.OutputTokens,
            TotalTokens = usageInfo.InputTokens + usageInfo.OutputTokens,
            InputTokenDetails =
                usageInfo.CacheReadInputTokens > 0 || usageInfo.CacheCreationInputTokens > 0
                    ? new LmModels.InputTokenDetails { CachedTokens = usageInfo.CacheReadInputTokens ?? 0 }
                    : null,
        };

        // Store additional cache info in ExtraProperties
        if (usageInfo.CacheCreationInputTokens > 0)
        {
            usage = usage.SetExtraProperty("cache_creation_input_tokens", usageInfo.CacheCreationInputTokens);
            _logger?.LogDebug("Cache creation tokens: {CacheCreationTokens}", usageInfo.CacheCreationInputTokens);
        }

        if (usageInfo.CacheCreation?.Ephemeral5mInputTokens > 0)
        {
            usage = usage.SetExtraProperty("ephemeral_5m_input_tokens", usageInfo.CacheCreation.Ephemeral5mInputTokens);
        }

        if (usageInfo.CacheCreation?.Ephemeral1hInputTokens > 0)
        {
            usage = usage.SetExtraProperty("ephemeral_1h_input_tokens", usageInfo.CacheCreation.Ephemeral1hInputTokens);
        }

        if (!string.IsNullOrEmpty(usageInfo.ServiceTier))
        {
            usage = usage.SetExtraProperty("service_tier", usageInfo.ServiceTier);
            _logger?.LogDebug("Service tier: {ServiceTier}", usageInfo.ServiceTier);
        }

        return new UsageMessage
        {
            Usage = usage,
            Role = role,
            GenerationId = generationId,
            RunId = runId,
            ThreadId = threadId,
        };
    }

    /// <summary>
    ///     Parse role string to Role enum
    /// </summary>
    private static Role ParseRole(string roleString)
    {
        return roleString?.ToLowerInvariant() switch
        {
            "user" => Role.User,
            "assistant" => Role.Assistant,
            "system" => Role.System,
            "tool" => Role.Tool,
            _ => Role.None,
        };
    }
}
