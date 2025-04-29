using System.Collections.Immutable;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Utils;

public class IMessageJsonConverterTests
{
    private JsonSerializerOptions GetOptionsWithConverter()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
        };

        options.Converters.Add(new IMessageJsonConverter());
        options.Converters.Add(new TextMessageJsonConverter());
        options.Converters.Add(new ImageMessageJsonConverter());
        options.Converters.Add(new ToolsCallMessageJsonConverter());
        options.Converters.Add(new ToolsCallResultMessageJsonConverter());
        options.Converters.Add(new ToolsCallAggregateMessageJsonConverter());
        options.Converters.Add(new TextUpdateMessageJsonConverter());
        options.Converters.Add(new ToolsCallUpdateMessageJsonConverter());
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
            Metadata = ImmutableDictionary<string, object>.Empty.Add("test", "value")
        };

        var options = GetOptionsWithConverter();

        // Act
        string json = JsonSerializer.Serialize(message, options);
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
        string json = @"{
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
        Assert.IsType<TextMessage>(message);

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
        string json = @"{
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
        Assert.IsType<TextMessage>(message);

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
        var toolCall = new ToolCall(
            FunctionName: "test_function",
            FunctionArgs: """{"arg1": "value1"}""")
        {
            ToolCallId = "tool-1"
        };

        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls = ImmutableList.Create(toolCall),
            Role = Role.Assistant,
            FromAgent = "assistant-agent",
            GenerationId = "gen-1",
            Metadata = ImmutableDictionary<string, object>.Empty.Add("source", "tool-call")
        };

        var toolCallResult = new ToolsCallResultMessage
        {
            ToolCallResults = ImmutableList.Create(new ToolCallResult("tool-1", "function result")),
            Role = Role.User,
            FromAgent = "user-agent",
            Metadata = ImmutableDictionary<string, object>.Empty.Add("source", "tool-result")
        };

        IMessage originalMessage = new ToolsCallAggregateMessage(toolCallMessage, toolCallResult, "combined-agent");

        var options = GetOptionsWithConverter();

        // Act
        string json = JsonSerializer.Serialize(originalMessage, options);
        Console.WriteLine(json);

        var deserializedMessage = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(deserializedMessage);
        Assert.IsType<ToolsCallAggregateMessage>(deserializedMessage);

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
        string json = $@"{{
            ""$type"": ""{typeDiscriminator}"",
            ""role"": ""assistant""
        }}";

        if (expectedType == typeof(TextMessage))
        {
            json = $@"{{
                ""$type"": ""{typeDiscriminator}"",
                ""text"": ""Required text"",
                ""role"": ""assistant""
            }}";
        }
        else if (expectedType == typeof(ImageMessage))
        {
            json = $@"{{
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
                ToolCalls = ImmutableList.Create(
                    new ToolCall("test_function", "{}"){ ToolCallId = "id1" }
                ),
                Role = Role.Assistant
            }
        };

        var options = GetOptionsWithConverter();

        foreach (var originalMessage in messages)
        {
            // Act
            string json = JsonSerializer.Serialize(originalMessage, options);
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
            Metadata = ImmutableDictionary<string, object>.Empty.Add("custom", "value")
        };

        var options = GetOptionsWithConverter();

        // Act - Verify the message goes through our converter
        string json = JsonSerializer.Serialize<IMessage>(originalMessage, options);
        Console.WriteLine(json);

        // Verify the type discriminator was added
        var jsonDocument = JsonDocument.Parse(json);
        Assert.True(jsonDocument.RootElement.TryGetProperty("$type", out var typeProperty));

        // Deserialize back to IMessage
        var deserializedMessage = JsonSerializer.Deserialize<IMessage>(json, options);

        // Assert
        Assert.NotNull(deserializedMessage);
        Assert.IsType<TextMessage>(deserializedMessage);

        var textMessage = (TextMessage)deserializedMessage;
        Assert.Equal(originalMessage.Text, textMessage.Text);
        Assert.Equal(originalMessage.Role, textMessage.Role);

        // Check if metadata was preserved
        Assert.NotNull(textMessage.Metadata);
        Assert.True(textMessage.Metadata.ContainsKey("custom"));
        Assert.Equal("value", textMessage.Metadata["custom"]);
    }
}