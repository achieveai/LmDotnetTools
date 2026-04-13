using System.Collections.Immutable;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Utils;

public class IMessageJsonConverterTests
{
    private static JsonSerializerOptions GetOptionsWithConverter()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };

        options.Converters.Add(new IMessageJsonConverter());
        options.Converters.Add(new TextMessageJsonConverter());
        options.Converters.Add(new ImageMessageJsonConverter());
        options.Converters.Add(new ToolsCallMessageJsonConverter());
        options.Converters.Add(new ToolsCallResultMessageJsonConverter());
        options.Converters.Add(new ToolsCallAggregateMessageJsonConverter());
        options.Converters.Add(new TextUpdateMessageJsonConverter());
        options.Converters.Add(new ToolsCallUpdateMessageJsonConverter());
        // Add singular tool call converters
        options.Converters.Add(new ToolCallMessageJsonConverter());
        options.Converters.Add(new ToolCallUpdateMessageJsonConverter());
        options.Converters.Add(new ToolCallResultMessageJsonConverter());
        return options;
    }

    [Fact]
    public void Serialize_TextMessage_AsIMessage_AddsTypeDiscriminator()
    {
        // Arrange
        IMessage message = new TextMessage
        {
            Text = "Hello world",
            Role = Role.Assistant,
            FromAgent = "test-agent",
            GenerationId = "test-gen-id",
            Metadata = ImmutableDictionary<string, object>.Empty.Add("test", "value"),
        };

        var options = GetOptionsWithConverter();

        // Act
        var json = JsonSerializer.Serialize(message, options);
        TestContextLogger.LogDebug("Serialized JSON: {Json}", json);

        // Assert
        var jsonDocument = JsonDocument.Parse(json);
        var root = jsonDocument.RootElement;

        Assert.True(root.TryGetProperty("$type", out var typeProperty));
        Assert.Equal("text", typeProperty.GetString());
        Assert.Equal("Hello world", root.GetProperty("text").GetString());
    }

    [Fact]
    public void Deserialize_TextMessage_WithTypeDiscriminator_ReturnsCorrectType()
    {
        // Arrange
        var json =
            @"{
            ""$type"": ""text"",
            ""text"": ""Hello world"",
            ""role"": ""assistant"",
            ""fromAgent"": ""test-agent"",
            ""generationId"": ""test-gen-id""
        }";

        var options = GetOptionsWithConverter();

        // Act
        var message = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(message);
        _ = Assert.IsType<TextMessage>(message);

        var textMessage = (TextMessage)message;
        Assert.Equal("Hello world", textMessage.Text);
        Assert.Equal(Role.Assistant, textMessage.Role);
        Assert.Equal("test-agent", textMessage.FromAgent);
        Assert.Equal("test-gen-id", textMessage.GenerationId);
    }

    [Fact]
    public void Deserialize_TextMessage_WithoutTypeDiscriminator_ReturnsCorrectType()
    {
        // Arrange
        var json =
            @"{
            ""text"": ""Hello world"",
            ""role"": ""assistant"",
            ""fromAgent"": ""test-agent"",
            ""generationId"": ""test-gen-id""
        }";

        var options = GetOptionsWithConverter();

        // Act
        var message = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(message);
        _ = Assert.IsType<TextMessage>(message);

        var textMessage = (TextMessage)message;
        Assert.Equal("Hello world", textMessage.Text);
        Assert.Equal(Role.Assistant, textMessage.Role);
        Assert.Equal("test-agent", textMessage.FromAgent);
        Assert.Equal("test-gen-id", textMessage.GenerationId);
    }

    [Fact]
    public void RoundTrip_ToolsCallAggregateMessage_PreservesAllData()
    {
        // Arrange
        var toolCall = new ToolCall
        {
            FunctionName = "test_function",
            FunctionArgs = """{"arg1": "value1"}""",
            ToolCallId = "tool-1",
        };

        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls = [toolCall],
            Role = Role.Assistant,
            FromAgent = "assistant-agent",
            GenerationId = "gen-1",
            Metadata = ImmutableDictionary<string, object>.Empty.Add("source", "tool-call"),
        };

        var toolCallResult = new ToolsCallResultMessage
        {
            ToolCallResults = [new ToolCallResult("tool-1", "function result")],
            Role = Role.User,
            FromAgent = "user-agent",
            Metadata = ImmutableDictionary<string, object>.Empty.Add("source", "tool-result"),
        };

        IMessage originalMessage = new ToolsCallAggregateMessage(toolCallMessage, toolCallResult, "combined-agent");

        var options = GetOptionsWithConverter();

        // Act
        var json = JsonSerializer.Serialize(originalMessage, options);
        TestContextLogger.LogDebug("Serialized JSON: {Json}", json);

        var deserializedMessage = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(deserializedMessage);
        _ = Assert.IsType<ToolsCallAggregateMessage>(deserializedMessage);

        var aggregateMessage = (ToolsCallAggregateMessage)deserializedMessage;
        Assert.Equal("combined-agent", aggregateMessage.FromAgent);
        Assert.Equal(Role.Assistant, aggregateMessage.Role);
        Assert.Equal("gen-1", aggregateMessage.GenerationId);

        // Verify tool call message
        Assert.NotNull(aggregateMessage.ToolsCallMessage);
        var resultingToolCalls = ((ICanGetToolCalls)aggregateMessage.ToolsCallMessage).GetToolCalls();
        Assert.NotNull(resultingToolCalls);

        // Verify tool call result
        Assert.NotNull(aggregateMessage.ToolsCallResult);
        Assert.NotEmpty(aggregateMessage.ToolsCallResult.ToolCallResults);
        Assert.Equal("tool-1", aggregateMessage.ToolsCallResult.ToolCallResults[0].ToolCallId);
        Assert.Equal("function result", aggregateMessage.ToolsCallResult.ToolCallResults[0].Result);
    }

    [Theory]
    [InlineData("text", typeof(TextMessage))]
    [InlineData("image", typeof(ImageMessage))]
    [InlineData("tools_call", typeof(ToolsCallMessage))]
    [InlineData("tools_call_result", typeof(ToolsCallResultMessage))]
    public void Deserialize_WithTypeDiscriminator_ReturnsCorrectType(string typeDiscriminator, Type expectedType)
    {
        // Arrange
        var json =
            $@"{{
            ""$type"": ""{typeDiscriminator}"",
            ""role"": ""assistant""
        }}";

        if (expectedType == typeof(TextMessage))
        {
            json =
                $@"{{
                ""$type"": ""{typeDiscriminator}"",
                ""text"": ""Required text"",
                ""role"": ""assistant""
            }}";
        }
        else if (expectedType == typeof(ImageMessage))
        {
            json =
                $@"{{
                ""$type"": ""{typeDiscriminator}"",
                ""role"": ""assistant"",
                ""image_data"": ""ZmFrZS1pbWFnZS1kYXRh""
            }}";
        }

        // Act - Use our custom converter explicitly
        var options = GetOptionsWithConverter();
        var message = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(message);
        Assert.IsType(expectedType, message);
    }

    [Fact]
    public void RoundTrip_AllMessageTypes_PreservesTypeInformation()
    {
        // Arrange
        var messages = new IMessage[]
        {
            new TextMessage { Text = "Hello", Role = Role.User },
            new ImageMessage { ImageData = BinaryData.FromString("fake-image-data"), Role = Role.Assistant },
            new ToolsCallMessage
            {
                ToolCalls =
                [
                    new ToolCall
                    {
                        FunctionName = "test_function",
                        FunctionArgs = "{}",
                        ToolCallId = "id1",
                    },
                ],
                Role = Role.Assistant,
            },
        };

        var options = GetOptionsWithConverter();

        foreach (var originalMessage in messages)
        {
            // Act
            var json = JsonSerializer.Serialize(originalMessage, options);
            TestContextLogger.LogDebug("Serialized message. MessageType: {MessageType}, Json: {Json}", originalMessage.GetType().Name, json);

            var deserializedMessage = JsonSerializer.Deserialize<IMessage>(json, options);

            // Assert
            Assert.NotNull(deserializedMessage);
            Assert.Equal(originalMessage.GetType(), deserializedMessage.GetType());
        }
    }

    [Fact]
    public void Serialize_DeserializeMessage_WithRespectToTypeSpecificConverter()
    {
        // Arrange
        var originalMessage = new TextMessage
        {
            Text = "Test message with converter",
            Role = Role.User,
            Metadata = ImmutableDictionary<string, object>.Empty.Add("custom", "value"),
        };

        var options = GetOptionsWithConverter();

        // Act - Verify the message goes through our converter
        var json = JsonSerializer.Serialize<IMessage>(originalMessage, options);
        TestContextLogger.LogDebug("Serialized JSON: {Json}", json);

        // Verify the type discriminator was added
        var jsonDocument = JsonDocument.Parse(json);
        Assert.True(jsonDocument.RootElement.TryGetProperty("$type", out var typeProperty));

        // Deserialize back to IMessage
        var deserializedMessage = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(deserializedMessage);
        _ = Assert.IsType<TextMessage>(deserializedMessage);

        var textMessage = (TextMessage)deserializedMessage;
        Assert.Equal(originalMessage.Text, textMessage.Text);
        Assert.Equal(originalMessage.Role, textMessage.Role);

        // Check if metadata was preserved
        Assert.NotNull(textMessage.Metadata);
        Assert.True(textMessage.Metadata.ContainsKey("custom"));
        Assert.Equal("value", textMessage.Metadata["custom"]);
    }

    [Fact]
    public void RoundTrip_ToolCallMessage_Singular_PreservesAllData()
    {
        // Arrange
        IMessage originalMessage = new ToolCallMessage
        {
            FunctionName = "get_weather",
            FunctionArgs = """{"location":"San Francisco"}""",
            ToolCallId = "call_123",
            Index = 0,
            ToolCallIdx = 0,
            Role = Role.Assistant,
            FromAgent = "test-agent",
            GenerationId = "gen-1",
        };

        var options = GetOptionsWithConverter();

        // Act
        var json = JsonSerializer.Serialize(originalMessage, options);
        TestContextLogger.LogDebug("Serialized ToolCallMessage. Json: {Json}", json);

        var deserializedMessage = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(deserializedMessage);
        _ = Assert.IsType<ToolCallMessage>(deserializedMessage);

        var toolCallMessage = (ToolCallMessage)deserializedMessage;
        Assert.Equal("get_weather", toolCallMessage.FunctionName);
        Assert.Equal("""{"location":"San Francisco"}""", toolCallMessage.FunctionArgs);
        Assert.Equal("call_123", toolCallMessage.ToolCallId);
        Assert.Equal(0, toolCallMessage.Index);
        Assert.Equal(0, toolCallMessage.ToolCallIdx);
        Assert.Equal(Role.Assistant, toolCallMessage.Role);
        Assert.Equal("test-agent", toolCallMessage.FromAgent);
        Assert.Equal("gen-1", toolCallMessage.GenerationId);
        Assert.Equal(ExecutionTarget.LocalFunction, toolCallMessage.ExecutionTarget);
    }

    [Fact]
    public void RoundTrip_ToolCallUpdateMessage_Singular_PreservesAllData()
    {
        // Arrange
        IMessage originalMessage = new ToolCallUpdateMessage
        {
            FunctionName = "get_weather",
            FunctionArgs = """{"loc""",
            ToolCallId = "call_123",
            Index = 0,
            Role = Role.Assistant,
            FromAgent = "test-agent",
            GenerationId = "gen-1",
            ChunkIdx = 0,
            IsUpdate = true,
        };

        var options = GetOptionsWithConverter();

        // Act
        var json = JsonSerializer.Serialize(originalMessage, options);
        TestContextLogger.LogDebug("Serialized ToolCallUpdateMessage. Json: {Json}", json);

        var deserializedMessage = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(deserializedMessage);
        _ = Assert.IsType<ToolCallUpdateMessage>(deserializedMessage);

        var toolCallUpdateMessage = (ToolCallUpdateMessage)deserializedMessage;
        Assert.Equal("get_weather", toolCallUpdateMessage.FunctionName);
        Assert.Equal("""{"loc""", toolCallUpdateMessage.FunctionArgs);
        Assert.Equal("call_123", toolCallUpdateMessage.ToolCallId);
        Assert.Equal(0, toolCallUpdateMessage.Index);
        Assert.Equal(Role.Assistant, toolCallUpdateMessage.Role);
        Assert.Equal("test-agent", toolCallUpdateMessage.FromAgent);
        Assert.Equal("gen-1", toolCallUpdateMessage.GenerationId);
        Assert.True(toolCallUpdateMessage.IsUpdate);
    }

    [Fact]
    public void RoundTrip_ToolCallResultMessage_Singular_PreservesAllData()
    {
        // Arrange
        IMessage originalMessage = new ToolCallResultMessage
        {
            ToolCallId = "call_123",
            Result = "Sunny, 72°F",
            Role = Role.User,
            FromAgent = "tool-executor",
            GenerationId = "gen-1",
        };

        var options = GetOptionsWithConverter();

        // Act
        var json = JsonSerializer.Serialize(originalMessage, options);
        TestContextLogger.LogDebug("Serialized ToolCallResultMessage. Json: {Json}", json);

        var deserializedMessage = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(deserializedMessage);
        _ = Assert.IsType<ToolCallResultMessage>(deserializedMessage);

        var toolCallResultMessage = (ToolCallResultMessage)deserializedMessage;
        Assert.Equal("call_123", toolCallResultMessage.ToolCallId);
        Assert.Equal("Sunny, 72°F", toolCallResultMessage.Result);
        Assert.Equal(Role.User, toolCallResultMessage.Role);
        Assert.Equal("tool-executor", toolCallResultMessage.FromAgent);
        Assert.Equal("gen-1", toolCallResultMessage.GenerationId);
        Assert.Equal(ExecutionTarget.LocalFunction, toolCallResultMessage.ExecutionTarget);
        Assert.Null(toolCallResultMessage.ToolName);
        Assert.False(toolCallResultMessage.IsError);
        Assert.Null(toolCallResultMessage.ErrorCode);
    }

    [Fact]
    public void Deserialize_LegacyServerToolUse_WithDiscriminator_ReturnsUnifiedToolCallMessage()
    {
        var json =
            """
            {
              "$type": "server_tool_use",
              "tool_use_id": "srvtoolu_1",
              "tool_name": "web_search",
              "input": { "query": "latest ai news" },
              "role": "assistant"
            }
            """;

        var options = GetOptionsWithConverter();

        var message = JsonSerializer.Deserialize<IMessage>(json, options);

        var toolCall = Assert.IsType<ToolCallMessage>(message);
        Assert.Equal("srvtoolu_1", toolCall.ToolCallId);
        Assert.Equal("web_search", toolCall.FunctionName);
        Assert.Equal(ExecutionTarget.ProviderServer, toolCall.ExecutionTarget);
        var args = JsonDocument.Parse(toolCall.FunctionArgs ?? "{}").RootElement;
        Assert.Equal("latest ai news", args.GetProperty("query").GetString());
    }

    [Fact]
    public void Deserialize_LegacyServerToolResult_WithoutDiscriminator_ReturnsUnifiedToolCallResultMessage()
    {
        var json =
            """
            {
              "tool_use_id": "srvtoolu_1",
              "tool_name": "web_search",
              "result": [{ "type": "web_search_result", "title": "A" }],
              "is_error": false,
              "role": "assistant"
            }
            """;

        var options = GetOptionsWithConverter();

        var message = JsonSerializer.Deserialize<IMessage>(json, options);

        var toolResult = Assert.IsType<ToolCallResultMessage>(message);
        Assert.Equal("srvtoolu_1", toolResult.ToolCallId);
        Assert.Equal("web_search", toolResult.ToolName);
        Assert.Equal(ExecutionTarget.ProviderServer, toolResult.ExecutionTarget);
        Assert.False(toolResult.IsError);
    }

    [Fact]
    public void Serialize_ProviderServerToolCallMessage_UsesToolCallDiscriminator()
    {
        IMessage message = new ToolCallMessage
        {
            ToolCallId = "srvtoolu_1",
            FunctionName = "web_search",
            FunctionArgs = """{"query":"x"}""",
            ExecutionTarget = ExecutionTarget.ProviderServer,
            Role = Role.Assistant,
        };

        var options = GetOptionsWithConverter();

        var json = JsonSerializer.Serialize(message, options);
        var root = JsonDocument.Parse(json).RootElement;

        // Server tool calls use the same "tool_call" discriminator as local tool calls,
        // with execution_target distinguishing them.
        Assert.Equal("tool_call", root.GetProperty("$type").GetString());
        Assert.Equal("srvtoolu_1", root.GetProperty("tool_call_id").GetString());
        Assert.Equal("ProviderServer", root.GetProperty("execution_target").GetString());
    }

    [Fact]
    public void Serialize_ProviderServerToolCallResultMessage_UsesToolCallResultDiscriminator()
    {
        IMessage message = new ToolCallResultMessage
        {
            ToolCallId = "srvtoolu_1",
            ToolName = "web_search",
            Result = "[]",
            ExecutionTarget = ExecutionTarget.ProviderServer,
            Role = Role.Assistant,
        };

        var options = GetOptionsWithConverter();

        var json = JsonSerializer.Serialize(message, options);
        var root = JsonDocument.Parse(json).RootElement;

        // Server tool results use the same "tool_call_result" discriminator as local tool results,
        // with execution_target distinguishing them.
        Assert.Equal("tool_call_result", root.GetProperty("$type").GetString());
        Assert.Equal("srvtoolu_1", root.GetProperty("tool_call_id").GetString());
        Assert.Equal("ProviderServer", root.GetProperty("execution_target").GetString());
    }

    [Theory]
    [InlineData("tool_call", typeof(ToolCallMessage))]
    [InlineData("tool_call_update", typeof(ToolCallUpdateMessage))]
    [InlineData("tool_call_result", typeof(ToolCallResultMessage))]
    public void Deserialize_SingularToolCallTypes_WithTypeDiscriminator_ReturnsCorrectType(
        string typeDiscriminator,
        Type expectedType
    )
    {
        // Arrange
        string json;
        if (expectedType == typeof(ToolCallMessage))
        {
            json =
                $@"{{
                ""$type"": ""{typeDiscriminator}"",
                ""function_name"": ""test_function"",
                ""function_args"": ""{{}}"",
                ""tool_call_id"": ""call_1"",
                ""role"": ""assistant""
            }}";
        }
        else if (expectedType == typeof(ToolCallUpdateMessage))
        {
            json =
                $@"{{
                ""$type"": ""{typeDiscriminator}"",
                ""function_name"": ""test_function"",
                ""function_args"": ""{{\""{{}}"",
                ""tool_call_id"": ""call_1"",
                ""isUpdate"": true,
                ""role"": ""assistant""
            }}";
        }
        else
        {
            json =
                $@"{{
                ""$type"": ""{typeDiscriminator}"",
                ""tool_call_id"": ""call_1"",
                ""result"": ""test result"",
                ""role"": ""user""
            }}";
        }

        var options = GetOptionsWithConverter();

        // Act
        var message = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(message);
        Assert.IsType(expectedType, message);
    }

    [Fact]
    public void RoundTrip_ProviderServerToolCallMessage_PreservesExecutionTarget()
    {
        // Arrange
        IMessage originalMessage = new ToolCallMessage
        {
            FunctionName = "web_search",
            FunctionArgs = """{"query":"latest ai news"}""",
            ToolCallId = "srvtoolu_42",
            Index = 0,
            ToolCallIdx = 0,
            ExecutionTarget = ExecutionTarget.ProviderServer,
            Role = Role.Assistant,
            FromAgent = "anthropic-agent",
            GenerationId = "gen-srv-1",
        };

        var options = GetOptionsWithConverter();

        // Act
        var json = JsonSerializer.Serialize(originalMessage, options);
        TestContextLogger.LogDebug("Serialized ProviderServer ToolCallMessage. Json: {Json}", json);

        var deserializedMessage = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(deserializedMessage);
        var toolCallMessage = Assert.IsType<ToolCallMessage>(deserializedMessage);
        Assert.Equal("web_search", toolCallMessage.FunctionName);
        Assert.Equal("""{"query":"latest ai news"}""", toolCallMessage.FunctionArgs);
        Assert.Equal("srvtoolu_42", toolCallMessage.ToolCallId);
        Assert.Equal(ExecutionTarget.ProviderServer, toolCallMessage.ExecutionTarget);
        Assert.Equal(Role.Assistant, toolCallMessage.Role);
        Assert.Equal("anthropic-agent", toolCallMessage.FromAgent);
        Assert.Equal("gen-srv-1", toolCallMessage.GenerationId);
    }

    [Fact]
    public void RoundTrip_ProviderServerToolCallResultMessage_PreservesExecutionTarget()
    {
        // Arrange
        IMessage originalMessage = new ToolCallResultMessage
        {
            ToolCallId = "srvtoolu_42",
            ToolName = "web_search",
            Result = """[{"type":"web_search_result","title":"AI News"}]""",
            ExecutionTarget = ExecutionTarget.ProviderServer,
            IsError = false,
            Role = Role.Assistant,
            FromAgent = "anthropic-agent",
            GenerationId = "gen-srv-1",
        };

        var options = GetOptionsWithConverter();

        // Act
        var json = JsonSerializer.Serialize(originalMessage, options);
        TestContextLogger.LogDebug("Serialized ProviderServer ToolCallResultMessage. Json: {Json}", json);

        var deserializedMessage = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(deserializedMessage);
        var toolCallResultMessage = Assert.IsType<ToolCallResultMessage>(deserializedMessage);
        Assert.Equal("srvtoolu_42", toolCallResultMessage.ToolCallId);
        Assert.Equal("web_search", toolCallResultMessage.ToolName);
        Assert.Equal("""[{"type":"web_search_result","title":"AI News"}]""", toolCallResultMessage.Result);
        Assert.Equal(ExecutionTarget.ProviderServer, toolCallResultMessage.ExecutionTarget);
        Assert.False(toolCallResultMessage.IsError);
        Assert.Equal(Role.Assistant, toolCallResultMessage.Role);
        Assert.Equal("anthropic-agent", toolCallResultMessage.FromAgent);
        Assert.Equal("gen-srv-1", toolCallResultMessage.GenerationId);
    }

    [Fact]
    public void RoundTrip_ImageMessage_WithMediaType_PreservesMediaType()
    {
        // Arrange
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }; // PNG header
        var binaryData = BinaryData.FromBytes(imageBytes, "image/png");

        IMessage originalMessage = new ImageMessage
        {
            ImageData = binaryData,
            Role = Role.Assistant,
            FromAgent = "test-agent",
            GenerationId = "gen-123",
        };

        var options = GetOptionsWithConverter();

        // Act
        var json = JsonSerializer.Serialize(originalMessage, options);
        TestContextLogger.LogDebug("Serialized ImageMessage with media_type. Json: {Json}", json);

        var deserializedMessage = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(deserializedMessage);
        var imageMessage = Assert.IsType<ImageMessage>(deserializedMessage);
        Assert.Equal("image/png", imageMessage.ImageData.MediaType);
        Assert.Equal(imageBytes, imageMessage.ImageData.ToArray());
        Assert.Equal(Role.Assistant, imageMessage.Role);
        Assert.Equal("test-agent", imageMessage.FromAgent);
        Assert.Equal("gen-123", imageMessage.GenerationId);
    }

    [Fact]
    public void Serialize_ImageMessage_OutputsMediaTypeProperty()
    {
        // Arrange
        var imageBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // JPEG header
        var binaryData = BinaryData.FromBytes(imageBytes, "image/jpeg");

        IMessage message = new ImageMessage
        {
            ImageData = binaryData,
            Role = Role.User,
        };

        var options = GetOptionsWithConverter();

        // Act
        var json = JsonSerializer.Serialize(message, options);
        TestContextLogger.LogDebug("Serialized ImageMessage. Json: {Json}", json);

        // Assert
        var jsonDocument = JsonDocument.Parse(json);
        var root = jsonDocument.RootElement;

        Assert.True(root.TryGetProperty("media_type", out var mediaTypeProperty));
        Assert.Equal("image/jpeg", mediaTypeProperty.GetString());
        Assert.True(root.TryGetProperty("image_data", out var imageDataProperty));
        Assert.Equal(Convert.ToBase64String(imageBytes), imageDataProperty.GetString());
    }

    [Fact]
    public void Deserialize_ImageMessage_WithMediaTypeBeforeImageData_PreservesMediaType()
    {
        // Arrange - media_type comes before image_data in JSON
        var imageBytes = "GIF8"u8.ToArray(); // GIF header
        var base64Data = Convert.ToBase64String(imageBytes);
        var json =
            $@"{{
            ""$type"": ""image"",
            ""media_type"": ""image/gif"",
            ""image_data"": ""{base64Data}"",
            ""role"": ""assistant""
        }}";

        var options = GetOptionsWithConverter();

        // Act
        var message = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(message);
        var imageMessage = Assert.IsType<ImageMessage>(message);
        Assert.Equal("image/gif", imageMessage.ImageData.MediaType);
        Assert.Equal(imageBytes, imageMessage.ImageData.ToArray());
    }

    [Fact]
    public void Deserialize_ImageMessage_WithImageDataBeforeMediaType_PreservesMediaType()
    {
        // Arrange - image_data comes before media_type in JSON
        var imageBytes = "RIFF"u8.ToArray(); // WebP header
        var base64Data = Convert.ToBase64String(imageBytes);
        var json =
            $@"{{
            ""$type"": ""image"",
            ""image_data"": ""{base64Data}"",
            ""media_type"": ""image/webp"",
            ""role"": ""user""
        }}";

        var options = GetOptionsWithConverter();

        // Act
        var message = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(message);
        var imageMessage = Assert.IsType<ImageMessage>(message);
        Assert.Equal("image/webp", imageMessage.ImageData.MediaType);
        Assert.Equal(imageBytes, imageMessage.ImageData.ToArray());
    }

    [Fact]
    public void Deserialize_ImageMessage_WithoutMediaType_DefaultsToNoMediaType()
    {
        // Arrange - no media_type in JSON (backward compatibility)
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var base64Data = Convert.ToBase64String(imageBytes);
        var json =
            $@"{{
            ""$type"": ""image"",
            ""image_data"": ""{base64Data}"",
            ""role"": ""assistant""
        }}";

        var options = GetOptionsWithConverter();

        // Act
        var message = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(message);
        var imageMessage = Assert.IsType<ImageMessage>(message);
        Assert.Null(imageMessage.ImageData.MediaType); // No media type when not specified
        Assert.Equal(imageBytes, imageMessage.ImageData.ToArray());
    }

    [Fact]
    public void RoundTrip_ImageMessage_WithAllProperties_PreservesAllData()
    {
        // Arrange
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var binaryData = BinaryData.FromBytes(imageBytes, "image/png");

        IMessage originalMessage = new ImageMessage
        {
            ImageData = binaryData,
            Role = Role.Assistant,
            FromAgent = "vision-agent",
            GenerationId = "gen-456",
            ThreadId = "thread-789",
            RunId = "run-abc",
            ParentRunId = "parent-run-xyz",
            MessageOrderIdx = 5,
            Metadata = ImmutableDictionary<string, object>.Empty.Add("source", "screenshot"),
        };

        var options = GetOptionsWithConverter();

        // Act
        var json = JsonSerializer.Serialize(originalMessage, options);
        TestContextLogger.LogDebug("Full ImageMessage JSON: {Json}", json);

        var deserializedMessage = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(deserializedMessage);
        var imageMessage = Assert.IsType<ImageMessage>(deserializedMessage);

        Assert.Equal("image/png", imageMessage.ImageData.MediaType);
        Assert.Equal(imageBytes, imageMessage.ImageData.ToArray());
        Assert.Equal(Role.Assistant, imageMessage.Role);
        Assert.Equal("vision-agent", imageMessage.FromAgent);
        Assert.Equal("gen-456", imageMessage.GenerationId);
        Assert.Equal("thread-789", imageMessage.ThreadId);
        Assert.Equal("run-abc", imageMessage.RunId);
        Assert.Equal("parent-run-xyz", imageMessage.ParentRunId);
        Assert.Equal(5, imageMessage.MessageOrderIdx);
        Assert.NotNull(imageMessage.Metadata);
        Assert.True(imageMessage.Metadata.ContainsKey("source"));
        Assert.Equal("screenshot", imageMessage.Metadata["source"]);
    }
}
