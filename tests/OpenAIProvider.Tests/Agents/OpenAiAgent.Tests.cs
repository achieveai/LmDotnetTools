using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

public class OpenAiAgentTests
{
    private const string BaseUrl = "http://test-mode/v1";

    [Fact]
    public async Task SimpleConversation_ShouldReturnResponse()
    {
        var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);
        var client = new OpenClient(httpClient, BaseUrl);
        var agent = new OpenClientAgent("TestAgent", client);

        var systemMessage = new TextMessage { Role = Role.System, Text = "You're a helpful AI Agent" };
        var userMessage = new TextMessage
        {
            Role = Role.User,
            Text =
                "Hello Bot\n<|instruction_start|>{\"instruction_chain\":[{\"id_message\":\"simple\",\"messages\":[{\"text_message\":{\"length\":18}}]}]}<|instruction_end|>",
        };

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
            new TextMessage
            {
                Role = Role.User,
                Text = "{\"response\":\"hello\"}",
            },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "gpt-4o-mini",
            Temperature = 0.7f,
            MaxToken = 1000,
            ResponseFormat = ResponseFormat.JSON,
        };

        var requestCapture = new RequestCapture<ChatCompletionRequest, ChatCompletionResponse>();
        var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(capture: requestCapture, chunkDelayMs: 0);
        var client = new OpenClient(httpClient, BaseUrl);
        var agent = new OpenClientAgent("TestAgent", client);

        // Act
        var request = ChatCompletionRequest.FromMessages(messages, options);
        var response = await agent.GenerateReplyAsync(messages, options);
        var responseText = ((ICanGetText)response.First())!.GetText()!;
        var parsed = JsonNode.Parse(responseText);
        var capturedRequest = requestCapture.GetRequest();
        Assert.NotNull(request);
        Assert.Equal("gpt-4o-mini", request.Model);
        Assert.Equal(ResponseFormat.JSON, options.ResponseFormat);
        Assert.NotNull(capturedRequest?.ResponseFormat);
        Assert.Equal("json_object", capturedRequest.ResponseFormat.ResponseFormatType);
        Assert.NotNull(parsed?["response"]);
    }

    [Fact]
    public async Task FunctionToolCall_ShouldReturnToolMessage()
    {
        // Arrange
        var messages = new[]
        {
            new TextMessage { Role = Role.System, Text = "You're a helpful AI Agent that can use tools" },
            new TextMessage
            {
                Role = Role.User,
                Text =
                    "What's the weather in San Francisco?\n<|instruction_start|>{\"instruction_chain\":[{\"id_message\":\"tool\",\"messages\":[{\"tool_call\":[{\"name\":\"getWeather\",\"args\":{\"location\":\"San Francisco\"}}]}]}]}<|instruction_end|>",
            },
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

        var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);
        var client = new OpenClient(httpClient, BaseUrl);
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
            new TextMessage
            {
                Role = Role.User,
                Text =
                    "What's the weather in San Francisco?\n<|instruction_start|>{\"instruction_chain\":[{\"id_message\":\"tool-stream\",\"messages\":[{\"tool_call\":[{\"name\":\"getWeather\",\"args\":{\"location\":\"San Francisco\"}}]}]}]}<|instruction_end|>",
            },
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

        var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);
        var client = new OpenClient(httpClient, BaseUrl);
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
            new TextMessage
            {
                Role = Role.User,
                Text =
                    "What's the weather in San Francisco?\n<|instruction_start|>{\"instruction_chain\":[{\"id_message\":\"tool-join\",\"messages\":[{\"tool_call\":[{\"name\":\"getWeather\",\"args\":{\"location\":\"San Francisco\"}}]}]}]}<|instruction_end|>",
            },
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

        var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);
        var client = new OpenClient(httpClient, BaseUrl);
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

    [Fact]
    public async Task GenerateReplyAsync_WithRequestResponseDump_WritesRequestAndResponseFiles()
    {
        // Uses TestSseMessageHandler via TestModeHttpClientFactory.
        var baseFileName = Path.Combine(Path.GetTempPath(), $"openai-dump-{Guid.NewGuid():N}");
        var requestPath = $"{baseFileName}.request.txt";
        var responsePath = $"{baseFileName}.response.txt";

        try
        {
            var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);
            var client = new OpenClient(httpClient, BaseUrl);
            var agent = new OpenClientAgent("TestAgent", client);

            var messages = new[]
            {
                new TextMessage { Role = Role.User, Text = "Hello there" },
            };

            var options = new GenerateReplyOptions
            {
                ModelId = "gpt-4o-mini",
                RequestResponseDumpFileName = baseFileName,
            };

            _ = await agent.GenerateReplyAsync(messages, options);

            Assert.True(File.Exists(requestPath));
            Assert.True(File.Exists(responsePath));
            Assert.Contains("\"model\"", await File.ReadAllTextAsync(requestPath));
            Assert.Contains("\"choices\"", await File.ReadAllTextAsync(responsePath));
        }
        finally
        {
            CleanupDumpFiles(baseFileName);
        }
    }

    [Fact]
    public async Task GenerateReplyStreamingAsync_WithRequestResponseDump_AppendsStreamingChunks()
    {
        // Uses TestSseMessageHandler via TestModeHttpClientFactory.
        var baseFileName = Path.Combine(Path.GetTempPath(), $"openai-stream-dump-{Guid.NewGuid():N}");
        var requestPath = $"{baseFileName}.request.txt";
        var responsePath = $"{baseFileName}.response.txt";

        try
        {
            var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0, wordsPerChunk: 2);
            var client = new OpenClient(httpClient, BaseUrl);
            var agent = new OpenClientAgent("TestAgent", client);

            var messages = new[]
            {
                new TextMessage
                {
                    Role = Role.User,
                    Text =
                        "Stream test\n<|instruction_start|>{\"instruction_chain\":[{\"id_message\":\"stream-dump\",\"messages\":[{\"text_message\":{\"length\":80}}]}]}<|instruction_end|>",
                },
            };

            var options = new GenerateReplyOptions
            {
                ModelId = "gpt-4o-mini",
                RequestResponseDumpFileName = baseFileName,
            };

            var stream = await agent.GenerateReplyStreamingAsync(messages, options);
            await foreach (var _ in stream) { }

            Assert.True(File.Exists(requestPath));
            Assert.True(File.Exists(responsePath));
            var lines = (await File.ReadAllLinesAsync(responsePath)).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
            Assert.True(lines.Count > 1);
        }
        finally
        {
            CleanupDumpFiles(baseFileName);
        }
    }

    [Fact]
    public async Task GenerateReplyAsync_WithRequestResponseDump_RotatesExistingFiles()
    {
        // Uses TestSseMessageHandler via TestModeHttpClientFactory.
        var baseFileName = Path.Combine(Path.GetTempPath(), $"openai-rotate-dump-{Guid.NewGuid():N}");
        var requestPath = $"{baseFileName}.request.txt";
        var responsePath = $"{baseFileName}.response.txt";
        var rotatedRequestPath = $"{baseFileName}.1.request.txt";
        var rotatedResponsePath = $"{baseFileName}.1.response.txt";

        try
        {
            await File.WriteAllTextAsync(requestPath, "old-request");
            await File.WriteAllTextAsync(responsePath, "old-response");

            var httpClient = TestModeHttpClientFactory.CreateOpenAiTestClient(chunkDelayMs: 0);
            var client = new OpenClient(httpClient, BaseUrl);
            var agent = new OpenClientAgent("TestAgent", client);

            var options = new GenerateReplyOptions
            {
                ModelId = "gpt-4o-mini",
                RequestResponseDumpFileName = baseFileName,
            };

            _ = await agent.GenerateReplyAsync([new TextMessage { Role = Role.User, Text = "rotation test" }], options);

            Assert.True(File.Exists(rotatedRequestPath));
            Assert.True(File.Exists(rotatedResponsePath));
            Assert.Equal("old-request", await File.ReadAllTextAsync(rotatedRequestPath));
            Assert.Equal("old-response", await File.ReadAllTextAsync(rotatedResponsePath));
            Assert.Contains("\"model\"", await File.ReadAllTextAsync(requestPath));
            Assert.Contains("\"choices\"", await File.ReadAllTextAsync(responsePath));
        }
        finally
        {
            CleanupDumpFiles(baseFileName);
        }
    }

    private static void CleanupDumpFiles(string baseFileName)
    {
        var directory = Path.GetDirectoryName(baseFileName);
        var fileName = Path.GetFileName(baseFileName);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(directory, $"{fileName}*"))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

}
