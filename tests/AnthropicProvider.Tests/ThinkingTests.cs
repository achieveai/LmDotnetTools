using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests;

public class ThinkingTests
{
    private static readonly JsonSerializerOptions s_jsonOptions =
        AnthropicJsonSerializerOptionsFactory.CreateUniversal();

    [Fact]
    public void AnthropicOutputConfig_ShouldDefaultToHighEffort()
    {
        // "high" is the reasoning depth a regular request assumes; "max" is the opt-in lever.
        Assert.Equal("high", new AnthropicOutputConfig().Effort);
    }

    [Fact]
    public void FromMessages_ShouldExtractOutputConfig_AndSerializeEffort()
    {
        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "Test message" },
        };
        var options = new GenerateReplyOptions
        {
            ModelId = "deepseek-v4-flash",
            ExtraProperties = ImmutableDictionary
                .Create<string, object?>()
                .Add("OutputConfig", new AnthropicOutputConfig { Effort = "max" }),
        };

        var request = AnthropicRequest.FromMessages(messages, options);

        // Extracted from ExtraProperties...
        Assert.NotNull(request.OutputConfig);
        Assert.Equal("max", request.OutputConfig.Effort);

        // ...and serialized under the wire name the provider expects.
        var json = JsonSerializer.Serialize(request, s_jsonOptions);
        Assert.Contains("\"output_config\"", json);
        Assert.Contains("\"effort\":\"max\"", json);
    }

    [Fact]
    public void FromMessages_WithoutOutputConfig_OmitsItFromJson()
    {
        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "Test message" },
        };
        var request = AnthropicRequest.FromMessages(
            messages,
            new GenerateReplyOptions { ModelId = "claude-3-7-sonnet-20250219" }
        );

        Assert.Null(request.OutputConfig);
        var json = JsonSerializer.Serialize(request, s_jsonOptions);
        Assert.DoesNotContain("output_config", json);
    }

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
    public void AnthropicThinking_Enabled_SerializesClassicShapeUnchanged()
    {
        // #709: adding the adaptive shape must not change one byte of what a classic
        // "enabled" + budget_tokens request serializes to.
        var thinking = AnthropicThinking.Enabled(2048);

        Assert.Equal("enabled", thinking.Type);
        Assert.Equal(2048, thinking.BudgetTokens);
        Assert.Equal("""{"type":"enabled","budget_tokens":2048}""", JsonSerializer.Serialize(thinking, s_jsonOptions));
    }

    [Fact]
    public void AnthropicThinking_Adaptive_RequestsSummarizedDisplay_AndOmitsBudgetTokens()
    {
        // #709 root cause: on Claude adaptive-thinking models `display` defaults to "omitted",
        // which returns a thinking block with an empty `thinking` field and only a signature.
        // "summarized" is the opt-in that makes the text come back. Those models also reject
        // budget_tokens outright, so it must be absent from the JSON rather than sent as 0.
        var thinking = AnthropicThinking.Adaptive();

        Assert.Equal("adaptive", thinking.Type);
        Assert.Equal("summarized", thinking.Display);
        Assert.Null(thinking.BudgetTokens);

        var json = JsonSerializer.Serialize(thinking, s_jsonOptions);
        Assert.Equal("""{"type":"adaptive","display":"summarized"}""", json);
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

        // ...and the classic shape reaches the wire exactly as it did before #709.
        var json = JsonSerializer.Serialize(request, s_jsonOptions);
        Assert.Contains("\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":2048}", json);
    }

    [Fact]
    public void FromMessages_ShouldExtractAdaptiveThinking_AndSerializeDisplay()
    {
        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "Test message" },
        };
        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-5",
            ExtraProperties = ImmutableDictionary
                .Create<string, object?>()
                .Add("Thinking", AnthropicThinking.Adaptive()),
        };

        var request = AnthropicRequest.FromMessages(messages, options);

        Assert.NotNull(request.Thinking);
        Assert.Equal("adaptive", request.Thinking.Type);
        Assert.Equal("summarized", request.Thinking.Display);

        var json = JsonSerializer.Serialize(request, s_jsonOptions);
        Assert.Contains("\"thinking\":{\"type\":\"adaptive\",\"display\":\"summarized\"}", json);
    }
}
