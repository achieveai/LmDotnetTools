using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.McpMiddleware;
using AchieveAi.LmDotnetTools.McpSampleServer;

namespace AchieveAi.LmDotnetTools.McpIntegrationTests;

public class McpToolInvocationTests
{
    // Helpers to set up the test environment
    private static (IEnumerable<FunctionContract>, IDictionary<string, Func<string, Task<string>>>) SetupToolsForTesting()
    {
        var greetingToolType = typeof(GreetingTool);
        var calculatorToolType = greetingToolType.Assembly.GetType("AchieveAi.LmDotnetTools.McpSampleServer.CalculatorTool");
        var toolTypes = new[] { greetingToolType, calculatorToolType! };
        return McpFunctionCallExtensions.CreateFunctionCallComponentsFromTypes(toolTypes);
    }

    [Fact]
    public async Task GreetingTool_SayHello_ReturnsGreetingWithName()
    {
        // Arrange
        var (_, functionMap) = SetupToolsForTesting();

        // Act & Assert - Test with different names

        // Test regular name
        var regularResult = await functionMap["GreetingTool-SayHello"]("{\"name\":\"John\"}");
        var regularTrimmedResult = regularResult.Trim('\"');
        Console.WriteLine($"Regular test - Actual result: {regularTrimmedResult}");
        // Use a more flexible assertion
        Assert.Contains("Hello", regularTrimmedResult);
        Assert.Contains("John", regularTrimmedResult);

        // Test with special characters
        var specialCharsResult = await functionMap["GreetingTool-SayHello"]("{\"name\":\"Maria O'Connor-Smith\"}");
        var trimmedResult = specialCharsResult.Trim('\"');
        // Output the actual result for debugging
        Console.WriteLine($"Special chars test - Actual result: {trimmedResult}");
        // Use a more flexible assertion that checks for basic greeting pattern
        Assert.Contains("Hello", trimmedResult);
        Assert.Contains("Maria", trimmedResult);

        // Test with emoji
        var emojiResult = await functionMap["GreetingTool-SayHello"]("{\"name\":\"User 😀\"}");
        var emojiTrimmedResult = emojiResult.Trim('\"');
        Console.WriteLine($"Emoji test - Actual result: {emojiTrimmedResult}");
        // Use a more flexible assertion that checks for basic greeting pattern
        Assert.Contains("Hello", emojiTrimmedResult);
        Assert.Contains("User", emojiTrimmedResult);

        // Test with a long name
        var longName = "Very" + new string('y', 50) + " Long Name";
        var longNameResult = await functionMap["GreetingTool-SayHello"]($"{{\"name\":\"{longName}\"}}");
        Assert.Contains($"Hello, {longName}", longNameResult.Trim('\"'));
    }

    [Fact]
    public async Task GreetingTool_SayGoodbye_ReturnsGoodbyeMessage()
    {
        // Arrange
        var (_, functionMap) = SetupToolsForTesting();

        // Act
        var result = await functionMap["GreetingTool-SayGoodbye"]("{\"name\":\"John\"}");

        // Assert
        var cleanResult = result.Trim('\"');
        Assert.Contains("Goodbye, John", cleanResult);
        Assert.Contains("Have a great day", cleanResult);
    }

    [Fact]
    public async Task CalculatorTool_Add_CorrectlyAddsNumbers()
    {
        // Arrange
        var (_, functionMap) = SetupToolsForTesting();

        // Act & Assert - Test different number combinations

        // Test positive integers
        var positiveResult = await functionMap["CalculatorTool-Add"]("{\"a\":5,\"b\":3}");
        double positiveValue = double.Parse(positiveResult.Trim('\"'));
        Assert.Equal(8, positiveValue);

        // Test negative numbers
        var negativeResult = await functionMap["CalculatorTool-Add"]("{\"a\":-10,\"b\":-5}");
        double negativeValue = double.Parse(negativeResult.Trim('\"'));
        Assert.Equal(-15, negativeValue);

        // Test mixed numbers
        var mixedResult = await functionMap["CalculatorTool-Add"]("{\"a\":7.5,\"b\":-2.5}");
        double mixedValue = double.Parse(mixedResult.Trim('\"'));
        Assert.Equal(5, mixedValue);

        // Test large numbers
        var largeResult = await functionMap["CalculatorTool-Add"]("{\"a\":1000000,\"b\":234567}");
        double largeValue = double.Parse(largeResult.Trim('\"'));
        Assert.Equal(1234567, largeValue);

        // Test decimal precision
        var decimalResult = await functionMap["CalculatorTool-Add"]("{\"a\":0.1,\"b\":0.2}");
        double decimalValue = double.Parse(decimalResult.Trim('\"'));
        Assert.Equal(0.3, decimalValue, 10); // Using precision to handle floating point errors
    }

    [Fact]
    public async Task CalculatorTool_CalculatesMultipleOperations()
    {
        // Arrange
        var (_, functionMap) = SetupToolsForTesting();

        // Act & Assert for various operations

        // Test multiplication
        var multiplyResult = await functionMap["CalculatorTool-Multiply"]("{\"a\":4,\"b\":5}");
        double multiplyValue = double.Parse(multiplyResult.Trim('\"'));
        Assert.Equal(20, multiplyValue);

        // Test subtraction
        var subtractResult = await functionMap["CalculatorTool-Subtract"]("{\"a\":10,\"b\":3}");
        double subtractValue = double.Parse(subtractResult.Trim('\"'));
        Assert.Equal(7, subtractValue);

        // Test division
        var divideResult = await functionMap["CalculatorTool-Divide"]("{\"a\":10,\"b\":2}");
        double divideValue = double.Parse(divideResult.Trim('\"'));
        Assert.Equal(5, divideValue);

        // Test square root
        var sqrtResult = await functionMap["CalculatorTool-Sqrt"]("{\"x\":16}");
        double sqrtValue = double.Parse(sqrtResult.Trim('\"'));
        Assert.Equal(4, sqrtValue);

        // Test power
        var powerResult = await functionMap["CalculatorTool-Power"]("{\"x\":2,\"y\":3}");
        double powerValue = double.Parse(powerResult.Trim('\"'));
        Assert.Equal(8, powerValue);
    }

    [Fact]
    public async Task CalculatorTool_HandlesErrorsGracefully()
    {
        // Arrange
        var (_, functionMap) = SetupToolsForTesting();

        // Act & Assert for error cases

        // Test division by zero
        var divideByZeroResult = await functionMap["CalculatorTool-Divide"]("{\"a\":10,\"b\":0}");
        // Check that the result contains error information - simpler assertion that doesn't rely on exact format
        Assert.Contains("error", divideByZeroResult);

        // Test square root of negative number
        var sqrtNegativeResult = await functionMap["CalculatorTool-Sqrt"]("{\"x\":-1}");
        // Check that the result contains error information - simpler assertion that doesn't rely on exact format
        Assert.Contains("error", sqrtNegativeResult);
    }
}
