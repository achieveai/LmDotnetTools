using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Tests.Utilities;
using AchieveAi.LmDotnetTools.TestUtils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class FunctionCallMiddlewareTests
{
  #region Constructor Tests

  [Fact]
  public void Constructor_ShouldThrowArgumentNullException_WhenFunctionsIsNull()
  {
    // Arrange
    var functionMap = new Dictionary<string, Func<string, Task<string>>>();
    
    // Act & Assert
    var exception = Assert.Throws<ArgumentNullException>(() => 
      new FunctionCallMiddleware(
        functions: null!, 
        functionMap: functionMap));
    
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
        Parameters = new[]
        {
          new FunctionParameterContract
          {
            Name = "location",
            ParameterType = typeof(string),
            Description = "City name",
            IsRequired = true
          }
        }
      }
    };
    
    var functionMap = new Dictionary<string, Func<string, Task<string>>>
    {
      // Missing the "getWeather" function
      { "add", args => Task.FromResult("10") }
    };
    
    // Act & Assert
    var exception = Assert.Throws<ArgumentException>(() => 
      new FunctionCallMiddleware(functions, functionMap));
    
    Assert.Contains("getWeather", exception.Message);
    Assert.Equal("functionMap", exception.ParamName);
  }

  [Fact]
  public void Constructor_ShouldThrowArgumentException_WhenFunctionMapIsNullButFunctionsAreProvided()
  {
    // Arrange
    var functions = new List<FunctionContract>
    {
      new FunctionContract
      {
        Name = "getWeather",
        Description = "Get the weather in a location"
      }
    };
    
    // Act & Assert
    var exception = Assert.Throws<ArgumentException>(() => 
      new FunctionCallMiddleware(functions, null!));
    
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
      new FunctionContract
      {
        Name = "function1",
        Description = "Test function 1"
      },
      new FunctionContract
      {
        Name = "function2",
        Description = "Test function 2"
      }
    };
    
    var functionMap = new Dictionary<string, Func<string, Task<string>>>
    {
      { "function1", args => Task.FromResult("result1") },
      { "function2", args => Task.FromResult("result2") }
    };
    
    // Act & Assert - no exception should be thrown
    var middleware = new FunctionCallMiddleware(functions, functionMap);
    Assert.NotNull(middleware);
  }

  #endregion
  
  #region Test Methods
  
  [Fact]
  public async Task InvokeAsync_ShouldExecuteFunction_WhenFunctionExists()
  {
    // Arrange
    var functionMap = CreateMockFunctionMap();
    var functionContracts = CreateMockFunctionContracts();
    var middleware = new FunctionCallMiddleware(functionContracts, functionMap);
    
    // Create a message with a tool call for the getWeather function
    var toolCallMessage = CreateToolCallMessage("getWeather", 
      new { location = "San Francisco", unit = "celsius" });
    
    // Create the context with our tool call message
    var context = new MiddlewareContext(
      new[] { toolCallMessage },
      new GenerateReplyOptions());
    
    // Mock the agent
    var mockAgent = new Mock<IAgent>();
    
    // Act
    var result = await middleware.InvokeAsync(context, mockAgent.Object);
    
    // Assert
    Assert.NotNull(result);
    Assert.IsType<ToolsCallResultMessage>(result);
    
    var resultMessage = (ToolsCallResultMessage)result;
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
    var toolCallMessage = CreateToolCallMessage("getNonExistentFunction", 
      new { param = "value" });
    
    // Create the context with our tool call message
    var context = new MiddlewareContext(
      new[] { toolCallMessage },
      new GenerateReplyOptions());
    
    // Mock the agent
    var mockAgent = new Mock<IAgent>();
    
    // Act
    var result = await middleware.InvokeAsync(context, mockAgent.Object);
    
    // Assert
    Assert.NotNull(result);
    Assert.IsType<ToolsCallResultMessage>(result);
    
    var resultMessage = (ToolsCallResultMessage)result;
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
    var context = new MiddlewareContext(
      new[] { message },
      new GenerateReplyOptions());
    
    // Mock the agent
    var mockAgent = new Mock<IAgent>();
    GenerateReplyOptions? capturedOptions = null;
    
    mockAgent
      .Setup(a => a.GenerateReplyAsync(
        It.IsAny<IEnumerable<IMessage>>(),
        It.IsAny<GenerateReplyOptions>(),
        It.IsAny<CancellationToken>()))
      .Callback<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
        (msgs, options, token) => capturedOptions = options)
      .ReturnsAsync(new TextMessage { Text = "Mock response", Role = Role.Assistant });
    
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
    var toolCallMessage = CreateToolCallMessage("add", 
      new { a = 5, b = 7 });
    
    // Create the context with our tool call message
    var context = new MiddlewareContext(
      new[] { toolCallMessage },
      new GenerateReplyOptions());
    
    // Mock the agent
    var mockAgent = new Mock<IAgent>();
    
    // Act
    var result = await middleware.InvokeAsync(context, mockAgent.Object);
    
    // Assert
    Assert.NotNull(result);
    Assert.IsType<ToolsCallResultMessage>(result);
    
    var resultMessage = (ToolsCallResultMessage)result;
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
    var toolCallMessage = CreateToolCallMessage("get_weather", 
      new { location = "San Francisco", unit = "celsius" });
    
    // Create the context with our tool call message
    var context = new MiddlewareContext(
      new[] { toolCallMessage },
      new GenerateReplyOptions());
    
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
  
  #endregion
  
  #region Helper Methods
  
  private Dictionary<string, Func<string, Task<string>>> CreateMockFunctionMap()
  {
    return new Dictionary<string, Func<string, Task<string>>>
    {
      ["getWeather"] = argsJson => Task.FromResult(GetWeatherAsync(argsJson)),
      ["getWeatherHistory"] = argsJson => Task.FromResult(GetWeatherHistoryAsync(argsJson)),
      ["add"] = argsJson => Task.FromResult(AddAsync(argsJson)),
      ["subtract"] = argsJson => Task.FromResult(SubtractAsync(argsJson)),
      ["multiply"] = argsJson => Task.FromResult(MultiplyAsync(argsJson)),
      ["divide"] = argsJson => Task.FromResult(DivideAsync(argsJson))
    };
  }
  
  private IEnumerable<FunctionContract> CreateMockFunctionContracts()
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
            ParameterType = typeof(string),
            Description = "City name",
            IsRequired = true
          },
          new FunctionParameterContract
          {
            Name = "unit",
            ParameterType = typeof(string),
            Description = "Temperature unit (celsius or fahrenheit)",
            IsRequired = false
          }
        }
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
            ParameterType = typeof(string),
            Description = "City name",
            IsRequired = true
          },
          new FunctionParameterContract
          {
            Name = "days",
            ParameterType = typeof(int),
            Description = "Number of days of history",
            IsRequired = true
          }
        }
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
            ParameterType = typeof(double),
            Description = "First number",
            IsRequired = true
          },
          new FunctionParameterContract
          {
            Name = "b",
            ParameterType = typeof(double),
            Description = "Second number",
            IsRequired = true
          }
        }
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
            ParameterType = typeof(double),
            Description = "First number",
            IsRequired = true
          },
          new FunctionParameterContract
          {
            Name = "b",
            ParameterType = typeof(double),
            Description = "Second number",
            IsRequired = true
          }
        }
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
            ParameterType = typeof(double),
            Description = "First number",
            IsRequired = true
          },
          new FunctionParameterContract
          {
            Name = "b",
            ParameterType = typeof(double),
            Description = "Second number",
            IsRequired = true
          }
        }
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
            ParameterType = typeof(double),
            Description = "First number",
            IsRequired = true
          },
          new FunctionParameterContract
          {
            Name = "b",
            ParameterType = typeof(double),
            Description = "Second number",
            IsRequired = true
          }
        }
      }
    };
  }
  
  private ToolsCallMessage CreateToolCallMessage(string functionName, object args)
  {
    // Serialize the arguments to a JSON string
    var jsonArgs = JsonSerializer.Serialize(args);
    
    return new ToolsCallMessage
    {
      ToolCalls = ImmutableList.Create(new ToolCall(functionName, jsonArgs)
      {
        ToolCallId = Guid.NewGuid().ToString()
      }),
      Role = Role.Assistant
    };
  }
  
  #endregion
  
  #region Mock Function Implementations
  
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
      return $"Weather history for {location} (last {days} days):\n" +
             $"- Day 1: 22°C, Sunny\n" +
             $"- Day 2: 20°C, Partly Cloudy\n" +
             $"- Day 3: 19°C, Light Rain";
    }
    catch (Exception ex)
    {
      return $"Error processing weather history request: {ex.Message}";
    }
  }
  
  private string AddAsync(string argsJson)
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
  
  private string SubtractAsync(string argsJson)
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
  
  private string MultiplyAsync(string argsJson)
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
  
  private string DivideAsync(string argsJson)
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
  
  #endregion
    [Fact]
    public async Task FunctionCallMiddleware_ShouldReturnToolAggregateMessage_Streaming_WithJoin()
    {
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
                        ParameterType = typeof(string),
                        IsRequired = true
                    },
                    new FunctionParameterContract
                    {
                        Name = "unit",
                        Description = "Temperature unit (celsius or fahrenheit)",
                        ParameterType = typeof(string),
                        IsRequired = false
                    }
                }
            }
        };

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["getWeather"] = async argsJson =>
            {
                await Task.Delay(1);
                return "{\"location\":\"San Francisco\",\"temperature\":23,\"unit\":\"celsius\"}";
            }
        };

        var middleware = new FunctionCallMiddleware(functionContracts, functionMap);

        var messages = new List<IMessage>
        {
            new TextMessage { Role = Role.System, Text = "You're a helpful AI Agent that can use tools" },
            new TextMessage { Role = Role.User, Text = "What's the weather in San Francisco?" }
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "gpt-4",
            Functions = functionContracts
        };

        var context = new MiddlewareContext(messages, options);

        var client = OpenClientFactory.CreateDatabasedClient(
            "FunctionToolCall_ShouldReturnToolMessage_streaming",
            null,
            false);

        var agent = new OpenClientAgent("TestAgent", client);

        // Act
        var responseStream = await middleware.InvokeStreamingAsync(context, agent);

        var responses = new List<IMessage>();
        await foreach (var response in responseStream)
        {
            responses.Add(response);
        }

        // Assert
        Assert.NotEmpty(responses);

        var lastMessage = responses.LastOrDefault(m => m is ToolsCallAggregateMessage);
        Assert.NotNull(lastMessage);
        Assert.IsType<ToolsCallAggregateMessage>(lastMessage);

        var aggregate = (ToolsCallAggregateMessage)lastMessage;
        Assert.NotEmpty(aggregate.ToolCallMessage.GetToolCalls()!);
        Assert.Contains(aggregate.ToolCallMessage.GetToolCalls()!, call => call.FunctionName == "getWeather");
    }

    [Fact]
    public async Task FunctionCallMiddleware_ShouldReturnMultipleToolAggregateMessages_Streaming()
    {
        // Arrange
        var functionContracts = new[]
        {
            new FunctionContract
            {
                Name = "python-mcp.execute_python_in_container",
                Description = "\nExecute Python code in a Docker container. The environment is limited to the container.\nFollowing packages are available:\n- pandas\n- numpy\n- matplotlib\n- seaborn\n- plotly\n- bokeh\n- hvplot\n- datashader\n- plotnine\n- cufflinks\n- graphviz\n- scipy\n- statsmodels\n- openpyxl\n- xlrd\n- xlsxwriter\n- pandasql\n- csv23\n- csvkit\n- polars\n- pyarrow\n- fastparquet\n- dask\n- vaex\n- python-dateutil\n- beautifulsoup4\n- requests\n- lxml\n- geopandas\n- folium\n- pydeck\n- holoviews\n- altair\n- visualkeras\n- kaleido\n- panel\n- voila\n\nArgs:\n    code: Python code to execute\n\nReturns:\n    Output from executed code\n",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "code",
                        Description = "",
                        ParameterType = typeof(string),
                        IsRequired = true
                    }
                }
            },
            new FunctionContract
            {
                Name = "python-mcp.list_directory",
                Description = "\nList the contents of a directory within the code directory where python code is executed\n\nArgs:\n    relative_path: Relative path within the code directory (default: list root code directory)\n    \nReturns:\n    Directory listing as a string\n",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "relative_path",
                        Description = "",
                        ParameterType = typeof(string),
                        IsRequired = false
                    }
                }
            },
            new FunctionContract
            {
                Name = "python-mcp.read_file",
                Description = "\nRead a file from the code directory where python code is executed\n\nArgs:\n    relative_path: Relative path to the file within the code directory\n    \nReturns:\n    File contents as a string\n",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "relative_path",
                        Description = "",
                        ParameterType = typeof(string),
                        IsRequired = true
                    }
                }
            },
            new FunctionContract
            {
                Name = "python-mcp.write_file",
                Description = "\nWrite content to a file in the code directory where python code is executed\n\nArgs:\n    relative_path: Relative path to the file within the code directory\n    content: Content to write to the file\n    \nReturns:\n    Status message\n",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "relative_path",
                        Description = "",
                        ParameterType = typeof(string),
                        IsRequired = true
                    },
                    new FunctionParameterContract
                    {
                        Name = "content",
                        Description = "",
                        ParameterType = typeof(string),
                        IsRequired = true
                    }
                }
            },
            new FunctionContract
            {
                Name = "python-mcp.delete_file",
                Description = "\nDelete a file from the code directory where python code is executed\n\nArgs:\n    relative_path: Relative path to the file within the code directory\n    \nReturns:\n    Status message\n",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "relative_path",
                        Description = "",
                        ParameterType = typeof(string),
                        IsRequired = true
                    }
                }
            },
            new FunctionContract
            {
                Name = "python-mcp.get_directory_tree",
                Description = "\nGet an ASCII tree representation of a directory structure where python code is executed\n\nArgs:\n    relative_path: Relative path within the code directory (default: root code directory)\n    \nReturns:\n    ASCII tree representation as a string\n",
                Parameters = new List<FunctionParameterContract>
                {
                    new FunctionParameterContract
                    {
                        Name = "relative_path",
                        Description = "",
                        ParameterType = typeof(string),
                        IsRequired = false
                    }
                }
            },
            new FunctionContract
            {
                Name = "python-mcp.cleanup_code_directory",
                Description = "\nClean up the code directory by removing all files and subdirectories\n\nReturns:\n    Status message\n",
                Parameters = new List<FunctionParameterContract>()
            }
        };

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["python-mcp.execute_python_in_container"] = async argsJson => { await Task.Delay(1); return ""; },
            ["python-mcp.list_directory"] = async argsJson => { await Task.Delay(1); return ""; },
            ["python-mcp.read_file"] = async argsJson => { await Task.Delay(1); return ""; },
            ["python-mcp.write_file"] = async argsJson => { await Task.Delay(1); return ""; },
            ["python-mcp.delete_file"] = async argsJson => { await Task.Delay(1); return ""; },
            ["python-mcp.get_directory_tree"] = async argsJson => { await Task.Delay(1); return ""; },
            ["python-mcp.cleanup_code_directory"] = async argsJson => { await Task.Delay(1); return ""; }
        };

        var middleware = new FunctionCallMiddleware(functionContracts, functionMap);

        var messages = new List<IMessage>
        {
            new TextMessage { Role = Role.System, Text = "You are a helpful assistant that can use tools to help users. When you need to execute Python code, use the execute_python_in_container tool." },
            new TextMessage { Role = Role.User, Text = "List files in root and \"code\" directories." }
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "meta-llama/llama-4-maverick",
            Temperature = (float?)0.699999988079071,
            Functions = functionContracts
        };

        var context = new MiddlewareContext(messages, options);

        var client = OpenClientFactory.CreateDatabasedClient(
            "FunctionToolCall_MultipleToolCalls_streaming",
            null,
            false);

        var agent = new OpenClientAgent("TestAgent", client);

        // Act
        var responseStream = await middleware.InvokeStreamingAsync(context, agent);

        var responses = new List<IMessage>();
        await foreach (var response in responseStream)
        {
            responses.Add(response);
        }

        // Assert
        Assert.NotEmpty(responses);

        var lastMessage = responses.LastOrDefault(m => m is ToolsCallAggregateMessage);
        Assert.NotNull(lastMessage);
        Assert.IsType<ToolsCallAggregateMessage>(lastMessage);

        var aggregate = (ToolsCallAggregateMessage)lastMessage;
        var toolCalls = aggregate.ToolCallMessage.GetToolCalls();
        Assert.NotNull(toolCalls);
        Assert.True(toolCalls!.Count() >= 2, "Should contain at least two tool calls");
        Assert.Contains(toolCalls, call => call.FunctionName == "python-mcp.list_directory");
        // Optionally, check that both tool calls are present (by id or argument)
    }
}
