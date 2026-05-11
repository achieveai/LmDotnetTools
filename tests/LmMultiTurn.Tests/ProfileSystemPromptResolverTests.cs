using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

public class ProfileSystemPromptResolverTests
{
    [Theory]
    [InlineData("profile", null, null, "profile")]      // profile wins
    [InlineData(null, "ctor", null, "ctor")]              // ctor wins
    [InlineData(null, null, "dev", "dev")]                // devInstructions wins
    [InlineData(null, null, null, null)]                   // all null
    [InlineData("profile", "ctor", "dev", "profile")]     // profile beats all
    [InlineData("  ", "ctor", null, "ctor")]              // whitespace profile skipped
    public void Resolve_PrecedenceCascade(string? profilePrompt, string? ctorPrompt, string? devInstructions, string? expected)
    {
        var profile = profilePrompt is null ? null : new AgentRuntimeProfile { SystemPrompt = profilePrompt };
        ProfileSystemPromptResolver.Resolve(profile, ctorPrompt, devInstructions).Should().Be(expected);
    }
}
