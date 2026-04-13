using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests;

public class ThinkingTests
{
    [Fact]
    public void AnthropicThinking_ShouldBeCreatedWithBudget()
    {
        // Simple test to verify the AnthropicThinking class works correctly
        var budget = 2048;
        var thinking = new AnthropicThinking(budget);

        Assert.Equal(budget, thinking.BudgetTokens);
        Assert.Equal("enabled", thinking.Type);
    }

    [Fact]
    public void FromMessages_ShouldExtractThinking()
    {
        // Create a message and options with thinking
        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "Test message" },
        };
        var thinking = new AnthropicThinking(2048);

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-3-7-sonnet-20250219",
            ExtraProperties = ImmutableDictionary.Create<string, object?>().Add("Thinking", thinking),
        };

        // Create the request
        var request = AnthropicRequest.FromMessages(messages, options);

        // Verify thinking was extracted correctly
        Assert.NotNull(request.Thinking);
        Assert.Equal(2048, request.Thinking.BudgetTokens);
    }
}
