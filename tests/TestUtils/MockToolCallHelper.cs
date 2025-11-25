using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.McpMiddleware;

namespace AchieveAi.LmDotnetTools.TestUtils;

/// <summary>
///     Helper class for creating mock tool calls for testing purposes
/// </summary>
public static class MockToolCallHelper
{
    /// <summary>
    ///     Creates function contracts and function map from mock tool call classes
    /// </summary>
    /// <param name="mockToolCallTypes">Types of mock tool call classes</param>
    /// <param name="callbackOverrides">Optional dictionary of function name to callback override</param>
    /// <returns>Tuple of function contracts and function map</returns>
    public static (IEnumerable<FunctionContract>, IDictionary<string, Func<string, Task<string>>>) CreateMockToolCalls(
        IEnumerable<Type> mockToolCallTypes,
        IDictionary<string, Func<string, Task<string>>>? callbackOverrides = null
    )
    {
        // Use McpFunctionCallExtensions to extract function contracts and maps
        var (functionContracts, functionMap) = McpFunctionCallExtensions.CreateFunctionCallComponentsFromTypes(
            mockToolCallTypes
        );

        // Apply overrides if provided
        if (callbackOverrides != null)
        {
            foreach (var contract in functionContracts)
            {
                var fullName = $"{contract.ClassName}-{contract.Name}";
                if (callbackOverrides.TryGetValue(fullName, out var callback))
                {
                    functionMap[fullName] = callback;
                }
            }
        }

        return (functionContracts, functionMap);
    }
}
