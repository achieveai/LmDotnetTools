using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Agents;

/// <summary>
/// Argv-level coverage for the host-chosen <c>--session-id &lt;guid&gt;</c> path
/// added by issue #55. Both <c>BuildCliArguments</c> (diagnostic string form)
/// and <c>BuildCliArgumentTokens</c> (runtime pre-tokenized form) must agree.
/// </summary>
public class ClaudeAgentSdkClientAssignSessionIdArgsTests
{
    private const string AssignedId = "00000000-0000-4000-8000-000000000abc";
    private const string ResumeId = "11111111-1111-4111-8111-111111111111";

    private static ClaudeAgentSdkClient NewClient() =>
        new(new ClaudeAgentSdkOptions());

    [Fact]
    public void BuildCliArguments_EmitsSessionIdFlag_WhenAssignedSessionIdSetAndSessionIdEmpty()
    {
        var client = NewClient();
        var request = new ClaudeAgentSdkRequest
        {
            ModelId = "claude-sonnet-4-6",
            AssignedSessionId = AssignedId,
        };

        var args = client.BuildCliArguments(request);

        Assert.Contains($"--session-id {AssignedId}", args);
        Assert.DoesNotContain("--resume", args);
    }

    [Fact]
    public void BuildCliArguments_ResumeWins_WhenBothSessionIdAndAssignedSessionIdSet()
    {
        // Defence-in-depth: ClaudeAgentLoop guarantees they are not both set,
        // but the argv builder must not double-emit if someone bypasses the loop.
        var client = NewClient();
        var request = new ClaudeAgentSdkRequest
        {
            ModelId = "claude-sonnet-4-6",
            SessionId = ResumeId,
            AssignedSessionId = AssignedId,
        };

        var args = client.BuildCliArguments(request);

        Assert.Contains($"--resume {ResumeId}", args);
        Assert.DoesNotContain("--session-id", args);
    }

    [Fact]
    public void BuildCliArguments_OmitsBothFlags_WhenNeitherSet()
    {
        var client = NewClient();
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6" };

        var args = client.BuildCliArguments(request);

        Assert.DoesNotContain("--resume", args);
        Assert.DoesNotContain("--session-id", args);
    }

    [Fact]
    public void BuildCliArgumentTokens_EmitsTokenPair_WhenAssignedSessionIdSet()
    {
        var client = NewClient();
        var request = new ClaudeAgentSdkRequest
        {
            ModelId = "claude-sonnet-4-6",
            AssignedSessionId = AssignedId,
        };

        var tokens = client.BuildCliArgumentTokens(request);

        var idx = tokens.ToList().IndexOf("--session-id");
        Assert.True(idx >= 0, "expected --session-id token in argv");
        Assert.Equal(AssignedId, tokens[idx + 1]);
        Assert.DoesNotContain("--resume", tokens);
    }

    [Fact]
    public void BuildCliArgumentTokens_ResumeWins_WhenBothSet()
    {
        var client = NewClient();
        var request = new ClaudeAgentSdkRequest
        {
            ModelId = "claude-sonnet-4-6",
            SessionId = ResumeId,
            AssignedSessionId = AssignedId,
        };

        var tokens = client.BuildCliArgumentTokens(request);

        var idx = tokens.ToList().IndexOf("--resume");
        Assert.True(idx >= 0, "expected --resume token when SessionId is set");
        Assert.Equal(ResumeId, tokens[idx + 1]);
        Assert.DoesNotContain("--session-id", tokens);
    }
}
