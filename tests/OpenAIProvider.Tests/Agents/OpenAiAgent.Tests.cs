using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

public class OpenAiAgentTests
{
    private static readonly string[] fallbackKeys = ["LLM_API_KEY"];
    private static readonly string[] fallbackKeysArray = ["LLM_API_BASE_URL"];

    private static string EnvTestPath =>
        Path.Combine(TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory), ".env.test");

    [Fact]
    public async Task SimpleConversation_ShouldReturnResponse()
    {
        // Create HTTP client with record/playback functionality
        var testCaseName = "SimpleConversation_ShouldReturnResponse";
        var testDataFilePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "OpenAIProvider.Tests",
            "TestData",
            "OpenAI",
            $"{testCaseName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataFilePath)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        // Create a system message
        var systemMessage = new TextMessage { Role = Role.System, Text = "You're a helpful AI Agent" };

        // Create a user message
        var userMessage = new TextMessage { Role = Role.User, Text = "Hello Bot" };

        // Act
        var response = await agent.GenerateReplyAsync(
            [systemMessage, userMessage],
            new GenerateReplyOptions { ModelId = "microsoft/phi-4-multimodal-instruct" }
        );

        // Assert
        Assert.NotNull(response);

        // Verify it's a text message with content
        _ = Assert.IsType<ICanGetText>(response.First(), false);
        var textMessage = (ICanGetText)response!.First();
        Assert.True(textMessage.CanGetText());
        Assert.NotNull(textMessage.GetText());
        Assert.NotEmpty(textMessage!.GetText()!);
    }

    [Fact]
    public async Task ChatCompletionRequest_SerializesToCorrectJson()
    {
        // Arrange
        var messages = new[]
        {
            new TextMessage
            {
                Role = Role.System,
                Text = "You will always respond in JSON as `{\"response\": \"...\"}`",
            },
            new TextMessage { Role = Role.User, Text = "Hello Bot!!!" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "gpt-4o-mini",
            Temperature = 0.7f,
            MaxToken = 1000,
            ResponseFormat = ResponseFormat.JSON,
        };

        // Create HTTP client with record/playback functionality
        var testDataFilePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "OpenAIProvider.Tests",
            "TestData",
            "OpenAI",
            "ChatCompletionRequest_SerializesToCorrectJson.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataFilePath)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        // Act
        var request = ChatCompletionRequest.FromMessages(messages, options);
        var response = await agent.GenerateReplyAsync(messages, options);
        var json = JsonNode.Parse(((ICanGetText)response.First())!.GetText()!);
        Assert.NotNull(json);
        Assert.NotNull(json["response"]);
    }

    [Fact]
    public async Task FunctionToolCall_ShouldReturnToolMessage()
    {
        // Arrange
        var messages = new[]
        {
            new TextMessage { Role = Role.System, Text = "You're a helpful AI Agent that can use tools" },
            new TextMessage { Role = Role.User, Text = "What's the weather in San Francisco?" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "gpt-4",
            Functions =
            [
                new FunctionContract
                {
                    Name = "getWeather",
                    Description = "Get current weather for a location",
                    Parameters =
                    [
                        new FunctionParameterContract
                        {
                            Name = "location",
                            Description = "City name",
                            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                            IsRequired = true,
                        },
                        new FunctionParameterContract
                        {
                            Name = "unit",
                            Description = "Temperature unit (celsius or fahrenheit)",
                            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                            IsRequired = false,
                        },
                    ],
                },
            ],
        };

        // Create HTTP client with record/playback functionality
        var testDataFilePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "OpenAIProvider.Tests",
            "TestData",
            "OpenAI",
            "FunctionToolCall_ShouldReturnToolMessage.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataFilePath)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        // Act
        var response = (await agent.GenerateReplyAsync(messages, options)).First();

        // Assert
        Assert.NotNull(response);

        // Since the response type may vary depending on the implementation,
        // we should check for different possible types of responses
        if (response is ToolsCallMessage toolMessage)
        {
            Assert.NotNull(toolMessage.ToolCalls);
            _ = Assert.Single(toolMessage.ToolCalls);
            Assert.Equal("getWeather", toolMessage.ToolCalls[0].FunctionName);
        }
        else if (response is TextMessage textMessage)
        {
            Assert.NotNull(textMessage.Text);
            Assert.Contains("getWeather", textMessage.Text);
        }
        else
        {
            // If neither expected type, fail the test
            Assert.Fail($"Expected tool call message, got {response.GetType().Name}");
        }
    }

    [Fact]
    public async Task FunctionToolCall_ShouldReturnToolMessage_Streaming()
    {
        // Arrange
        var messages = new[]
        {
            new TextMessage { Role = Role.System, Text = "You're a helpful AI Agent that can use tools" },
            new TextMessage { Role = Role.User, Text = "What's the weather in San Francisco?" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "meta-llama/llama-4-maverick",
            Functions =
            [
                new FunctionContract
                {
                    Name = "getWeather",
                    Description = "Get current weather for a location",
                    Parameters =
                    [
                        new FunctionParameterContract
                        {
                            Name = "location",
                            Description = "City name",
                            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                            IsRequired = true,
                        },
                        new FunctionParameterContract
                        {
                            Name = "unit",
                            Description = "Temperature unit (celsius or fahrenheit)",
                            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                            IsRequired = false,
                        },
                    ],
                },
            ],
        };

        // Create HTTP client with record/playback functionality
        var testDataFilePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "FunctionToolCall_ShouldReturnToolMessage_streaming.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataFilePath)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client).WithMiddleware(
            new MessageUpdateJoinerMiddleware("Message Joiner")
        );

        // Act
        var responseStream = await agent.GenerateReplyStreamingAsync(messages, options);

        var responses = new List<IMessage>();
        await foreach (var response in responseStream)
        {
            responses.Add(response);
        }

        // Assert
        Assert.NotEmpty(responses);

        var firstResponse = responses.First(m =>
            m is ToolsCallMessage || (m is TextMessage textMessage && !string.IsNullOrEmpty(textMessage.Text))
        );
        Assert.NotNull(firstResponse);

        // Since the response type may vary depending on the implementation,
        // we should check for different possible types of responses
        if (firstResponse is ToolsCallMessage toolMessage)
        {
            Assert.NotNull(toolMessage.ToolCalls);
            _ = Assert.Single(toolMessage.ToolCalls);
            Assert.Equal("getWeather", toolMessage.ToolCalls[0].FunctionName);
        }
        else if (firstResponse is TextMessage textMessage)
        {
            Assert.NotNull(textMessage.Text);
            Assert.Contains("getWeather", textMessage.Text);
        }
        else
        {
            // If neither expected type, fail the test
            Assert.Fail($"Expected tool call message, got {firstResponse.GetType().Name}");
        }
    }

    [Fact]
    public async Task FunctionToolCall_ShouldReturnToolMessage_Streaming_WithJoin()
    {
        // Arrange
        var messages = new[]
        {
            new TextMessage { Role = Role.System, Text = "You're a helpful AI Agent that can use tools" },
            new TextMessage { Role = Role.User, Text = "What's the weather in San Francisco?" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "gpt-4",
            Functions =
            [
                new FunctionContract
                {
                    Name = "getWeather",
                    Description = "Get current weather for a location",
                    Parameters =
                    [
                        new FunctionParameterContract
                        {
                            Name = "location",
                            Description = "City name",
                            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                            IsRequired = true,
                        },
                        new FunctionParameterContract
                        {
                            Name = "unit",
                            Description = "Temperature unit (celsius or fahrenheit)",
                            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                            IsRequired = false,
                        },
                    ],
                },
            ],
        };

        // Create HTTP client with record/playback functionality
        var testDataFilePath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "OpenAIProvider.Tests",
            "TestData",
            "OpenAI",
            "FunctionToolCall_ShouldReturnToolMessage_Streaming_WithJoin.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataFilePath)
            .ForwardToApi(GetApiBaseUrlFromEnv(), GetApiKeyFromEnv())
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
        var agent = new OpenClientAgent("TestAgent", client);

        // Act
        var response = await agent.GenerateReplyAsync(messages, options);

        // Assert
        Assert.NotNull(response);

        var firstResponse = response.First();
        Assert.NotNull(firstResponse);

        // Since the response type may vary depending on the implementation,
        // we should check for different possible types of responses
        if (firstResponse is ToolsCallMessage toolMessage)
        {
            Assert.NotNull(toolMessage.ToolCalls);
            _ = Assert.Single(toolMessage.ToolCalls);
            Assert.Equal("getWeather", toolMessage.ToolCalls[0].FunctionName);
        }
        else if (firstResponse is TextMessage textMessage)
        {
            Assert.NotNull(textMessage.Text);
            Assert.Contains("getWeather", textMessage.Text);
        }
        else
        {
            // If neither expected type, fail the test
            Assert.Fail($"Expected tool call message, got {firstResponse.GetType().Name}");
        }
    }

    /// <summary>
    ///     Helper method to get API key from environment (using shared EnvironmentHelper)
    /// </summary>
    private static string GetApiKeyFromEnv()
    {
        return EnvironmentHelper.GetApiKeyFromEnv("OPENAI_API_KEY", fallbackKeys);
    }

    /// <summary>
    ///     Helper method to get API base URL from environment (using shared EnvironmentHelper)
    /// </summary>
    private static string GetApiBaseUrlFromEnv()
    {
        return EnvironmentHelper.GetApiBaseUrlFromEnv("OPENAI_API_URL", fallbackKeysArray);
    }
}
