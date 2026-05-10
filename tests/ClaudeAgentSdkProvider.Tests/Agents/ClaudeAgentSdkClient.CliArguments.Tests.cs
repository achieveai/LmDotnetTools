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

    [Fact]
    public void BuildCliArguments_OmitsModelFlag_WhenModelIdIsEmpty()
    {
        // Regression for: --max-turns being parsed as the model name when ModelId
        // is empty. The old builder always emitted "--model {value}", producing
        // "--model --max-turns 50 ..." which the CLI parsed as Model="--max-turns".
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = string.Empty, MaxTurns = 50 };

        var args = client.BuildCliArguments(request);

        Assert.DoesNotContain("--model", args);
        Assert.Contains("--max-turns 50", args);
    }

    [Fact]
    public void BuildCliArguments_OmitsModelFlag_WhenModelIdIsWhitespace()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "   ", MaxTurns = 50 };

        var args = client.BuildCliArguments(request);

        Assert.DoesNotContain("--model", args);
        Assert.Contains("--max-turns 50", args);
    }

    [Fact]
    public void BuildCliArguments_EmitsModelFlag_WhenModelIdIsProvided()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6", MaxTurns = 50 };

        var args = client.BuildCliArguments(request);

        Assert.Contains("--model claude-sonnet-4-6", args);
        Assert.Contains("--max-turns 50", args);
    }
}
