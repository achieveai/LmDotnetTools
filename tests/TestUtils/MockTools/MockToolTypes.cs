namespace AchieveAi.LmDotnetTools.TestUtils.MockTools;

/// <summary>
/// Utility class providing easy access to mock tool types
/// </summary>
public static class MockToolTypes
{
    /// <summary>
    /// All mock tool types defined in this assembly
    /// </summary>
    public static readonly IReadOnlyList<Type> All = new[]
    {
        typeof(MockGreetingTool),
        typeof(MockCalculatorTool),
        typeof(MockPythonExecutionTool),
        typeof(MockWeatherTool)
    };

    /// <summary>
    /// Mock tool types from C# Program.cs sample
    /// </summary>
    public static readonly IReadOnlyList<Type> CSharpSample = new[]
    {
        typeof(MockGreetingTool),
        typeof(MockCalculatorTool)
    };

    /// <summary>
    /// Mock tool types from Python server.py sample
    /// </summary>
    public static readonly IReadOnlyList<Type> PythonSample = new[]
    {
        typeof(MockPythonExecutionTool)
    };

    /// <summary>
    /// Weather tool types
    /// </summary>
    public static readonly IReadOnlyList<Type> Weather = new[]
    {
        typeof(MockWeatherTool)
    };
}