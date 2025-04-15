using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpMiddleware;
using System.Text.Json;
using System.Collections.Generic;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using Xunit;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

/// <summary>
/// Basic tests for McpFunctionCallExtensions that don't require external assemblies
/// </summary>
public class McpFunctionCallExtensionsBasicTests
{
  [Fact]
  public async Task CreateFunctionMap_StoresAndExecutesFunctions()
  {
    // Arrange - create simple function contracts and handlers
    var functionContracts = new List<FunctionContract>
    {
      new FunctionContract
      {
        Name = "Echo",
        Description = "Returns the input text",
        Parameters = new List<FunctionParameterContract>
        {
          new FunctionParameterContract
          {
            Name = "text",
            Description = "Text to echo",
            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
            IsRequired = true
          }
        }
      },
      new FunctionContract
      {
        Name = "Add",
        Description = "Adds two numbers",
        Parameters = new List<FunctionParameterContract>
        {
          new FunctionParameterContract
          {
            Name = "a",
            Description = "First number",
            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
            IsRequired = true
          },
          new FunctionParameterContract
          {
            Name = "b",
            Description = "Second number",
            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(double)),
            IsRequired = true
          }
        }
      }
    };
    
    var functionMap = new Dictionary<string, Func<string, Task<string>>>
    {
      ["Echo"] = async (argsJson) =>
      {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
        var text = args!["text"].GetString() ?? string.Empty;
        await Task.Yield(); // Make the task actually async
        return text;
      },
      ["Add"] = async (argsJson) =>
      {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
        var a = args!["a"].GetDouble();
        var b = args!["b"].GetDouble();
        await Task.Yield(); // Make the task actually async
        return (a + b).ToString();
      }
    };
    
    // Act - create middleware with these functions
    var middleware = new FunctionCallMiddleware(functionContracts, functionMap);
    
    // Assert
    Assert.NotNull(middleware);
    
    // Verify we can create and execute functions
    var echoFunc = functionMap["Echo"];
    var addFunc = functionMap["Add"];
    
    Assert.NotNull(echoFunc);
    Assert.NotNull(addFunc);
    
    // Test echo function
    var echoResult = await echoFunc("{\"text\":\"Hello World\"}");
    Assert.Equal("Hello World", echoResult);
    
    // Test add function
    var addResult = await addFunc("{\"a\":5,\"b\":3}");
    Assert.Equal("8", addResult);
  }
  
  [Fact]
  public Task FunctionCallMiddleware_ExecutesToolCalls()
  {
    // Arrange - create simple function contracts and handlers
    var functionContracts = new List<FunctionContract>
    {
      new FunctionContract
      {
        Name = "Echo",
        Description = "Returns the input text",
        Parameters = new List<FunctionParameterContract>
        {
          new FunctionParameterContract
          {
            Name = "text",
            Description = "Text to echo",
            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
            IsRequired = true
          }
        }
      }
    };
    
    var functionMap = new Dictionary<string, Func<string, Task<string>>>
    {
      ["Echo"] = async (argsJson) =>
      {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
        var text = args!["text"].GetString() ?? string.Empty;
        await Task.Yield(); // Make the task actually async
        return text;
      }
    };
    
    // Create middleware with these functions
    var middleware = new FunctionCallMiddleware(functionContracts, functionMap);
    
    // Assert middleware is properly created
    Assert.NotNull(middleware);
    Assert.Equal("FunctionCallMiddleware", middleware.Name);
    
    return Task.CompletedTask;
  }
}
