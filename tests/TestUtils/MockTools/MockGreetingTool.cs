using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AchieveAi.LmDotnetTools.TestUtils.MockTools;

/// <summary>
/// Mock greeting tool for testing that mirrors GreetingTool in Program.cs
/// </summary>
[McpServerToolType]
public static class MockGreetingTool
{
    /// <summary>
    /// Says hello to the provided name
    /// </summary>
    /// <param name="name">The name to greet</param>
    /// <returns>A greeting message</returns>
    [McpServerTool, Description("Greets a person by name")]
    public static string SayHello(string name)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }

    /// <summary>
    /// Says goodbye to the provided name
    /// </summary>
    /// <param name="name">The name to say goodbye to</param>
    /// <returns>A goodbye message</returns>
    [McpServerTool, Description("Says goodbye to a person by name")]
    public static string SayGoodbye(string name)
    {
        throw new NotImplementedException("Mock tool call - implement using callback override");
    }
}