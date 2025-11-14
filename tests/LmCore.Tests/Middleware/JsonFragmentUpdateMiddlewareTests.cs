using System.Collections.Immutable;
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
    /// Test data for basic middleware functionality testing
    /// </summary>
    public static IEnumerable<object[]> BasicMiddlewareTestCases =>
        new List<object[]>
        {
            // Tool call ID, function name, function args
            new object[] { "call-1", "test_function", "{\"message\":\"Hello\"}" },
            new object[] { "call-2", "math_function", "{\"x\":10,\"y\":20}" },
            new object[] { "call-3", "simple_function", "{}" },
            new object[] { "call-4", "array_function", "[1,2,3]" },
        };

    /// <summary>
    /// Test data for streaming functionality testing
    /// </summary>
    public static IEnumerable<object[]> StreamingTestCases =>
        new List<object[]>
        {
            // Fragment 1, Fragment 2, Fragment 3
            new object[] { "{\"name\":\"Jo", "hn\",\"age\":", "25}" },
            new object[] { "{\"items\":[1", ",2,3],\"total\":", "6}" },
            new object[] { "{\"status\":\"", "processing\",\"progress\":", "50}" },
        };

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
        Assert.Single(result);
        Assert.Same(textMessage, result[0]);

        System.Diagnostics.Debug.WriteLine("✓ Non-ToolsCallUpdateMessage passed through unchanged");
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
        System.Diagnostics.Debug.WriteLine($"Testing: {functionName} with args: {functionArgs}");

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
            ToolCallUpdates = ImmutableList.Create(toolCallUpdate),
        };

        var messages = new List<IMessage> { toolsCallUpdateMessage };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        System.Diagnostics.Debug.WriteLine($"Result count: {result.Count}");

        // Assert
        Assert.Single(result);
        var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(result[0]);
        Assert.Single(processedMessage.ToolCallUpdates);

        var processedUpdate = processedMessage.ToolCallUpdates[0];
        Assert.Equal(toolCallId, processedUpdate.ToolCallId);
        Assert.Equal(functionName, processedUpdate.FunctionName);
        Assert.Equal(functionArgs, processedUpdate.FunctionArgs);

        // Verify JsonFragmentUpdates were added for non-empty JSON
        if (!string.IsNullOrEmpty(functionArgs) && functionArgs != "{}")
        {
            Assert.NotNull(processedUpdate.JsonFragmentUpdates);
            Assert.NotEmpty(processedUpdate.JsonFragmentUpdates);

            System.Diagnostics.Debug.WriteLine(
                $"JsonFragmentUpdates count: {processedUpdate.JsonFragmentUpdates.Count}"
            );
            foreach (var update in processedUpdate.JsonFragmentUpdates)
            {
                System.Diagnostics.Debug.WriteLine($"  {update.Kind}: Path='{update.Path}' Value='{update.TextValue}'");
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
        System.Diagnostics.Debug.WriteLine($"Testing streaming: '{fragment1}' + '{fragment2}' + '{fragment3}'");

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
            new ToolsCallUpdateMessage { Role = Role.Assistant, ToolCallUpdates = ImmutableList.Create(update1) },
            new ToolsCallUpdateMessage { Role = Role.Assistant, ToolCallUpdates = ImmutableList.Create(update2) },
            new ToolsCallUpdateMessage { Role = Role.Assistant, ToolCallUpdates = ImmutableList.Create(update3) },
        };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        // Assert
        Assert.Equal(3, result.Count);

        // Each message should be processed and have JsonFragmentUpdates
        foreach (var message in result)
        {
            var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(message);
            Assert.Single(processedMessage.ToolCallUpdates);

            var processedUpdate = processedMessage.ToolCallUpdates[0];
            Assert.NotNull(processedUpdate.JsonFragmentUpdates);

            System.Diagnostics.Debug.WriteLine(
                $"Message has {processedUpdate.JsonFragmentUpdates.Count} fragment updates"
            );
            foreach (var update in processedUpdate.JsonFragmentUpdates)
            {
                System.Diagnostics.Debug.WriteLine($"  {update.Kind}: Path='{update.Path}' Value='{update.TextValue}'");
            }
        }

        System.Diagnostics.Debug.WriteLine("✓ All streaming messages processed successfully");
    }

    [Fact]
    public void ProcessAsync_WithEmptyFunctionArgs_ReturnsOriginalUpdate()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing empty function args");

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
            ToolCallUpdates = ImmutableList.Create(toolCallUpdate),
        };

        var messages = new List<IMessage> { toolsCallUpdateMessage };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        // Assert
        Assert.Single(result);
        var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(result[0]);
        var processedUpdate = processedMessage.ToolCallUpdates[0];

        // Original update should be returned unchanged (no JsonFragmentUpdates)
        Assert.Equal("empty-call", processedUpdate.ToolCallId);
        Assert.Equal("empty_function", processedUpdate.FunctionName);
        Assert.Equal("", processedUpdate.FunctionArgs);
        Assert.Null(processedUpdate.JsonFragmentUpdates);

        System.Diagnostics.Debug.WriteLine("✓ Empty function args handled correctly");
    }

    [Fact]
    public void ProcessAsync_WithNullFunctionArgs_ReturnsOriginalUpdate()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing null function args");

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
            ToolCallUpdates = ImmutableList.Create(toolCallUpdate),
        };

        var messages = new List<IMessage> { toolsCallUpdateMessage };

        // Act
        var result = ProcessMessagesSync(middleware, messages);

        // Assert
        Assert.Single(result);
        var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(result[0]);
        var processedUpdate = processedMessage.ToolCallUpdates[0];

        // Original update should be returned unchanged (no JsonFragmentUpdates)
        Assert.Equal("null-call", processedUpdate.ToolCallId);
        Assert.Equal("null_function", processedUpdate.FunctionName);
        Assert.Null(processedUpdate.FunctionArgs);
        Assert.Null(processedUpdate.JsonFragmentUpdates);

        System.Diagnostics.Debug.WriteLine("✓ Null function args handled correctly");
    }

    [Fact]
    public void ProcessAsync_WithMultipleToolCallUpdatesInSameMessage_ProcessesEachSeparately()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing multiple tool call updates in same message");

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
        Assert.Single(result);
        var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(result[0]);
        Assert.Equal(2, processedMessage.ToolCallUpdates.Count);

        foreach (var processedUpdate in processedMessage.ToolCallUpdates)
        {
            Assert.NotNull(processedUpdate.JsonFragmentUpdates);
            Assert.NotEmpty(processedUpdate.JsonFragmentUpdates);

            System.Diagnostics.Debug.WriteLine(
                $"Tool call {processedUpdate.ToolCallId} has {processedUpdate.JsonFragmentUpdates.Count} fragment updates"
            );
        }

        System.Diagnostics.Debug.WriteLine("✓ Multiple tool call updates processed correctly");
    }

    [Fact]
    public void ClearGenerators_RemovesAllGeneratorState()
    {
        // Arrange
        System.Diagnostics.Debug.WriteLine("Testing generator clearing");

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
            ToolCallUpdates = ImmutableList.Create(toolCallUpdate),
        };

        var messages = new List<IMessage> { toolsCallUpdateMessage };

        // Process once to create generators
        ProcessMessagesSync(middleware, messages);

        // Act
        middleware.ClearGenerators();

        // Assert
        // Processing the same message again should work (no state corruption)
        var result = ProcessMessagesSync(middleware, messages);
        Assert.Single(result);

        var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(result[0]);
        var processedUpdate = processedMessage.ToolCallUpdates[0];
        Assert.NotNull(processedUpdate.JsonFragmentUpdates);

        System.Diagnostics.Debug.WriteLine("✓ Generator clearing works correctly");
    }

    [Fact]
    public void ProcessAsync_WithCompleteJsonToolCall_IncludesJsonCompleteEvent()
    {
        System.Diagnostics.Debug.WriteLine(
            "Testing ProcessAsync with complete JSON tool call includes JsonComplete event"
        );

        var middleware = new JsonFragmentUpdateMiddleware();

        // Create a complete JSON tool call update
        var toolCallUpdate = new ToolCallUpdate
        {
            FunctionName = "test_function",
            FunctionArgs = "{\"message\": \"Hello World\"}",
            Index = 0,
            ToolCallId = "call_123",
        };

        var message = new ToolsCallUpdateMessage
        {
            Role = Role.Assistant,
            ToolCallUpdates = ImmutableList.Create(toolCallUpdate),
        };

        var messages = new[] { message };
        var result = ProcessMessagesSync(middleware, messages);

        Assert.Single(result);
        var processedMessage = Assert.IsType<ToolsCallUpdateMessage>(result[0]);
        var processedToolCall = processedMessage.ToolCallUpdates[0];

        // Verify that JsonFragmentUpdates were added
        Assert.NotNull(processedToolCall.JsonFragmentUpdates);
        Assert.NotEmpty(processedToolCall.JsonFragmentUpdates);

        // Verify that a JsonComplete event was included
        var jsonCompleteUpdates = processedToolCall
            .JsonFragmentUpdates.Where(u => u.Kind == JsonFragmentKind.JsonComplete)
            .ToList();

        Assert.Single(jsonCompleteUpdates);

        var completeEvent = jsonCompleteUpdates.First();
        Assert.Equal("root", completeEvent.Path);
        Assert.Equal("{\"message\": \"Hello World\"}", completeEvent.TextValue);

        System.Diagnostics.Debug.WriteLine($"✓ JsonComplete event found: {completeEvent.TextValue}");
        System.Diagnostics.Debug.WriteLine($"✓ Total fragment updates: {processedToolCall.JsonFragmentUpdates.Count}");
    }

    /// <summary>
    /// Helper method to process messages synchronously for testing
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
    /// Helper method to convert IEnumerable to IAsyncEnumerable for testing
    /// </summary>
    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
            await Task.Yield(); // Allow for async behavior
        }
    }
}
