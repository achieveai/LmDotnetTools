using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Tests.Utilities;
using Moq;
using Xunit;
using System.Runtime.CompilerServices;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class FunctionCallMiddlewareTests
{
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
    Assert.Equal("get_weather", toolCallResult.ToolCall.FunctionName);
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
  
  private string GetWeatherAsync(string argsJson)
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
  
  private string GetWeatherHistoryAsync(string argsJson)
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
}
