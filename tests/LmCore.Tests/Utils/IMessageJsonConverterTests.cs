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
        Console.WriteLine(json);

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
        var toolCall = new ToolCall { FunctionName = "test_function", FunctionArgs = """{"arg1": "value1"}""", ToolCallId = "tool-1" };

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
        Console.WriteLine(json);

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
                ToolCalls = [new ToolCall { FunctionName = "test_function", FunctionArgs = "{}", ToolCallId = "id1" }],
                Role = Role.Assistant,
            },
        };

        var options = GetOptionsWithConverter();

        foreach (var originalMessage in messages)
        {
            // Act
            var json = JsonSerializer.Serialize(originalMessage, options);
            Console.WriteLine($"Serialized {originalMessage.GetType().Name}: {json}");

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
        Console.WriteLine(json);

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
        Console.WriteLine($"Serialized ToolCallMessage: {json}");

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
        Console.WriteLine($"Serialized ToolCallUpdateMessage: {json}");

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
        Console.WriteLine($"Serialized ToolCallResultMessage: {json}");

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
}
