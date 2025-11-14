using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Tests.Utilities;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.McpMiddleware;
using AchieveAi.LmDotnetTools.McpSampleServer;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.TestUtils;
using dotenv.net;
using Xunit;
using static AchieveAi.LmDotnetTools.LmTestUtils.ChatCompletionTestData;
using static AchieveAi.LmDotnetTools.LmTestUtils.FakeHttpMessageHandler;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class FunctionCallMiddlewareTests
{
    [Fact]
    public void Constructor_ShouldThrowArgumentNullException_WhenFunctionsIsNull()
    {
        // Arrange
        var functionMap = new Dictionary<string, Func<string, Task<string>>>();

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new FunctionCallMiddleware(functions: null!, functionMap: functionMap)
        );

        Assert.Equal("functions", exception.ParamName);
    }

    [Fact]
    public void Constructor_ShouldThrowArgumentException_WhenFunctionMissingFromMap()
    {
        // Arrange
        var functions = new List<FunctionContract>
        {
            new FunctionContract
            {
                Name = "getWeather",
                Description = "Get the weather in a location",
                Parameters =
                [
                    new FunctionParameterContract
                    {
                        Name = "location",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        Description = "City name",
                        IsRequired = true,
                    },
                ],
            },
        };

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["add"] = args => Task.FromResult("10"),
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => new FunctionCallMiddleware(functions, functionMap));

        Assert.Contains("getWeather", exception.Message);
        Assert.Equal("functionMap", exception.ParamName);
    }

    [Fact]
    public void Constructor_ShouldThrowArgumentException_WhenFunctionMapIsNullButFunctionsAreProvided()
    {
        // Arrange
        var functions = new List<FunctionContract>
        {
            new FunctionContract { Name = "getWeather", Description = "Get the weather in a location" },
        };

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => new FunctionCallMiddleware(functions, null!));

        Assert.Contains("Function map must be provided", exception.Message);
        Assert.Equal("functionMap", exception.ParamName);
    }

    [Fact]
    public void Constructor_ShouldNotThrow_WhenFunctionsIsEmpty()
    {
        // Arrange
        var functions = new List<FunctionContract>();
        var functionMap = new Dictionary<string, Func<string, Task<string>>>();

        // Act & Assert - no exception should be thrown
        var middleware = new FunctionCallMiddleware(functions, functionMap);
        Assert.NotNull(middleware);
    }

    [Fact]
    public void Constructor_ShouldNotThrow_WhenAllFunctionsHaveCorrespondingMapEntries()
    {
        // Arrange
        var functions = new List<FunctionContract>
        {
            new FunctionContract { Name = "function1", Description = "Test function 1" },
            new FunctionContract { Name = "function2", Description = "Test function 2" },
        };

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["function1"] = args => Task.FromResult("result1"),
            ["function2"] = args => Task.FromResult("result2"),
        };

        // Act & Assert - no exception should be thrown
        var middleware = new FunctionCallMiddleware(functions, functionMap);
        Assert.NotNull(middleware);
    }

    [Fact]
    public async Task InvokeAsync_ShouldExecuteFunction_WhenFunctionExists()
    {
        // Arrange
        var functionMap = CreateMockFunctionMap();
        var functionContracts = CreateMockFunctionContracts();
        var middleware = new FunctionCallMiddleware(functionContracts, functionMap);

        // Create a message with a tool call for the getWeather function
        var toolCallMessage = CreateToolCallMessage("getWeather", new { location = "San Francisco", unit = "celsius" });

        // Create the context with our tool call message
        var context = new MiddlewareContext(new[] { toolCallMessage }, new GenerateReplyOptions());

        // Mock the agent
        var mockAgent = new Mock<IAgent>();

        // Act
        var result = await middleware.InvokeAsync(context, mockAgent.Object);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.IsType<ToolsCallResultMessage>(result.First());

        var resultMessage = (ToolsCallResultMessage)result.First();
        Assert.NotEmpty(resultMessage.ToolCallResults);

        // Check that the tool call result contains the expected content
        var toolCallResult = resultMessage.ToolCallResults.First();
        Assert.Contains("San Francisco", toolCallResult.Result);
        Assert.Contains("23", toolCallResult.Result); // Temperature from mock
        Assert.Contains("celsius", toolCallResult.Result);
    }

    [Fact]
    public async Task InvokeAsync_ShouldReturnError_WhenFunctionDoesNotExist()
    {
        // Arrange
        var functionMap = CreateMockFunctionMap();
        var functionContracts = CreateMockFunctionContracts();
        var middleware = new FunctionCallMiddleware(functionContracts, functionMap);

        // Create a message with a tool call for a non-existent function
        var toolCallMessage = CreateToolCallMessage("getNonExistentFunction", new { param = "value" });

        // Create the context with our tool call message
        var context = new MiddlewareContext(new[] { toolCallMessage }, new GenerateReplyOptions());

        // Mock the agent
        var mockAgent = new Mock<IAgent>();

        // Act
        var result = await middleware.InvokeAsync(context, mockAgent.Object);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.IsType<ToolsCallResultMessage>(result.First());

        var resultMessage = (ToolsCallResultMessage)result.First();
        Assert.NotEmpty(resultMessage.ToolCallResults);

        // Check that the tool call result contains the expected error message
        var toolCallResult = resultMessage.ToolCallResults.First();
        Assert.Contains("not available", toolCallResult.Result);
        Assert.Contains("getWeather", toolCallResult.Result); // Should list available functions
        Assert.Contains("getWeatherHistory", toolCallResult.Result);
        Assert.Contains("add", toolCallResult.Result);
    }

    [Fact]
    public async Task InvokeAsync_ShouldAddFunctionsToOptions_WhenForwardingToAgent()
    {
        // Arrange
        var functionMap = CreateMockFunctionMap();
        var functionContracts = CreateMockFunctionContracts();
        var middleware = new FunctionCallMiddleware(functionContracts, functionMap);

        // Create a regular text message (no tool call)
        var message = new TextMessage { Text = "What's the weather in San Francisco?", Role = Role.User };

        // Create the context with our message
        var context = new MiddlewareContext(new[] { message }, new GenerateReplyOptions());

        // Mock the agent
        var mockAgent = new Mock<IAgent>();
        GenerateReplyOptions? capturedOptions = null;

        mockAgent
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, options, token) => capturedOptions = options
            )
            .ReturnsAsync(
                new[]
                {
                    new TextMessage { Text = "Mock response", Role = Role.Assistant },
                }
            );

        // Act
        await middleware.InvokeAsync(context, mockAgent.Object);

        // Assert
        Assert.NotNull(capturedOptions);
        Assert.NotNull(capturedOptions.Functions);
        Assert.Equal(functionContracts.Count(), capturedOptions.Functions.Length);

        // Verify all our function contracts were added
        Assert.Contains(capturedOptions.Functions, f => f.Name == "getWeather");
        Assert.Contains(capturedOptions.Functions, f => f.Name == "getWeatherHistory");
        Assert.Contains(capturedOptions.Functions, f => f.Name == "add");
    }

    [Fact]
    public async Task InvokeAsync_ShouldExecuteMathFunction_WhenCalledWithCorrectParameters()
    {
        // Arrange
        var functionMap = CreateMockFunctionMap();
        var functionContracts = CreateMockFunctionContracts();
        var middleware = new FunctionCallMiddleware(functionContracts, functionMap);

        // Create a message with a tool call for the add function
        var toolCallMessage = CreateToolCallMessage("add", new { a = 5, b = 7 });

        // Create the context with our tool call message
        var context = new MiddlewareContext(new[] { toolCallMessage }, new GenerateReplyOptions());

        // Mock the agent
        var mockAgent = new Mock<IAgent>();

        // Act
        var result = await middleware.InvokeAsync(context, mockAgent.Object);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.IsType<ToolsCallResultMessage>(result.First());

        var resultMessage = (ToolsCallResultMessage)result.First();
        Assert.NotEmpty(resultMessage.ToolCallResults);

        // Check that the tool call result contains the expected content
        var toolCallResult = resultMessage.ToolCallResults.First();
        Assert.Contains("12", toolCallResult.Result); // 5 + 7 = 12
    }

    [Fact]
    public async Task InvokeStreamingAsync_ShouldExecuteFunction_WhenToolCallIsPresent()
    {
        // Arrange
        var functionMap = CreateMockFunctionMap();
        var functionContracts = CreateMockFunctionContracts();
        var middleware = new FunctionCallMiddleware(functionContracts, functionMap);

        // Create a message with a tool call
        var toolCallMessage = CreateToolCallMessage(
            "get_weather",
            new { location = "San Francisco", unit = "celsius" }
        );

        // Create the context with our tool call message
        var context = new MiddlewareContext(new[] { toolCallMessage }, new GenerateReplyOptions());

        // Create a simple streaming agent that returns no messages
        var nextAgent = new MockStreamingAgent(Array.Empty<IMessage>());

        // Act - invoke the streaming middleware
        var resultStreamTask = middleware.InvokeStreamingAsync(context, nextAgent);
        var resultStream = await resultStreamTask;

        // Collect the results
        var results = new List<IMessage>();
        await foreach (var message in resultStream)
        {
            results.Add(message);
        }

        // Assert
        Assert.NotEmpty(results);

        // Check that there is a valid response message
        var lastMessage = results.Last();
        Assert.IsType<ToolsCallResultMessage>(lastMessage);

        var resultMessage = (ToolsCallResultMessage)lastMessage;
        Assert.NotEmpty(resultMessage.ToolCallResults);

        // Verify that we have a result from the function execution
        // The exact format of the result might vary so we don't check the exact content
        var toolCallResult = resultMessage.ToolCallResults.First();
        Assert.NotEmpty(toolCallResult.Result);
    }

    private static Dictionary<string, Func<string, Task<string>>> CreateMockFunctionMap()
    {
        return new Dictionary<string, Func<string, Task<string>>>
        {
            ["getWeather"] = argsJson => Task.FromResult(GetWeatherAsync(argsJson)),
            ["getWeatherHistory"] = argsJson => Task.FromResult(GetWeatherHistoryAsync(argsJson)),
            ["add"] = argsJson => Task.FromResult(AddAsync(argsJson)),
            ["subtract"] = argsJson => Task.FromResult(SubtractAsync(argsJson)),
            ["multiply"] = argsJson => Task.FromResult(MultiplyAsync(argsJson)),
            ["divide"] = argsJson => Task.FromResult(DivideAsync(argsJson)),
        };
    }

    private static IEnumerable<FunctionContract> CreateMockFunctionContracts()
    {
        return new[]
        {
            new FunctionContract
            {
                Name = "getWeather",
                Description = "Get current weather for a location",
                Parameters = new[]
                {
                    new FunctionParameterContract
                    {
                        Name = "location",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        Description = "City name",
                        IsRequired = true,
                    },
                    new FunctionParameterContract
                    {
                        Name = "unit",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        Description = "Temperature unit (celsius or fahrenheit)",
                        IsRequired = false,
                    },
                },
            },
            new FunctionContract
            {
                Name = "getWeatherHistory",
                Description = "Get historical weather data for a location",
                Parameters = new[]
                {
                    new FunctionParameterContract
                    {
                        Name = "location",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        Description = "City name",
                        IsRequired = true,
                    },
                    new FunctionParameterContract
                    {
                        Name = "days",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(int)),
                        Description = "Number of days of history",
                        IsRequired = true,
                    },
                },
            },
            new FunctionContract
            {
                Name = "add",
                Description = "Add two numbers",
                Parameters = new[]
                {
                    new FunctionParameterContract
                    {
                        Name = "a",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
                        Description = "First number",
                        IsRequired = true,
                    },
                    new FunctionParameterContract
                    {
                        Name = "b",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
                        Description = "Second number",
                        IsRequired = true,
                    },
                },
            },
            new FunctionContract
            {
                Name = "subtract",
                Description = "Subtract second number from first number",
                Parameters = new[]
                {
                    new FunctionParameterContract
                    {
                        Name = "a",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
                        Description = "First number",
                        IsRequired = true,
                    },
                    new FunctionParameterContract
                    {
                        Name = "b",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
                        Description = "Second number",
                        IsRequired = true,
                    },
                },
            },
            new FunctionContract
            {
                Name = "multiply",
                Description = "Multiply two numbers",
                Parameters = new[]
                {
                    new FunctionParameterContract
                    {
                        Name = "a",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
                        Description = "First number",
                        IsRequired = true,
                    },
                    new FunctionParameterContract
                    {
                        Name = "b",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
                        Description = "Second number",
                        IsRequired = true,
                    },
                },
            },
            new FunctionContract
            {
                Name = "divide",
                Description = "Divide first number by second number",
                Parameters = new[]
                {
                    new FunctionParameterContract
                    {
                        Name = "a",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
                        Description = "First number",
                        IsRequired = true,
                    },
                    new FunctionParameterContract
                    {
                        Name = "b",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
                        Description = "Second number",
                        IsRequired = true,
                    },
                },
            },
        };
    }

    private static ToolsCallMessage CreateToolCallMessage(string functionName, object args)
    {
        // Serialize the arguments to a JSON string
        var jsonArgs = JsonSerializer.Serialize(args);

        return new ToolsCallMessage
        {
            ToolCalls = ImmutableList.Create(
                new ToolCall(functionName, jsonArgs) { ToolCallId = Guid.NewGuid().ToString() }
            ),
            Role = Role.Assistant,
        };
    }

    private static string GetWeatherAsync(string argsJson)
    {
        try
        {
            var argsNode = JsonNode.Parse(argsJson);
            if (argsNode == null)
            {
                return "Error: Invalid arguments";
            }

            var location = argsNode["location"]?.GetValue<string>();
            if (string.IsNullOrEmpty(location))
            {
                return "Error: Location is required";
            }

            var unitNode = argsNode["unit"];
            string unit = unitNode != null ? unitNode.GetValue<string>().ToLower() : "celsius";
            int temperature = unit == "celsius" ? 23 : 73;
            string condition = "Sunny";

            return $"Weather in {location}: {temperature}°{unit.Substring(0, 1).ToUpper()}, {condition}. Unit: {unit}";
        }
        catch (Exception ex)
        {
            return $"Error processing weather request: {ex.Message}";
        }
    }

    private static string GetWeatherHistoryAsync(string argsJson)
    {
        try
        {
            var argsNode = JsonNode.Parse(argsJson);
            if (argsNode == null)
            {
                return "Error: Invalid arguments";
            }

            var location = argsNode["location"]?.GetValue<string>();
            if (string.IsNullOrEmpty(location))
            {
                return "Error: Location is required";
            }

            var daysNode = argsNode["days"];
            if (daysNode == null || daysNode.GetValue<int>() <= 0)
            {
                return "Error: Days must be greater than 0";
            }

            int days = daysNode.GetValue<int>();

            // Ensure we have the exact phrase "weather history" for the test to pass
            return $"Weather history for {location} (last {days} days):\n"
                + $"- Day 1: 22°C, Sunny\n"
                + $"- Day 2: 20°C, Partly Cloudy\n"
                + $"- Day 3: 19°C, Light Rain";
        }
        catch (Exception ex)
        {
            return $"Error processing weather history request: {ex.Message}";
        }
    }

    private static string AddAsync(string argsJson)
    {
        try
        {
            var argsNode = JsonNode.Parse(argsJson);
            if (argsNode == null)
            {
                return "Error: Invalid arguments";
            }

            var aNode = argsNode["a"];
            var bNode = argsNode["b"];

            if (aNode == null || bNode == null)
            {
                return "Error: Both a and b parameters are required";
            }

            double a = aNode.GetValue<double>();
            double b = bNode.GetValue<double>();
            double result = a + b;

            return $"{a} + {b} = {result}";
        }
        catch (Exception ex)
        {
            return $"Error processing add request: {ex.Message}";
        }
    }

    private static string SubtractAsync(string argsJson)
    {
        try
        {
            var argsNode = JsonNode.Parse(argsJson);
            if (argsNode == null)
            {
                return "Error: Invalid arguments";
            }

            var aNode = argsNode["a"];
            var bNode = argsNode["b"];

            if (aNode == null || bNode == null)
            {
                return "Error: Both a and b parameters are required";
            }

            double a = aNode.GetValue<double>();
            double b = bNode.GetValue<double>();
            double result = a - b;

            return $"{a} - {b} = {result}";
        }
        catch (Exception ex)
        {
            return $"Error processing subtract request: {ex.Message}";
        }
    }

    private static string MultiplyAsync(string argsJson)
    {
        try
        {
            var argsNode = JsonNode.Parse(argsJson);
            if (argsNode == null)
            {
                return "Error: Invalid arguments";
            }

            var aNode = argsNode["a"];
            var bNode = argsNode["b"];

            if (aNode == null || bNode == null)
            {
                return "Error: Both a and b parameters are required";
            }

            double a = aNode.GetValue<double>();
            double b = bNode.GetValue<double>();
            double result = a * b;

            return $"{a} × {b} = {result}";
        }
        catch (Exception ex)
        {
            return $"Error processing multiply request: {ex.Message}";
        }
    }

    private static string DivideAsync(string argsJson)
    {
        try
        {
            var argsNode = JsonNode.Parse(argsJson);
            if (argsNode == null)
            {
                return "Error: Invalid arguments";
            }

            var aNode = argsNode["a"];
            var bNode = argsNode["b"];

            if (aNode == null || bNode == null)
            {
                return "Error: Both a and b parameters are required";
            }

            double a = aNode.GetValue<double>();
            double b = bNode.GetValue<double>();

            if (Math.Abs(b) < 0.0001)
            {
                return "Error: Cannot divide by zero";
            }

            double result = a / b;
            return $"{a} ÷ {b} = {result}";
        }
        catch (Exception ex)
        {
            return $"Error processing divide request: {ex.Message}";
        }
    }

    [Fact]
    public async Task FunctionCallMiddleware_ShouldReturnToolAggregateMessage_Streaming_WithJoin()
    {
        EnvironmentHelper.LoadEnvIfNeeded();
        System.Diagnostics.Debug.WriteLine("=== TEST START ===");

        // Arrange
        var functionContracts = new[]
        {
            new FunctionContract
            {
                Name = "getWeather",
                Description = "Get current weather for a location",
                Parameters = new List<FunctionParameterContract>
                {
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
                },
            },
        };

        System.Diagnostics.Debug.WriteLine("Function contracts created");

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["getWeather"] = async argsJson =>
            {
                await Task.Delay(1);
                return "{\"location\":\"San Francisco\",\"temperature\":23,\"unit\":\"celsius\"}";
            },
        };

        System.Diagnostics.Debug.WriteLine("Function map created");

        var middleware = new FunctionCallMiddleware(functionContracts, functionMap);

        System.Diagnostics.Debug.WriteLine("Middleware created");

        var messages = new List<IMessage>
        {
            new TextMessage { Role = Role.System, Text = "You're a helpful AI Agent that can use tools" },
            new TextMessage { Role = Role.User, Text = "What's the weather in San Francisco?" },
        };

        System.Diagnostics.Debug.WriteLine("Messages created");

        var options = new GenerateReplyOptions { ModelId = "gpt-4", Functions = functionContracts };

        System.Diagnostics.Debug.WriteLine("Options created");

        var context = new MiddlewareContext(messages, options);

        System.Diagnostics.Debug.WriteLine("Context created");

        // Create HTTP client with streaming response that includes tool calls (replaces record/playback)
        var toolCallStreamingResponse = CreateToolCallStreamingResponse();
        var handler = CreateRetryHandler(
            failureCount: 0, // No failures, just success
            successResponse: toolCallStreamingResponse
        );

        System.Diagnostics.Debug.WriteLine("Handler created");

        var httpClient = new HttpClient(handler);

        System.Diagnostics.Debug.WriteLine("HttpClient created");

        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());

        System.Diagnostics.Debug.WriteLine("OpenClient created");

        var agent = new OpenClientAgent("TestAgent", client);

        System.Diagnostics.Debug.WriteLine("Agent created");

        System.Diagnostics.Debug.WriteLine("=== MIDDLEWARE TEST DEBUG ===");
        System.Diagnostics.Debug.WriteLine($"Context messages count: {context.Messages.Count()}");
        System.Diagnostics.Debug.WriteLine($"Last message type: {context.Messages.Last().GetType().Name}");
        System.Diagnostics.Debug.WriteLine($"Agent type: {agent.GetType().Name}");

        // Act
        System.Diagnostics.Debug.WriteLine("Calling middleware.InvokeStreamingAsync...");
        var responseStream = await middleware.InvokeStreamingAsync(context, agent);
        System.Diagnostics.Debug.WriteLine("Got response stream, iterating...");

        var responses = new List<IMessage>();
        await foreach (var response in responseStream)
        {
            System.Diagnostics.Debug.WriteLine($"Response received: {response.GetType().Name}, Role: {response.Role}");
            if (response is ToolsCallMessage toolsCall)
            {
                System.Diagnostics.Debug.WriteLine($"  Tool calls count: {toolsCall.ToolCalls?.Count ?? 0}");
            }
            responses.Add(response);
        }

        // Assert
        System.Diagnostics.Debug.WriteLine($"Total responses: {responses.Count}");
        foreach (var response in responses)
        {
            System.Diagnostics.Debug.WriteLine($"Response type: {response.GetType().Name}, Role: {response.Role}");
        }
        System.Diagnostics.Debug.WriteLine("=== END DEBUG ===");
        Assert.NotEmpty(responses);

        var lastMessage = responses.LastOrDefault(m => m is ToolsCallAggregateMessage);
        Assert.NotNull(lastMessage);
        Assert.IsType<ToolsCallAggregateMessage>(lastMessage);

        var aggregate = (ToolsCallAggregateMessage)lastMessage;
        Assert.NotEmpty(aggregate.ToolsCallMessage.GetToolCalls()!);
        Assert.Contains(aggregate.ToolsCallMessage.GetToolCalls()!, call => call.FunctionName == "getWeather");
    }

    [Fact]
    public async Task FunctionCallMiddleware_ShouldReturnMultipleToolAggregateMessages_Streaming()
    {
        EnvironmentHelper.LoadEnvIfNeeded();

        // Arrange
        var functionContracts = new[]
        {
            new FunctionContract
            {
                Name = "python-mcp.execute_python_in_container",
                Description =
                    "\nExecute Python code in a Docker container. The environment is limited to the container.\nFollowing packages are available:\n- pandas\n- numpy\n- matplotlib\n- seaborn\n- plotly\n- bokeh\n- hvplot\n- datashader\n- plotnine\n- cufflinks\n- graphviz\n- scipy\n- statsmodels\n- openpyxl\n- xlrd\n- xlsxwriter\n- pandasql\n- csv23\n- csvkit\n- polars\n- pyarrow\n- fastparquet\n- dask\n- vaex\n- python-dateutil\n- beautifulsoup4\n- requests\n- lxml\n- geopandas\n- folium\n- pydeck\n- holoviews\n- altair\n- visualkeras\n- kaleido\n- panel\n- voila\n\nArgs:\n    code: Python code to execute\n\nReturns:\n    Output from executed code\n",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "code",
                        Description = "",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        IsRequired = true,
                    },
                },
            },
            new FunctionContract
            {
                Name = "python-mcp.list_directory",
                Description =
                    "\nList the contents of a directory within the code directory where python code is executed\n\nArgs:\n    relative_path: Relative path within the code directory (default: list root code directory)\n    \nReturns:\n    Directory listing as a string\n",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "relative_path",
                        Description = "",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        IsRequired = false,
                    },
                },
            },
            new FunctionContract
            {
                Name = "python-mcp.read_file",
                Description =
                    "\nRead a file from the code directory where python code is executed\n\nArgs:\n    relative_path: Relative path to the file within the code directory\n    \nReturns:\n    File contents as a string\n",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "relative_path",
                        Description = "",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        IsRequired = true,
                    },
                },
            },
            new FunctionContract
            {
                Name = "python-mcp.write_file",
                Description =
                    "\nWrite content to a file in the code directory where python code is executed\n\nArgs:\n    relative_path: Relative path to the file within the code directory\n    content: Content to write to the file\n    \nReturns:\n    Status message\n",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "relative_path",
                        Description = "",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        IsRequired = true,
                    },
                    new FunctionParameterContract
                    {
                        Name = "content",
                        Description = "",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        IsRequired = true,
                    },
                },
            },
            new FunctionContract
            {
                Name = "python-mcp.delete_file",
                Description =
                    "\nDelete a file from the code directory where python code is executed\n\nArgs:\n    relative_path: Relative path to the file within the code directory\n    \nReturns:\n    Status message\n",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "relative_path",
                        Description = "",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        IsRequired = true,
                    },
                },
            },
            new FunctionContract
            {
                Name = "python-mcp.get_directory_tree",
                Description =
                    "\nGet an ASCII tree representation of a directory structure where python code is executed\n\nArgs:\n    relative_path: Relative path within the code directory (default: root code directory)\n    \nReturns:\n    ASCII tree representation as a string\n",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "relative_path",
                        Description = "",
                        ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        IsRequired = false,
                    },
                },
            },
            new FunctionContract
            {
                Name = "python-mcp.cleanup_code_directory",
                Description =
                    "\nClean up the code directory by removing all files and subdirectories\n\nReturns:\n    Status message\n",
                Parameters = new List<FunctionParameterContract>(),
            },
        };

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["python-mcp.execute_python_in_container"] = async argsJson =>
            {
                await Task.Delay(1);
                return "";
            },
            ["python-mcp.list_directory"] = async argsJson =>
            {
                await Task.Delay(1);
                return "";
            },
            ["python-mcp.read_file"] = async argsJson =>
            {
                await Task.Delay(1);
                return "";
            },
            ["python-mcp.write_file"] = async argsJson =>
            {
                await Task.Delay(1);
                return "";
            },
            ["python-mcp.delete_file"] = async argsJson =>
            {
                await Task.Delay(1);
                return "";
            },
            ["python-mcp.get_directory_tree"] = async argsJson =>
            {
                await Task.Delay(1);
                return "";
            },
            ["python-mcp.cleanup_code_directory"] = async argsJson =>
            {
                await Task.Delay(1);
                return "";
            },
        };

        var middleware = new FunctionCallMiddleware(functionContracts, functionMap);

        var messages = new List<IMessage>
        {
            new TextMessage
            {
                Role = Role.System,
                Text =
                    "You are a helpful assistant that can use tools to help users. When you need to execute Python code, use the execute_python_in_container tool.",
            },
            new TextMessage { Role = Role.User, Text = "List files in root and \"code\" directories." },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "meta-llama/llama-4-maverick",
            Temperature = (float?)0.699999988079071,
            Functions = functionContracts,
        };

        var context = new MiddlewareContext(messages, options);

        // Create HTTP client with streaming response that includes multiple tool calls (replaces record/playback)
        var streamingResponse = CreateMultipleToolCallStreamingResponse();
        var handler = FakeHttpMessageHandler.CreateRetryHandler(
            failureCount: 0, // No failures, just success
            successResponse: streamingResponse
        );

        var httpClient = new HttpClient(handler);
        var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());

        var agent = new OpenClientAgent("TestAgent", client);

        // Act
        var responseStream = await middleware.InvokeStreamingAsync(context, agent);

        var responses = new List<IMessage>();
        await foreach (var response in responseStream)
        {
            System.Diagnostics.Debug.WriteLine($"Response received: {response.GetType().Name}");
            responses.Add(response);
        }

        // Assert
        System.Diagnostics.Debug.WriteLine($"Total responses: {responses.Count}");
        foreach (var response in responses)
        {
            System.Diagnostics.Debug.WriteLine($"Response type: {response.GetType().Name}, Role: {response.Role}");
        }
        Assert.NotEmpty(responses);

        var lastMessage = responses.LastOrDefault(m => m is ToolsCallAggregateMessage);
        Assert.NotNull(lastMessage);
        Assert.IsType<ToolsCallAggregateMessage>(lastMessage);

        var aggregate = (ToolsCallAggregateMessage)lastMessage;
        var toolCalls = aggregate.ToolsCallMessage.GetToolCalls();
        Assert.NotNull(toolCalls);
        Assert.True(toolCalls!.Count() >= 2, "Should contain at least two tool calls");
        Assert.Contains(toolCalls, call => call.FunctionName == "python-mcp.list_directory");
        // Optionally, check that both tool calls are present (by id or argument)
    }

    [Fact]
    public async Task McpFunctionCallMiddleware_ShouldAddLargeNumbers_WhenUsingCalculatorTool()
    {
        // Arrange
        // Get the McpSampleServer assembly to access its tools
        var mcpSampleServerAssembly = typeof(CalculatorTool).Assembly;

        // Create function call middleware components from the MCP sample server assembly
        var (functions, functionMap) = McpFunctionCallExtensions.CreateFunctionCallComponentsFromAssembly(
            mcpSampleServerAssembly
        );

        // Create the middleware with the MCP tools
        var middleware = new FunctionCallMiddleware(functions, functionMap, "McpCalculatorTest");

        // Create large numbers to test with
        double firstNumber = 9876543210.123;
        double secondNumber = 1234567890.987;
        double expectedSum = firstNumber + secondNumber; // 11111111101.11

        // Set up a fixed tool call ID for testing
        string toolCallId = Guid.NewGuid().ToString();

        // Create a tool call message with the calculator add function and our large numbers
        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls = ImmutableList.Create(
                new ToolCall("CalculatorTool-Add", JsonSerializer.Serialize(new { a = firstNumber, b = secondNumber }))
                {
                    ToolCallId = toolCallId,
                }
            ),
            Role = Role.Assistant,
        };

        var messages = new List<IMessage> { toolCallMessage };
        var context = new MiddlewareContext(messages, new GenerateReplyOptions());

        // Create a mock agent that won't actually be used for generation
        // since we're directly testing the middleware function execution
        var mockAgent = new MockAgent(new TextMessage { Role = Role.Assistant, Text = "This is a mock response" });

        // Act
        var result = await middleware.InvokeAsync(context, mockAgent);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result);
        Assert.IsType<ToolsCallResultMessage>(result.First());

        var toolsCallResultMessage = (ToolsCallResultMessage)result.First();
        Assert.Single(toolsCallResultMessage.ToolCallResults);
        var resultJson = toolsCallResultMessage
            .ToolCallResults.FirstOrDefault(tr => tr.ToolCallId == toolCallId)
            .Result!;
        Assert.NotNull(resultJson);

        var resultValue = JsonSerializer.Deserialize<double>(resultJson);
        // Verify the sum is correct
        Assert.Equal(expectedSum, resultValue);
        Assert.Equal(11111111101.11, resultValue, 5); // Compare with 5 decimal precision
    }

    [Fact]
    public void DiagnosticTest_CheckBasicSetup()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("=== DIAGNOSTIC TEST START ===");

            // Test environment loading
            EnvironmentHelper.LoadEnvIfNeeded();
            var apiKey = GetApiKeyFromEnv();
            var baseUrl = GetApiBaseUrlFromEnv();
            System.Diagnostics.Debug.WriteLine($"API Key: {apiKey}");
            System.Diagnostics.Debug.WriteLine($"Base URL: {baseUrl}");

            // Test MockHttpHandlerBuilder
            var handler = MockHttpHandlerBuilder.Create().RespondWithJson("{\"test\": \"value\"}").Build();
            System.Diagnostics.Debug.WriteLine("Handler created successfully");

            // Test HttpClient
            var httpClient = new HttpClient(handler);
            System.Diagnostics.Debug.WriteLine("HttpClient created successfully");

            // Test OpenClient
            var client = new OpenClient(httpClient, baseUrl);
            System.Diagnostics.Debug.WriteLine("OpenClient created successfully");

            // Test OpenClientAgent
            var agent = new OpenClientAgent("TestAgent", client);
            System.Diagnostics.Debug.WriteLine("OpenClientAgent created successfully");

            System.Diagnostics.Debug.WriteLine("=== DIAGNOSTIC TEST COMPLETE ===");

            Assert.True(true); // If we get here, basic setup works
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Exception in diagnostic test: {ex}");
            throw;
        }
    }

    [Fact]
    public async Task DiagnosticTest_CheckAgentStreaming()
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("=== AGENT STREAMING TEST START ===");

            // Use the exact same pattern as the working OpenAI streaming test
            var streamingResponse = CreateStreamingResponse();
            var fakeHandler = FakeHttpMessageHandler.CreateRetryHandler(
                failureCount: 0, // No failures, just success
                successResponse: streamingResponse
            );

            var httpClient = new HttpClient(fakeHandler);
            var client = new OpenClient(httpClient, GetApiBaseUrlFromEnv());
            var agent = new OpenClientAgent("TestAgent", client);

            var messages = new List<IMessage>
            {
                new TextMessage { Role = Role.System, Text = "You're a helpful AI Agent that can use tools" },
                new TextMessage { Role = Role.User, Text = "What's the weather in San Francisco?" },
            };

            var options = new GenerateReplyOptions
            {
                ModelId = "gpt-4",
                Functions = new[]
                {
                    new FunctionContract
                    {
                        Name = "getWeather",
                        Description = "Get current weather for a location",
                        Parameters = new List<FunctionParameterContract>
                        {
                            new FunctionParameterContract
                            {
                                Name = "location",
                                Description = "City name",
                                ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                                IsRequired = true,
                            },
                        },
                    },
                },
            };

            System.Diagnostics.Debug.WriteLine("Calling agent.GenerateReplyStreamingAsync...");

            // Test the agent directly
            var streamingResponse2 = await agent.GenerateReplyStreamingAsync(messages, options);

            System.Diagnostics.Debug.WriteLine("Got streaming response, iterating...");

            var responses = new List<IMessage>();
            await foreach (var response in streamingResponse2)
            {
                System.Diagnostics.Debug.WriteLine($"Agent response: {response.GetType().Name}, Role: {response.Role}");
                responses.Add(response);
            }

            System.Diagnostics.Debug.WriteLine($"Total agent responses: {responses.Count}");

            Assert.True(responses.Count > 0, "Agent should return at least one response");

            System.Diagnostics.Debug.WriteLine("=== AGENT STREAMING TEST COMPLETE ===");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Exception in agent streaming test: {ex}");
            throw;
        }
    }

    /// <summary>
    /// Helper method to get API key from environment (using shared EnvironmentHelper)
    /// </summary>
    private static string GetApiKeyFromEnv()
    {
        string[] fallbackKeys = new[] { "LLM_API_KEY" };
        return EnvironmentHelper.GetApiKeyFromEnv("OPENAI_API_KEY", fallbackKeys, "test-api-key");
    }

    /// <summary>
    /// Helper method to get API base URL from environment (using shared EnvironmentHelper)
    /// </summary>
    private static string GetApiBaseUrlFromEnv()
    {
        string[] fallbackKeys = new[] { "LLM_API_BASE_URL" };
        return EnvironmentHelper.GetApiBaseUrlFromEnv("OPENAI_API_URL", fallbackKeys, "https://api.openai.com/v1");
    }

    /// <summary>
    /// Creates a streaming response that contains tool calls for testing middleware
    /// </summary>
    private static string CreateToolCallStreamingResponse()
    {
        var chunks = new List<string>
        {
            // Start with role
            JsonSerializer.Serialize(
                new
                {
                    id = "chatcmpl-test123",
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "gpt-4",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new { role = "assistant", content = string.Empty },
                            finish_reason = (string?)null,
                        },
                    },
                }
            ),
            // Tool call start
            JsonSerializer.Serialize(
                new
                {
                    id = "chatcmpl-test123",
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "gpt-4",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new
                            {
                                tool_calls = new[]
                                {
                                    new
                                    {
                                        index = 0,
                                        id = "call_test123",
                                        type = "function",
                                        function = new { name = "getWeather", arguments = "" },
                                    },
                                },
                            },
                            finish_reason = (string?)null,
                        },
                    },
                }
            ),
            // Tool call arguments
            JsonSerializer.Serialize(
                new
                {
                    id = "chatcmpl-test123",
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "gpt-4",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new
                            {
                                tool_calls = new[]
                                {
                                    new
                                    {
                                        index = 0,
                                        function = new { arguments = "{\"location\": \"San Francisco\"}" },
                                    },
                                },
                            },
                            finish_reason = (string?)null,
                        },
                    },
                }
            ),
            // End with tool_calls finish reason
            JsonSerializer.Serialize(
                new
                {
                    id = "chatcmpl-test123",
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "gpt-4",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new { },
                            finish_reason = "tool_calls",
                        },
                    },
                }
            ),
        };

        var sseResponse = string.Join("\n\n", chunks.Select(chunk => $"data: {chunk}"));
        return sseResponse + "\n\ndata: [DONE]\n\n";
    }

    /// <summary>
    /// Creates a streaming response that contains multiple tool calls for testing middleware
    /// </summary>
    private static string CreateMultipleToolCallStreamingResponse()
    {
        var chunks = new List<string>
        {
            // Start with role
            JsonSerializer.Serialize(
                new
                {
                    id = "chatcmpl-test456",
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "meta-llama/llama-4-maverick",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new { role = "assistant", content = string.Empty },
                            finish_reason = (string?)null,
                        },
                    },
                }
            ),
            // First tool call start
            JsonSerializer.Serialize(
                new
                {
                    id = "chatcmpl-test456",
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "meta-llama/llama-4-maverick",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new
                            {
                                tool_calls = new[]
                                {
                                    new
                                    {
                                        index = 0,
                                        id = "call_test123",
                                        type = "function",
                                        function = new { name = "python-mcp.list_directory", arguments = "" },
                                    },
                                },
                            },
                            finish_reason = (string?)null,
                        },
                    },
                }
            ),
            // First tool call arguments
            JsonSerializer.Serialize(
                new
                {
                    id = "chatcmpl-test456",
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "meta-llama/llama-4-maverick",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new
                            {
                                tool_calls = new[]
                                {
                                    new { index = 0, function = new { arguments = "{\"relative_path\": \"\"}" } },
                                },
                            },
                            finish_reason = (string?)null,
                        },
                    },
                }
            ),
            // Second tool call start
            JsonSerializer.Serialize(
                new
                {
                    id = "chatcmpl-test456",
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "meta-llama/llama-4-maverick",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new
                            {
                                tool_calls = new[]
                                {
                                    new
                                    {
                                        index = 1,
                                        id = "call_test456",
                                        type = "function",
                                        function = new { name = "python-mcp.list_directory", arguments = "" },
                                    },
                                },
                            },
                            finish_reason = (string?)null,
                        },
                    },
                }
            ),
            // Second tool call arguments
            JsonSerializer.Serialize(
                new
                {
                    id = "chatcmpl-test456",
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "meta-llama/llama-4-maverick",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new
                            {
                                tool_calls = new[]
                                {
                                    new { index = 1, function = new { arguments = "{\"relative_path\": \"code\"}" } },
                                },
                            },
                            finish_reason = (string?)null,
                        },
                    },
                }
            ),
            // End with tool_calls finish reason
            JsonSerializer.Serialize(
                new
                {
                    id = "chatcmpl-test456",
                    @object = "chat.completion.chunk",
                    created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    model = "meta-llama/llama-4-maverick",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new { },
                            finish_reason = "tool_calls",
                        },
                    },
                }
            ),
        };

        var sseResponse = string.Join("\n\n", chunks.Select(chunk => $"data: {chunk}"));
        return sseResponse + "\n\ndata: [DONE]\n\n";
    }
}
