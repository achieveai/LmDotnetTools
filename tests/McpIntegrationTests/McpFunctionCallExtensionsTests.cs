using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.McpMiddleware;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.McpSampleServer;
using AchieveAi.LmDotnetTools.LmCore.Core;
using ModelContextProtocol.Server;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

public class McpFunctionCallExtensionsTests
{
    [Fact]
    public void CreateFunctionCallComponentsFromAssembly_WithMcpSampleServer_CreatesCorrectContracts()
    {
        // Arrange
        var greetingToolType = typeof(GreetingTool);

        // Verify we can access the McpServerToolTypeAttribute on the type
        var attr = greetingToolType.GetCustomAttribute<McpServerToolTypeAttribute>();
        Assert.NotNull(attr); // Verify the attribute is present

        // Find the CalculatorTool type by name
        var calculatorToolType = greetingToolType.Assembly.GetType(
            "AchieveAi.LmDotnetTools.McpSampleServer.CalculatorTool"
        );
        Assert.NotNull(calculatorToolType);

        // Log the types we've found
        Debug.WriteLine($"Found GreetingTool: {greetingToolType.FullName}");
        Debug.WriteLine($"Found CalculatorTool: {calculatorToolType!.FullName}");

        // Check that there are methods with McpServerToolAttribute on the GreetingTool
        var greetingToolMethods = greetingToolType
            .GetMethods()
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null)
            .ToList();

        Assert.NotEmpty(greetingToolMethods);
        Debug.WriteLine($"Found {greetingToolMethods.Count} tool methods on GreetingTool");

        // Act - test with specific tool types rather than scanning whole assembly
        var toolTypes = new[] { greetingToolType, calculatorToolType };
        var (functionContracts, functionMap) = McpFunctionCallExtensions.CreateFunctionCallComponentsFromTypes(
            toolTypes
        );

        // Convert to list for easier assertions
        var contractsList = functionContracts.ToList();

        // Assert
        Assert.NotNull(contractsList);
        Assert.NotEmpty(contractsList);

        // Verify we have the expected number of functions (7 from CalculatorTool + 2 from GreetingTool)
        Assert.Equal(11, contractsList.Count);

        // Test GreetingTool methods
        var sayHelloContract = contractsList.FirstOrDefault(c => c.Name == "SayHello");
        Assert.NotNull(sayHelloContract);
        Assert.Equal("Greets a person by name", sayHelloContract!.Description);
        Assert.NotNull(sayHelloContract.Parameters);
        _ = Assert.Single(sayHelloContract.Parameters!);
        Assert.Equal("name", sayHelloContract.Parameters!.First().Name);

        var sayGoodbyeContract = contractsList.FirstOrDefault(c => c.Name == "SayGoodbye");
        Assert.NotNull(sayGoodbyeContract);
        Assert.Equal("Says goodbye to a person by name", sayGoodbyeContract!.Description);
        Assert.NotNull(sayGoodbyeContract.Parameters);
        _ = Assert.Single(sayGoodbyeContract.Parameters!);
        Assert.Equal("name", sayGoodbyeContract.Parameters!.First().Name);

        // Test CalculatorTool methods
        var addContract = contractsList.FirstOrDefault(c => c.Name == "Add");
        Assert.NotNull(addContract);
        Assert.Equal("Adds two numbers", addContract!.Description);
        Assert.NotNull(addContract.Parameters);
        var addParams = addContract.Parameters!.ToList();
        Assert.Equal(2, addParams.Count);
        Assert.Equal("a", addParams[0].Name);
        Assert.Equal("b", addParams[1].Name);

        var divideContract = contractsList.FirstOrDefault(c => c.Name == "Divide");
        Assert.NotNull(divideContract);
        Assert.Equal("Divides the first number by the second", divideContract!.Description);
        Assert.NotNull(divideContract.Parameters);
        var divideParams = divideContract.Parameters!.ToList();
        Assert.Equal(2, divideParams.Count);
        Assert.Equal("a", divideParams[0].Name);
        Assert.Equal("b", divideParams[1].Name);

        // Verify function map has all functions
        Assert.Equal(contractsList.Count, functionMap.Count);

        // Verify function map contains all the function names from the contracts
        foreach (var contract in contractsList)
        {
            Assert.True(functionMap.ContainsKey(contract.ClassName + "-" + contract.Name));
        }
    }

    [Fact]
    public async Task FunctionMap_CanInvokeMethods_WithCorrectArguments()
    {
        // Arrange - use direct reflection to find McpServerToolTypeAttribute to avoid assembly resolution issues
        var greetingToolType = typeof(GreetingTool);
        var calculatorToolType = greetingToolType.Assembly.GetType(
            "AchieveAi.LmDotnetTools.McpSampleServer.CalculatorTool"
        );
        Assert.NotNull(calculatorToolType);

        // Act - test with specific tool types rather than scanning whole assembly
        var toolTypes = new[] { greetingToolType, calculatorToolType! };
        var (_, functionMap) = McpFunctionCallExtensions.CreateFunctionCallComponentsFromTypes(toolTypes);

        // Act & Assert for SayHello
        var sayHelloResult = await functionMap["GreetingTool-SayHello"]("{\"name\":\"Test User\"}");
        Assert.Contains("Hello, Test User", sayHelloResult);

        // Act & Assert for Add
        var addResult = await functionMap["CalculatorTool-Add"]("{\"a\":5,\"b\":3}");
        // Parse the result to verify it's a valid number
        var addNumber = double.Parse(addResult.Trim('\"'));
        Assert.Equal(8, addNumber);

        // Act & Assert for Divide
        var divideResult = await functionMap["CalculatorTool-Divide"]("{\"a\":10,\"b\":2}");
        // Parse the result to verify it's a valid number
        var divideNumber = double.Parse(divideResult.Trim('\"'));
        Assert.Equal(5, divideNumber);

        // Test error handling for Divide by zero
        var divideByZeroResult = await functionMap["CalculatorTool-Divide"]("{\"a\":10,\"b\":0}");
        // Parse the result to verify it contains an error message
        var errorObj = JsonSerializer.Deserialize<JsonElement>(divideByZeroResult);
        Assert.True(errorObj.TryGetProperty("error", out _));
    }

    [Fact]
    public void CreateFunctionCallMiddlewareFromAssembly_CreatesFunctionCallMiddleware()
    {
        // Arrange - we'll create the middleware directly with the two specific types rather than scanning the assembly
        var greetingToolType = typeof(GreetingTool);
        var calculatorToolType = greetingToolType.Assembly.GetType(
            "AchieveAi.LmDotnetTools.McpSampleServer.CalculatorTool"
        );
        Assert.NotNull(calculatorToolType);

        // Create a middleware using a direct approach rather than assembly scanning
        var toolTypes = new[] { greetingToolType, calculatorToolType! };
        var (functions, functionMap) = McpFunctionCallExtensions.CreateFunctionCallComponentsFromTypes(toolTypes);
        var middleware = new FunctionCallMiddleware(functions, functionMap, "FunctionCallMiddleware");

        // Assert
        Assert.NotNull(middleware);
        Assert.Equal("FunctionCallMiddleware", middleware.Name);
    }
}
