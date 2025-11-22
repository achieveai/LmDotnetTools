using System.Reflection;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmTestUtils;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Middleware;

public class MessageUpdateJoinerMiddlewareTests
{
    /// <summary>
    /// Gets the path to test files
    /// </summary>
    private static string GetTestFilesPath()
    {
        // Start from the assembly location
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        var currentDir = Path.GetDirectoryName(assemblyLocation);
        if (currentDir == null)
        {
            throw new InvalidOperationException("Could not determine current directory");
        }

        // Go up the directory tree to find the repository root
        while (currentDir != null && !Directory.Exists(Path.Combine(currentDir, ".git")))
        {
            currentDir = Directory.GetParent(currentDir)?.FullName;
        }

        if (currentDir == null)
        {
            throw new InvalidOperationException("Could not find repository root");
        }

        // The test files are in tests/AnthropicProvider.Tests/TestFiles
        return Path.Combine(currentDir, "tests", "AnthropicProvider.Tests", "TestFiles");
    }

    [Fact]
    public async Task StreamingResponseShouldJoinToExpectedMessages()
    {
        // Arrange
        // Get paths to test files
        var testFilesPath = GetTestFilesPath();
        Console.WriteLine($"Test files path: {testFilesPath}");

        var streamingResponsePath = Path.Combine(testFilesPath, "example_streaming_response2.txt");
        var expectedOutputPath = Path.Combine(testFilesPath, "streaming_responses2_lmcore.json");

        // Verify files exist
        if (!File.Exists(streamingResponsePath))
        {
            throw new FileNotFoundException($"Streaming response file not found: {streamingResponsePath}");
        }

        if (!File.Exists(expectedOutputPath))
        {
            throw new FileNotFoundException($"Expected output file not found: {expectedOutputPath}");
        }

        // Create the mock HTTP handler that reads from the file using MockHttpHandlerBuilder
        var handler = MockHttpHandlerBuilder.Create().RespondWithStreamingFile(streamingResponsePath).Build();

        var httpClient = new HttpClient(handler);
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);

        // Create the Anthropic agent
        var agent = new AnthropicAgent("TestAgent", anthropicClient);

        // Create the middleware
        var middleware = new MessageUpdateJoinerMiddleware();

        // Set up middleware context with a dummy message (required for validation, but content doesn't matter for this test)
        var dummyMessage = new TextMessage { Text = "Test message", Role = Role.User };
        var context = new MiddlewareContext(new[] { dummyMessage }, new GenerateReplyOptions());

        // Read the expected output from the JSON file and directly parse the JSON array
        var expectedJson = await File.ReadAllTextAsync(expectedOutputPath);

        // For simplicity, let's manually parse the expected messages
        var expectedMessages = ParseMessagesFromJson(expectedJson);
        Assert.NotNull(expectedMessages);

        // Act
        // Get the streaming response through the middleware
        var streamingResult = await middleware.InvokeStreamingAsync(context, agent);
        var actualMessages = new List<IMessage>();

        await foreach (var message in streamingResult)
        {
            actualMessages.Add(message);
        }

        // Assert
        Assert.NotEmpty(actualMessages);
        Assert.Equal(expectedMessages.Count + 1, actualMessages.Count); // +1 for the additional UsageMessage

        // Make sure one of the messages is a UsageMessage
        Assert.Contains(actualMessages, m => m is UsageMessage);

        // Remove the UsageMessage for comparison with expected
        var actualMessagesWithoutUsage = actualMessages.Where(m => !(m is UsageMessage)).ToList();

        // Verify the content of the messages
        for (var i = 0; i < expectedMessages.Count; i++)
        {
            var expected = expectedMessages[i];
            var actual = actualMessagesWithoutUsage[i];

            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(expected.Role, actual.Role);

            if (expected is TextMessage expectedText && actual is TextMessage actualText)
            {
                Assert.Equal(expectedText.Text, actualText.Text);
            }
            else if (expected is ToolsCallMessage expectedTool && actual is ToolsCallMessage actualTool)
            {
                Assert.Equal(expectedTool.ToolCalls.Count, actualTool.ToolCalls.Count);
                Assert.Equal(expectedTool.ToolCalls[0].FunctionName, actualTool.ToolCalls[0].FunctionName);

                // Handle possible null FunctionArgs
                if (expectedTool.ToolCalls[0].FunctionArgs != null && actualTool.ToolCalls[0].FunctionArgs != null)
                {
                    // Use null-forgiving operator at assignment to indicate we've verified non-null
                    var expectedArgs = expectedTool.ToolCalls[0].FunctionArgs!;
                    var actualArgs = actualTool.ToolCalls[0].FunctionArgs!;
                    Assert.Contains(expectedArgs, actualArgs);
                }
            }
        }
    }

    /// <summary>
    /// Parses messages from JSON without using a converter
    /// </summary>
    private static List<IMessage> ParseMessagesFromJson(string json)
    {
        var result = new List<IMessage>();
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Parse the JSON array
        using var document = JsonDocument.Parse(json);
        var rootArray = document.RootElement;

        // Process each message in the array
        foreach (var element in rootArray.EnumerateArray())
        {
            // Check if it's a TextMessage or ToolsCallMessage based on properties
            if (element.TryGetProperty("text", out var textElement))
            {
                // It's a TextMessage
                var text = textElement.GetString() ?? "";
                var role = GetRoleFromElement(element);
                var fromAgent = GetStringProperty(element, "from_agent");

                result.Add(
                    new TextMessage
                    {
                        Text = text,
                        Role = role,
                        FromAgent = fromAgent,
                    }
                );
            }
            else if (
                element.TryGetProperty("tool_calls", out var _)
                || (element.TryGetProperty("source", out var sourceElement) && sourceElement.GetString() == "tool-call")
            )
            {
                // It's a ToolsCallMessage
                var role = GetRoleFromElement(element);
                var fromAgent = GetStringProperty(element, "from_agent");
                var generationId = GetStringProperty(element, "generation_id");

                // Extract tool calls
                var toolCalls = new List<ToolCall>();

                if (element.TryGetProperty("tool_calls", out var toolCallsElement))
                {
                    foreach (var toolCallElement in toolCallsElement.EnumerateArray())
                    {
                        var functionName = GetStringProperty(toolCallElement, "function_name");
                        var functionArgs = GetStringProperty(toolCallElement, "function_args");
                        var toolCallId = GetStringProperty(toolCallElement, "tool_call_id");

                        toolCalls.Add(new ToolCall(functionName, functionArgs) { ToolCallId = toolCallId });
                    }
                }

                result.Add(
                    new ToolsCallMessage
                    {
                        Role = role,
                        FromAgent = fromAgent,
                        GenerationId = generationId,
                        ToolCalls = [.. toolCalls],
                    }
                );
            }
        }

        return result;
    }

    private static Role GetRoleFromElement(JsonElement element)
    {
        if (element.TryGetProperty("role", out var roleElement))
        {
            var roleString = roleElement.GetString();
            return roleString?.ToLowerInvariant() switch
            {
                "assistant" => Role.Assistant,
                "user" => Role.User,
                "system" => Role.System,
                "tool" => Role.Tool,
                _ => Role.None,
            };
        }

        return Role.None;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property))
        {
            return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        }

        return null;
    }
}
