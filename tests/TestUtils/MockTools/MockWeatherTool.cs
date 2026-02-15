using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AchieveAi.LmDotnetTools.TestUtils.MockTools;

/// <summary>
///     Mock weather tool for testing
/// </summary>
[McpServerToolType]
public static partial class MockWeatherTool
{
    /// <summary>
    ///     Gets the current weather for a specified location
    /// </summary>
    /// <param name="location">City name</param>
    /// <returns>Weather information as a string</returns>
    [McpServerTool(Name = "getWeather")]
    [Description("Get current weather for a location")]
    public static partial string GetWeather(string location)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }
}
