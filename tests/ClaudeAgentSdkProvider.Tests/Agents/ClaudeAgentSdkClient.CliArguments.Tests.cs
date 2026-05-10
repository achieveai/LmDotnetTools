using System.Reflection;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Agents;

public class ClaudeAgentSdkClientCliArgumentsTests
{
    [Fact]
    public void BuildCliArguments_IncludesPartialMessagesForToolCallVisibility()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var method = typeof(ClaudeAgentSdkClient).GetMethod(
            "BuildCliArguments",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6", AllowedTools = "Read" };

        var args = Assert.IsType<string>(method?.Invoke(client, [request]));

        Assert.Contains("--include-partial-messages", args);
    }
}
