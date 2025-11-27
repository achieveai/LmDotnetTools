using System.Collections.Immutable;
using System.Diagnostics;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class JsonFragmentUpdateMiddlewareTests
{
    private readonly ITestOutputHelper _output;

    public JsonFragmentUpdateMiddlewareTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    ///     Test data for basic middleware functionality testing
    /// </summary>
    public static IEnumerable<object[]> BasicMiddlewareTestCases =>
        [
            // Tool call ID, function name, function args
            ["call-1", "test_function", "{\"message\":\"Hello\"}"],
            ["call-2", "math_function", "{\"x\":10,\"y\":20}"],
            ["call-3", "simple_function", "{}"],
            ["call-4", "array_function", "[1,2,3]"],
        ];

    /// <summary>
    ///     Test data for streaming functionality testing
    /// </summary>
    public static IEnumerable<object[]> StreamingTestCases =>
        [
            // Fragment 1, Fragment 2, Fragment 3
            ["{\"name\":\"Jo", "hn\",\"age\":", "25}"],
            ["{\"items\":[1", ",2,3],\"total\":", "6}"],
            ["{\"status\":\"", "processing\",\"progress\":", "50}"],
        ];

    [Fact]
    public void ProcessAsync_WithNonToolsCallUpdateMessage_PassesThroughUnchanged()
    {
        // Arrange
        var middleware = new JsonFragmentUpdateMiddleware();
        var textMessage = new TextMessage { Text = "Hello, World!", Role = Role.User };
        var messages = new List<IMessage> { textMessage };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        // Assert
        _ = Assert.Single(result);
        Assert.Same(textMessage, result[0]);

        Debug.WriteLine("✓ Non-ToolsCallUpdateMessage passed through unchanged");
    }

    [Theory]
    [MemberData(nameof(BasicMiddlewareTestCases))]
    public void ProcessAsync_WithBasicToolsCallUpdateMessage_AddsJsonFragmentUpdates(
        string toolCallId,
        string functionName,
        string functionArgs
    )
    {
        // Arrange
        Debug.WriteLine($"Testing: {functionName} with args: {functionArgs}");

        var middleware = new JsonFragmentUpdateMiddleware();
        var toolCallUpdate = new ToolCallUpdate
        {
            ToolCallId = toolCallId,
            FunctionName = functionName,
            FunctionArgs = functionArgs,
        };

        var toolsCallUpdateMessage = new ToolsCallUpdateMessage
        {
            Role = Role.Assistant,
            ToolCallUpdates = [toolCallUpdate],
        };

        var messages = new List<IMessage> { toolsCallUpdateMessage };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        Debug.WriteLine($"Result count: {result.Count}");

        // Assert
        _ = Assert.Single(result);
        var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(result[0]);
        _ = Assert.Single(processedMessage.ToolCallUpdates);

        var processedUpdate = processedMessage.ToolCallUpdates[0];
        Assert.Equal(toolCallId, processedUpdate.ToolCallId);
        Assert.Equal(functionName, processedUpdate.FunctionName);
        Assert.Equal(functionArgs, processedUpdate.FunctionArgs);

        // Verify JsonFragmentUpdates were added for non-empty JSON
        if (!string.IsNullOrEmpty(functionArgs) && functionArgs != "{}")
        {
            Assert.NotNull(processedUpdate.JsonFragmentUpdates);
            Assert.NotEmpty(processedUpdate.JsonFragmentUpdates);

            Debug.WriteLine($"JsonFragmentUpdates count: {processedUpdate.JsonFragmentUpdates.Count}");
            foreach (var update in processedUpdate.JsonFragmentUpdates)
            {
                Debug.WriteLine($"  {update.Kind}: Path='{update.Path}' Value='{update.TextValue}'");
            }
        }
    }

    [Theory]
    [MemberData(nameof(StreamingTestCases))]
    public void ProcessAsync_WithStreamingToolCallUpdates_GroupsFragmentsProperly(
        string fragment1,
        string fragment2,
        string fragment3
    )
    {
        // Arrange
        Debug.WriteLine($"Testing streaming: '{fragment1}' + '{fragment2}' + '{fragment3}'");

        var middleware = new JsonFragmentUpdateMiddleware();

        // Create three separate tool call updates simulating streaming
        var update1 = new ToolCallUpdate
        {
            ToolCallId = "stream-call-1",
            FunctionName = "streaming_function",
            FunctionArgs = fragment1,
        };

        var update2 = new ToolCallUpdate
        {
            ToolCallId = "stream-call-1", // Same ID
            FunctionName = "streaming_function",
            FunctionArgs = fragment2,
        };

        var update3 = new ToolCallUpdate
        {
            ToolCallId = "stream-call-1", // Same ID
            FunctionName = "streaming_function",
            FunctionArgs = fragment3,
        };

        var messages = new List<IMessage>
        {
            new ToolsCallUpdateMessage { Role = Role.Assistant, ToolCallUpdates = [update1] },
            new ToolsCallUpdateMessage { Role = Role.Assistant, ToolCallUpdates = [update2] },
            new ToolsCallUpdateMessage { Role = Role.Assistant, ToolCallUpdates = [update3] },
        };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        // Assert
        Assert.Equal(3, result.Count);

        // Each message should be processed and have JsonFragmentUpdates
        foreach (var message in result)
        {
            var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(message);
            _ = Assert.Single(processedMessage.ToolCallUpdates);

            var processedUpdate = processedMessage.ToolCallUpdates[0];
            Assert.NotNull(processedUpdate.JsonFragmentUpdates);

            Debug.WriteLine($"Message has {processedUpdate.JsonFragmentUpdates.Count} fragment updates");
            foreach (var update in processedUpdate.JsonFragmentUpdates)
            {
                Debug.WriteLine($"  {update.Kind}: Path='{update.Path}' Value='{update.TextValue}'");
            }
        }

        Debug.WriteLine("✓ All streaming messages processed successfully");
    }

    [Fact]
    public void ProcessAsync_WithEmptyFunctionArgs_ReturnsOriginalUpdate()
    {
        // Arrange
        Debug.WriteLine("Testing empty function args");

        var middleware = new JsonFragmentUpdateMiddleware();
        var toolCallUpdate = new ToolCallUpdate
        {
            ToolCallId = "empty-call",
            FunctionName = "empty_function",
            FunctionArgs = "", // Empty
        };

        var toolsCallUpdateMessage = new ToolsCallUpdateMessage
        {
            Role = Role.Assistant,
            ToolCallUpdates = [toolCallUpdate],
        };

        var messages = new List<IMessage> { toolsCallUpdateMessage };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        // Assert
        _ = Assert.Single(result);
        var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(result[0]);
        var processedUpdate = processedMessage.ToolCallUpdates[0];

        // Original update should be returned unchanged (no JsonFragmentUpdates)
        Assert.Equal("empty-call", processedUpdate.ToolCallId);
        Assert.Equal("empty_function", processedUpdate.FunctionName);
        Assert.Equal("", processedUpdate.FunctionArgs);
        Assert.Null(processedUpdate.JsonFragmentUpdates);

        Debug.WriteLine("✓ Empty function args handled correctly");
    }

    [Fact]
    public void ProcessAsync_WithNullFunctionArgs_ReturnsOriginalUpdate()
    {
        // Arrange
        Debug.WriteLine("Testing null function args");

        var middleware = new JsonFragmentUpdateMiddleware();
        var toolCallUpdate = new ToolCallUpdate
        {
            ToolCallId = "null-call",
            FunctionName = "null_function",
            FunctionArgs = null, // Null
        };

        var toolsCallUpdateMessage = new ToolsCallUpdateMessage
        {
            Role = Role.Assistant,
            ToolCallUpdates = [toolCallUpdate],
        };

        var messages = new List<IMessage> { toolsCallUpdateMessage };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        // Assert
        _ = Assert.Single(result);
        var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(result[0]);
        var processedUpdate = processedMessage.ToolCallUpdates[0];

        // Original update should be returned unchanged (no JsonFragmentUpdates)
        Assert.Equal("null-call", processedUpdate.ToolCallId);
        Assert.Equal("null_function", processedUpdate.FunctionName);
        Assert.Null(processedUpdate.FunctionArgs);
        Assert.Null(processedUpdate.JsonFragmentUpdates);

        Debug.WriteLine("✓ Null function args handled correctly");
    }

    [Fact]
    public void ProcessAsync_WithMultipleToolCallUpdatesInSameMessage_ProcessesEachSeparately()
    {
        // Arrange
        Debug.WriteLine("Testing multiple tool call updates in same message");

        var middleware = new JsonFragmentUpdateMiddleware();
        var toolCallUpdates = ImmutableList.Create(
            new ToolCallUpdate
            {
                ToolCallId = "call-1",
                FunctionName = "function1",
                FunctionArgs = "{\"param1\":\"value1\"}",
            },
            new ToolCallUpdate
            {
                ToolCallId = "call-2",
                FunctionName = "function2",
                FunctionArgs = "{\"param2\":\"value2\"}",
            }
        );

        var toolsCallUpdateMessage = new ToolsCallUpdateMessage
        {
            Role = Role.Assistant,
            ToolCallUpdates = toolCallUpdates,
        };

        var messages = new List<IMessage> { toolsCallUpdateMessage };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        // Assert
        _ = Assert.Single(result);
        var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(result[0]);
        Assert.Equal(2, processedMessage.ToolCallUpdates.Count);

        foreach (var processedUpdate in processedMessage.ToolCallUpdates)
        {
            Assert.NotNull(processedUpdate.JsonFragmentUpdates);
            Assert.NotEmpty(processedUpdate.JsonFragmentUpdates);

            Debug.WriteLine(
                $"Tool call {processedUpdate.ToolCallId} has {processedUpdate.JsonFragmentUpdates.Count} fragment updates"
            );
        }

        Debug.WriteLine("✓ Multiple tool call updates processed correctly");
    }

    [Fact]
    public void ClearGenerators_RemovesAllGeneratorState()
    {
        // Arrange
        Debug.WriteLine("Testing generator clearing");

        var middleware = new JsonFragmentUpdateMiddleware();

        // Process some messages to create generators
        var toolCallUpdate = new ToolCallUpdate
        {
            ToolCallId = "test-call",
            FunctionName = "test_function",
            FunctionArgs = "{\"test\":\"value\"}",
        };

        var toolsCallUpdateMessage = new ToolsCallUpdateMessage
        {
            Role = Role.Assistant,
            ToolCallUpdates = [toolCallUpdate],
        };

        var messages = new List<IMessage> { toolsCallUpdateMessage };

        // Process once to create generators
        _ = ProcessMessagesSync(middleware, messages);

        // Act
        middleware.ClearGenerators();

        // Assert
        // Processing the same message again should work (no state corruption)
        var result = ProcessMessagesSync(middleware, messages);
        _ = Assert.Single(result);

        var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(result[0]);
        var processedUpdate = processedMessage.ToolCallUpdates[0];
        Assert.NotNull(processedUpdate.JsonFragmentUpdates);

        Debug.WriteLine("✓ Generator clearing works correctly");
    }

    [Fact]
    public void ProcessAsync_WithCompleteJsonToolCall_IncludesJsonCompleteEvent()
    {
        Debug.WriteLine("Testing ProcessAsync with complete JSON tool call includes JsonComplete event");

        var middleware = new JsonFragmentUpdateMiddleware();

        // Create a complete JSON tool call update
        var toolCallUpdate = new ToolCallUpdate
        {
            FunctionName = "test_function",
            FunctionArgs = "{\"message\": \"Hello World\"}",
            Index = 0,
            ToolCallId = "call_123",
        };

        var message = new ToolsCallUpdateMessage { Role = Role.Assistant, ToolCallUpdates = [toolCallUpdate] };

        var messages = new[] { message };
        var result = ProcessMessagesSync(middleware, messages);

        _ = Assert.Single(result);
        var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(result[0]);
        var processedToolCall = processedMessage.ToolCallUpdates[0];

        // Verify that JsonFragmentUpdates were added
        Assert.NotNull(processedToolCall.JsonFragmentUpdates);
        Assert.NotEmpty(processedToolCall.JsonFragmentUpdates);

        // Verify that a JsonComplete event was included
        var jsonCompleteUpdates = processedToolCall
            .JsonFragmentUpdates.Where(u => u.Kind == JsonFragmentKind.JsonComplete)
            .ToList();

        _ = Assert.Single(jsonCompleteUpdates);

        var completeEvent = jsonCompleteUpdates.First();
        Assert.Equal("root", completeEvent.Path);
        Assert.Equal("{\"message\": \"Hello World\"}", completeEvent.TextValue);

        Debug.WriteLine($"✓ JsonComplete event found: {completeEvent.TextValue}");
        Debug.WriteLine($"✓ Total fragment updates: {processedToolCall.JsonFragmentUpdates.Count}");
    }

    /// <summary>
    ///     Helper method to process messages synchronously for testing
    /// </summary>
    private static List<IMessage> ProcessMessagesSync(
        JsonFragmentUpdateMiddleware middleware,
        IEnumerable<IMessage> messages
    )
    {
        var result = new List<IMessage>();

        // Convert to async enumerable and process
        var asyncMessages = ToAsyncEnumerable(messages);
        var processedMessages = middleware.ProcessAsync(asyncMessages);

        // Convert back to list synchronously using a task
        var task = Task.Run(async () =>
        {
            var list = new List<IMessage>();
            await foreach (var message in processedMessages)
            {
                list.Add(message);
            }

            return list;
        });

        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    ///     Helper method to convert IEnumerable to IAsyncEnumerable for testing
    /// </summary>
    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
            await Task.Yield(); // Allow for async behavior
        }
    }

    #region ToolCallUpdateMessage Tests (Singular)

    [Theory]
    [MemberData(nameof(BasicMiddlewareTestCases))]
    public void ProcessAsync_WithBasicToolCallUpdateMessage_AddsJsonFragmentUpdates(
        string toolCallId,
        string functionName,
        string functionArgs
    )
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine($"Testing singular: {functionName} with args: {functionArgs}");

        var middleware = new JsonFragmentUpdateMiddleware();
        var toolCallUpdateMessage = new ToolCallUpdateMessage
        {
            ToolCallId = toolCallId,
            FunctionName = functionName,
            FunctionArgs = functionArgs,
            Role = Role.Assistant,
        };

        var messages = new List<IMessage> { toolCallUpdateMessage };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        System.Diagnostics.Debug.WriteLine($"Result count: {result.Count}");

        // Assert
        _ = Assert.Single(result);
        var processedMessage = Assert.IsType<ToolCallUpdateMessage>(result[0]);

        Assert.Equal(toolCallId, processedMessage.ToolCallId);
        Assert.Equal(functionName, processedMessage.FunctionName);
        Assert.Equal(functionArgs, processedMessage.FunctionArgs);

        // Verify JsonFragmentUpdates were added for non-empty JSON
        if (!string.IsNullOrEmpty(functionArgs) && functionArgs != "{}")
        {
            Assert.NotNull(processedMessage.JsonFragmentUpdates);
            Assert.NotEmpty(processedMessage.JsonFragmentUpdates);

            System.Diagnostics.Debug.WriteLine(
                $"JsonFragmentUpdates count: {processedMessage.JsonFragmentUpdates.Count}"
            );
            foreach (var update in processedMessage.JsonFragmentUpdates)
            {
                System.Diagnostics.Debug.WriteLine($"  {update.Kind}: Path='{update.Path}' Value='{update.TextValue}'");
            }
        }
    }

    [Theory]
    [MemberData(nameof(StreamingTestCases))]
    public void ProcessAsync_WithStreamingToolCallUpdateMessages_GroupsFragmentsProperly(
        string fragment1,
        string fragment2,
        string fragment3
    )
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine($"Testing singular streaming: '{fragment1}' + '{fragment2}' + '{fragment3}'");

        var middleware = new JsonFragmentUpdateMiddleware();

        // Create three separate ToolCallUpdateMessage instances simulating streaming
        var messages = new List<IMessage>
        {
            new ToolCallUpdateMessage
            {
                ToolCallId = "stream-call-1",
                FunctionName = "streaming_function",
                FunctionArgs = fragment1,
                Role = Role.Assistant,
            },
            new ToolCallUpdateMessage
            {
                ToolCallId = "stream-call-1", // Same ID
                FunctionName = "streaming_function",
                FunctionArgs = fragment2,
                Role = Role.Assistant,
            },
            new ToolCallUpdateMessage
            {
                ToolCallId = "stream-call-1", // Same ID
                FunctionName = "streaming_function",
                FunctionArgs = fragment3,
                Role = Role.Assistant,
            },
        };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        // Assert
        Assert.Equal(3, result.Count);

        // Each message should be processed and have JsonFragmentUpdates
        foreach (var message in result)
        {
            var processedMessage = Assert.IsType<ToolCallUpdateMessage>(message);
            Assert.NotNull(processedMessage.JsonFragmentUpdates);

            System.Diagnostics.Debug.WriteLine(
                $"Message has {processedMessage.JsonFragmentUpdates.Count} fragment updates"
            );
            foreach (var update in processedMessage.JsonFragmentUpdates)
            {
                System.Diagnostics.Debug.WriteLine($"  {update.Kind}: Path='{update.Path}' Value='{update.TextValue}'");
            }
        }

        System.Diagnostics.Debug.WriteLine("✓ All singular streaming messages processed successfully");
    }

    [Fact]
    public void ProcessAsync_WithEmptyFunctionArgs_ToolCallUpdateMessage_ReturnsOriginalUpdate()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing singular empty function args");

        var middleware = new JsonFragmentUpdateMiddleware();
        var toolCallUpdateMessage = new ToolCallUpdateMessage
        {
            ToolCallId = "empty-call",
            FunctionName = "empty_function",
            FunctionArgs = "", // Empty
            Role = Role.Assistant,
        };

        var messages = new List<IMessage> { toolCallUpdateMessage };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        // Assert
        _ = Assert.Single(result);
        var processedMessage = Assert.IsType<ToolCallUpdateMessage>(result[0]);

        // Original message should be returned unchanged (no JsonFragmentUpdates)
        Assert.Equal("empty-call", processedMessage.ToolCallId);
        Assert.Equal("empty_function", processedMessage.FunctionName);
        Assert.Equal("", processedMessage.FunctionArgs);
        Assert.Null(processedMessage.JsonFragmentUpdates);

        System.Diagnostics.Debug.WriteLine("✓ Singular empty function args handled correctly");
    }

    [Fact]
    public void ProcessAsync_WithNullFunctionArgs_ToolCallUpdateMessage_ReturnsOriginalUpdate()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing singular null function args");

        var middleware = new JsonFragmentUpdateMiddleware();
        var toolCallUpdateMessage = new ToolCallUpdateMessage
        {
            ToolCallId = "null-call",
            FunctionName = "null_function",
            FunctionArgs = null, // Null
            Role = Role.Assistant,
        };

        var messages = new List<IMessage> { toolCallUpdateMessage };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        // Assert
        _ = Assert.Single(result);
        var processedMessage = Assert.IsType<ToolCallUpdateMessage>(result[0]);

        // Original message should be returned unchanged (no JsonFragmentUpdates)
        Assert.Equal("null-call", processedMessage.ToolCallId);
        Assert.Equal("null_function", processedMessage.FunctionName);
        Assert.Null(processedMessage.FunctionArgs);
        Assert.Null(processedMessage.JsonFragmentUpdates);

        System.Diagnostics.Debug.WriteLine("✓ Singular null function args handled correctly");
    }

    [Fact]
    public void ProcessAsync_WithCompleteJsonToolCallUpdateMessage_IncludesJsonCompleteEvent()
    {
        System.Diagnostics.Debug.WriteLine(
            "Testing singular ProcessAsync with complete JSON includes JsonComplete event"
        );

        var middleware = new JsonFragmentUpdateMiddleware();

        // Create a complete JSON ToolCallUpdateMessage
        var message = new ToolCallUpdateMessage
        {
            FunctionName = "test_function",
            FunctionArgs = "{\"message\": \"Hello World\"}",
            Index = 0,
            ToolCallId = "call_123",
            Role = Role.Assistant,
        };

        var messages = new[] { message };
        var result = ProcessMessagesSync(middleware, messages);

        _ = Assert.Single(result);
        var processedMessage = Assert.IsType<ToolCallUpdateMessage>(result[0]);

        // Verify that JsonFragmentUpdates were added
        Assert.NotNull(processedMessage.JsonFragmentUpdates);
        Assert.NotEmpty(processedMessage.JsonFragmentUpdates);

        // Verify that a JsonComplete event was included
        var jsonCompleteUpdates = processedMessage
            .JsonFragmentUpdates.Where(u => u.Kind == JsonFragmentKind.JsonComplete)
            .ToList();

        _ = Assert.Single(jsonCompleteUpdates);

        var completeEvent = jsonCompleteUpdates.First();
        Assert.Equal("root", completeEvent.Path);
        Assert.Equal("{\"message\": \"Hello World\"}", completeEvent.TextValue);

        System.Diagnostics.Debug.WriteLine($"✓ Singular JsonComplete event found: {completeEvent.TextValue}");
        System.Diagnostics.Debug.WriteLine($"✓ Total fragment updates: {processedMessage.JsonFragmentUpdates.Count}");
    }

    [Fact]
    public void ProcessAsync_WithMixedToolCallAndToolsCallUpdateMessages_ProcessesBothCorrectly()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing mixed singular and plural messages");

        var middleware = new JsonFragmentUpdateMiddleware();

        var messages = new List<IMessage>
        {
            // Singular ToolCallUpdateMessage
            new ToolCallUpdateMessage
            {
                ToolCallId = "single-call",
                FunctionName = "single_function",
                FunctionArgs = "{\"type\":\"single\"}",
                Role = Role.Assistant,
            },
            // Plural ToolsCallUpdateMessage
            new ToolsCallUpdateMessage
            {
                Role = Role.Assistant,
                ToolCallUpdates = [
                    new ToolCallUpdate
                    {
                        ToolCallId = "plural-call",
                        FunctionName = "plural_function",
                        FunctionArgs = "{\"type\":\"plural\"}",
                    }
                ],
            },
        };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        // Assert
        Assert.Equal(2, result.Count);

        // First should be ToolCallUpdateMessage
        var singularMessage = Assert.IsType<ToolCallUpdateMessage>(result[0]);
        Assert.Equal("single-call", singularMessage.ToolCallId);
        Assert.NotNull(singularMessage.JsonFragmentUpdates);
        Assert.NotEmpty(singularMessage.JsonFragmentUpdates);

        // Second should be ToolsCallUpdateMessage
        var pluralMessage = Assert.IsType<ToolsCallUpdateMessage>(result[1]);
        _ = Assert.Single(pluralMessage.ToolCallUpdates);
        var pluralUpdate = pluralMessage.ToolCallUpdates[0];
        Assert.Equal("plural-call", pluralUpdate.ToolCallId);
        Assert.NotNull(pluralUpdate.JsonFragmentUpdates);
        Assert.NotEmpty(pluralUpdate.JsonFragmentUpdates);

        System.Diagnostics.Debug.WriteLine("✓ Mixed message types processed correctly");
    }

    #endregion
}
