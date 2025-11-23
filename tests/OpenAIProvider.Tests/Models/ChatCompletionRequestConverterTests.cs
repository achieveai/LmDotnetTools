using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Models
{
    public class ChatCompletionRequestConverterTests
    {
        // Small value to compensate for floating-point precision issues
        private const double FloatPrecisionDelta = 0.00001;
        private static readonly string[] expected = ["stop1", "stop2"];

        [Fact]
        public void Create_BasicMessages_CreatesCorrectRequest()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.System, Text = "You are a helpful assistant" },
                new TextMessage { Role = Role.User, Text = "Hello, how are you?" },
            };

            var options = new GenerateReplyOptions
            {
                ModelId = "gpt-4",
                Temperature = 0.5f,
                MaxToken = 2000,
            };

            // Act
            var result = ChatCompletionRequest.FromMessages(messages, options);

            // Assert
            Assert.Equal("gpt-4", result.Model);
            Assert.Equal(0.5d, result.Temperature, precision: 5);
            Assert.Equal(2000, result.MaxTokens);
            Assert.Equal(2, result.Messages.Count);

            Assert.Equal(RoleEnum.System, result.Messages[0].Role);
            Assert.Equal("You are a helpful assistant", result.Messages[0].Content!.Get<string>());

            Assert.Equal(RoleEnum.User, result.Messages[1].Role);
            Assert.Equal("Hello, how are you?", result.Messages[1].Content!.Get<string>());
        }

        [Fact]
        public void Create_WithAllOptions_SetsAllProperties()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.User, Text = "Generate something creative" },
            };

            var options = new GenerateReplyOptions
            {
                ModelId = "gpt-4-turbo",
                Temperature = 0.9f,
                MaxToken = 1000,
                TopP = 0.95f,
                StopSequence = ["stop1", "stop2"],
                RandomSeed = 42,
            };

            // Act
            var result = ChatCompletionRequest.FromMessages(messages, options);

            // Assert
            Assert.Equal("gpt-4-turbo", result.Model);
            Assert.Equal(0.9d, result.Temperature, precision: 5);
            Assert.Equal(1000, result.MaxTokens);

            // Make sure TopP is not null before accessing Value
            _ = Assert.NotNull(result.TopP);
            Assert.Equal(0.95d, result.TopP!.Value, precision: 5);

            Assert.Equal(expected, result.Stop);
            Assert.Equal(42, result.RandomSeed);
        }

        [Fact]
        public void Create_WithFunctionCalls_SetsFunctionTools()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.User, Text = "What's the weather like?" },
            };

            // Create a function using the proper schema
            var function = new FunctionContract
            {
                Name = "get_weather",
                Description = "Get the current weather",             // Set parameters as an array of FunctionParameterContract
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "location",
                        Description = "The city name",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        IsRequired = true,
                    },
                }
            };

            var options = new GenerateReplyOptions { ModelId = "gpt-4", Functions = [function] };

            // Act
            var result = ChatCompletionRequest.FromMessages(messages, options);

            // Assert
            Assert.NotNull(result.Tools);
            _ = Assert.Single(result.Tools);

            var tool = result.Tools[0];
            Assert.Equal("function", tool.Type);
            Assert.Equal("get_weather", tool.Function.Name);
            Assert.Equal("Get the current weather", tool.Function.Description);
        }

        [Fact]
        public void Create_WithVariousMessageTypes_ConvertsCorrectly()
        {
            // Arrange
            var toolCall = new ToolCall { FunctionName = "get_weather", FunctionArgs = "{\"location\":\"New York\"}", ToolCallId = "call_123" };

            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.System, Text = "You are a helpful assistant" },
                new TextMessage { Role = Role.User, Text = "What's the weather in New York?" },
                new ToolsCallMessage { Role = Role.Assistant, ToolCalls = [toolCall] },
                new TextMessage
                {
                    Role = Role.Tool,
                    Text = "{\"temp\":72,\"condition\":\"sunny\"}",
                    FromAgent = "get_weather",
                },
                new TextMessage { Role = Role.Assistant, Text = "It's 72 degrees and sunny in New York." },
            };

            var options = new GenerateReplyOptions { ModelId = "gpt-4" };

            // Act
            var result = ChatCompletionRequest.FromMessages(messages, options);

            // Assert
            Assert.Equal(5, result.Messages.Count);

            // Check tool call message conversion
            var toolCallMessage = result.Messages[2];
            Assert.Equal(RoleEnum.Assistant, toolCallMessage.Role);
            Assert.NotNull(toolCallMessage.ToolCalls);
            _ = Assert.Single(toolCallMessage.ToolCalls);
            Assert.Equal("get_weather", toolCallMessage.ToolCalls[0].Function.Name);

            // Check function response conversion
            var functionResponseMessage = result.Messages[3];
            Assert.Equal(RoleEnum.Tool, functionResponseMessage.Role);

            // Use string.Contains instead of direct comparison for robustness
            var content = functionResponseMessage.Content?.Get<string>() ?? string.Empty;
            Assert.Contains("temp", content);
            Assert.Contains("72", content);
            Assert.Contains("sunny", content);
        }

        [Fact]
        public void Create_WithDefaultOptions_UsesSensibleDefaults()
        {
            // Arrange
            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.User, Text = "Hello" },
            };

            // Act - passing null options
            var result = ChatCompletionRequest.FromMessages(messages, null);

            // Assert
            Assert.Equal("", result.Model); // Empty string as default model
            Assert.Equal(0.7d, result.Temperature, precision: 5); // Default temperature
            Assert.Equal(1024, result.MaxTokens); // Default max tokens
            _ = Assert.Single(result.Messages);
            Assert.Equal(RoleEnum.User, result.Messages[0].Role);
        }
    }
}
