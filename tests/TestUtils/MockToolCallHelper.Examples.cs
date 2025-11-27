using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.TestUtils.MockTools;

namespace AchieveAi.LmDotnetTools.TestUtils;

/// <summary>
///     Examples of how to use the MockToolCallHelper
/// </summary>
public static class MockToolCallHelperExamples
{
    /// <summary>
    ///     Example showing how to create mock tool calls with all tools
    /// </summary>
    public static (IEnumerable<FunctionContract>, IDictionary<string, Func<string, Task<string>>>) CreateAllMockTools()
    {
        var callbackOverrides = new Dictionary<string, Func<string, Task<string>>>
        {
            // Override greeting tool methods
            ["MockGreetingTool-SayHello"] = argsJson =>
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                var name = args!["name"].GetString();
                return Task.FromResult($"Hello, {name}! This is a mock response.");
            },
            ["MockGreetingTool-SayGoodbye"] = argsJson =>
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                var name = args!["name"].GetString();
                return Task.FromResult($"Goodbye, {name}! Have a great day from the mock.");
            },

            // Override calculator method as an example
            ["MockCalculatorTool-Add"] = argsJson =>
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                var a = args!["a"].GetDouble();
                var b = args!["b"].GetDouble();
                return Task.FromResult((a + b).ToString());
            },

            // Override one Python tool method using correct Python method name
            ["MockPythonExecutionTool-execute_python_in_container"] = argsJson =>
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                var code = args!["code"].GetString();
                return Task.FromResult($"Mock Python execution output for code: {code}");
            },
        };

        return MockToolCallHelper.CreateMockToolCalls(MockToolTypes.All, callbackOverrides);
    }

    /// <summary>
    ///     Example showing how to create mock tool calls for C# sample only
    /// </summary>
    public static (
        IEnumerable<FunctionContract>,
        IDictionary<string, Func<string, Task<string>>>
    ) CreateCSharpMockTools()
    {
        return MockToolCallHelper.CreateMockToolCalls(MockToolTypes.CSharpSample);
    }

    /// <summary>
    ///     Example of creating a specific override for a calculator method
    /// </summary>
    public static (
        IEnumerable<FunctionContract>,
        IDictionary<string, Func<string, Task<string>>>
    ) CreateCalculatorMockWithDivideOverride()
    {
        var callbackOverrides = new Dictionary<string, Func<string, Task<string>>>
        {
            ["MockCalculatorTool-Divide"] = argsJson =>
            {
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argsJson);
                var a = args!["a"].GetDouble();
                var b = args!["b"].GetDouble();

                return b == 0
                    ? Task.FromResult(JsonSerializer.Serialize(new { error = "Cannot divide by zero" }))
                    : Task.FromResult((a / b).ToString());
            },
        };

        return MockToolCallHelper.CreateMockToolCalls([typeof(MockCalculatorTool)], callbackOverrides);
    }
}
