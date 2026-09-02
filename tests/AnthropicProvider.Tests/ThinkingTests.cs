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
    public void AnthropicThinking_AdaptiveObjectInitializer_OmitsBudgetTokens()
    {
        // BudgetTokens must not carry a property initializer: a record built through an object
        // initializer skips the factories, and a defaulted 1024 would ride along on an adaptive
        // request that the model rejects with HTTP 400.
        var thinking = new AnthropicThinking { Type = "adaptive", Display = "summarized" };

        Assert.Null(thinking.BudgetTokens);
        Assert.Equal(
            """{"type":"adaptive","display":"summarized"}""",
            JsonSerializer.Serialize(thinking, s_jsonOptions)
        );
    }

    [Fact]
    public void FromMessages_WithoutThinking_OmitsItFromJson()
    {
        // A direct api.anthropic.com caller that configures nothing must send no thinking key at all —
        // #709 changed only what Copilot-backed adaptive models request.
        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "Test message" },
        };

        var request = AnthropicRequest.FromMessages(
            messages,
            new GenerateReplyOptions { ModelId = "claude-3-7-sonnet-20250219" }
        );

        Assert.Null(request.Thinking);
        Assert.DoesNotContain("thinking", JsonSerializer.Serialize(request, s_jsonOptions));
    }

    [Theory]
    [InlineData(false, 0.7f)]
    [InlineData(true, 1.0f)]
    public void FromMessages_DefaultTemperature_DependsOnWhetherThinkingIsRequested(
        bool withThinking,
        float expectedTemperature
    )
    {
        // Extended thinking requires temperature 1.0, so requesting it moves the default off 0.7.
        // Adaptive Copilot Claude requests now carry Thinking (#709) and therefore go out at 1.0;
        // pinned here because that is a behaviour change for those models, not just a shape change.
        var messages = new[]
        {
            new TextMessage { Role = Role.User, Text = "Test message" },
        };
        var options = new GenerateReplyOptions { ModelId = "claude-sonnet-5" };
        if (withThinking)
        {
            options = options with
            {
                ExtraProperties = ImmutableDictionary
                    .Create<string, object?>()
                    .Add("Thinking", AnthropicThinking.Adaptive()),
            };
        }

        Assert.Equal(expectedTemperature, AnthropicRequest.FromMessages(messages, options).Temperature);
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
