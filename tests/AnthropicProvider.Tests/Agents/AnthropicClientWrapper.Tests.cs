using System.Reflection;
using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
// Note: Using MockHttpHandlerBuilder for modern HTTP-level testing
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.TestUtils;
using Xunit;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

public class MockHttpHandlerBuilderRecordPlaybackTests
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
    public async Task MockHttpHandlerBuilder_RecordPlayback_NonStreaming_WorksCorrectly()
    {
        // Arrange - Using MockHttpHandlerBuilder with record/playback functionality
        var testName = "RecordPlaybackNonStreaming";
        var testDataPath = Path.Combine(
            TestUtils.TestUtils.FindWorkspaceRoot(AppDomain.CurrentDomain.BaseDirectory),
            "tests",
            "TestData",
            "Anthropic",
            $"{testName}.json"
        );

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataPath, allowAdditional: true)
            .RespondWithAnthropicMessage("Hello from record/playback test!", "claude-3-sonnet-20240229", 10, 20)
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient("test-api-key", httpClient: httpClient);

        try
        {
            var request = new AnthropicRequest
            {
                Model = "claude-3-sonnet-20240229",
                Messages = new List<AnthropicMessage>
                {
                    new AnthropicMessage
                    {
                        Role = "user",
                        Content = new List<AnthropicContent>
                        {
                            new AnthropicContent { Type = "text", Text = "Hello, world!" },
                        },
                    },
                },
            };

            // Act
            var response = await client.CreateChatCompletionsAsync(request);

            // Assert
            Assert.NotNull(response);
            Assert.StartsWith("msg_test", response.Id); // MockHttpHandlerBuilder generates msg_test prefix
            Assert.Equal("message", response.Type);
            Assert.Equal("assistant", response.Role);
            Assert.Single(response.Content);
            Assert.Equal("text", response.Content[0].Type);

            var typedContent = Assert.IsType<AnthropicResponseTextContent>(response.Content[0]);
            Assert.Equal("Hello from record/playback test!", typedContent.Text);

            // Verify record/playback functionality works - record interaction occurs automatically
            // when using MockHttpHandlerBuilder with .WithRecordPlayback()
        }
        finally
        {
            // Clean up
            if (File.Exists(testDataPath))
            {
                File.Delete(testDataPath);
            }
        }
    }

    [Fact]
    public async Task MockHttpHandlerBuilder_RecordPlayback_Streaming_WorksCorrectly()
    {
        // Arrange - Using MockHttpHandlerBuilder with streaming file response
        var testFilesPath = GetTestFilesPath();
        var streamingFilePath = Path.Combine(testFilesPath, "example_streaming_response2.txt");

        // Verify the file exists
        if (!File.Exists(streamingFilePath))
        {
            throw new FileNotFoundException($"Streaming response file not found: {streamingFilePath}");
        }

        var testDataPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".json");

        var handler = MockHttpHandlerBuilder
            .Create()
            .WithRecordPlayback(testDataPath, allowAdditional: false)
            .RespondWithStreamingFile(streamingFilePath)
            .Build();

        var httpClient = new HttpClient(handler);
        var client = new AnthropicClient("test-api-key", httpClient: httpClient);

        try
        {
            var request = new AnthropicRequest
            {
                Model = "claude-3-sonnet-20240229",
                Messages = new List<AnthropicMessage>
                {
                    new AnthropicMessage
                    {
                        Role = "user",
                        Content = new List<AnthropicContent>
                        {
                            new AnthropicContent
                            {
                                Type = "text",
                                Text = "Write a Python function to calculate Fibonacci sequence for n=10",
                            },
                        },
                    },
                },
                Stream = true,
            };

            // Act
            var streamEvents = await client.StreamingChatCompletionsAsync(request);
            var events = new List<AnthropicStreamEvent>();

            await foreach (var streamEvent in streamEvents)
            {
                events.Add(streamEvent);
            }

            // Assert
            Assert.NotEmpty(events);

            // Verify message_start event exists
            var messageStartEvent = events.FirstOrDefault(e => e.Type == "message_start");
            Assert.NotNull(messageStartEvent);

            // Verify content_block_delta events exist
            var contentDeltas = events.Where(e => e.Type == "content_block_delta").ToList();
            Assert.NotEmpty(contentDeltas);

            // Verify message_stop event exists (stream completion)
            var messageStopEvent = events.FirstOrDefault(e => e.Type == "message_stop");
            Assert.NotNull(messageStopEvent);

            // MockHttpHandlerBuilder with .WithRecordPlayback() handles streaming record/playback automatically
        }
        finally
        {
            // Clean up
            if (File.Exists(testDataPath))
            {
                File.Delete(testDataPath);
            }
        }
    }
}
