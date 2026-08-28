using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class ModelRouteTests
{
    private static ProxyModelInfo Dual =>
        new(
            "claude-opus-4.8",
            "Anthropic",
            [CopilotModelsResponse.MessagesEndpoint, ProxyModelResolver.ChatCompletionsEndpoint]
        );

    private static ProxyModelInfo ResponsesOnly =>
        new("gpt-5.3-codex", "OpenAI", [CopilotModelsResponse.ResponsesEndpoint]);

    private static ProxyModelInfo ResponsesAndChat =>
        new("gpt-5.4", "OpenAI", [CopilotModelsResponse.ResponsesEndpoint, ProxyModelResolver.ChatCompletionsEndpoint]);

    private static ProxyModelInfo NoMetadata => new("pinned-model", "", []);

    [Fact]
    public void Anthropic_dialect_passes_through_for_a_messages_capable_model()
    {
        var route = ModelRouter.Resolve(ProxyDialect.AnthropicMessages, Dual);

        route.Should().NotBeNull();
        route!.Kind.Should().Be(ProxyRouteKind.Passthrough);
        route.UpstreamPath.Should().Be("/v1/messages");
    }

    [Fact]
    public void Anthropic_dialect_translates_for_a_responses_only_model()
    {
        var route = ModelRouter.Resolve(ProxyDialect.AnthropicMessages, ResponsesOnly);

        route.Should().NotBeNull();
        route!.Kind.Should().Be(ProxyRouteKind.TranslateAnthropicToResponses);
        route.UpstreamPath.Should().Be("/responses");
    }

    [Fact]
    public void Anthropic_dialect_prefers_passthrough_when_a_model_serves_both()
    {
        // gpt-5.4 advertises /responses AND /chat/completions but NOT /v1/messages, so it translates.
        ModelRouter
            .Resolve(ProxyDialect.AnthropicMessages, ResponsesAndChat)!
            .Kind.Should()
            .Be(ProxyRouteKind.TranslateAnthropicToResponses);
    }

    [Fact]
    public void A_model_with_no_endpoint_metadata_is_treated_as_anthropic_capable()
    {
        var route = ModelRouter.Resolve(ProxyDialect.AnthropicMessages, NoMetadata);

        route.Should().NotBeNull();
        route!.Kind.Should().Be(ProxyRouteKind.Passthrough);
    }

    [Fact]
    public void Chat_completions_dialect_passes_through_only_for_models_that_advertise_it()
    {
        ModelRouter.Resolve(ProxyDialect.ChatCompletions, Dual)!.UpstreamPath.Should().Be("/chat/completions");
        ModelRouter.Resolve(ProxyDialect.ChatCompletions, ResponsesOnly).Should().BeNull();
    }

    [Fact]
    public void Responses_dialect_passes_through_only_for_models_that_advertise_it()
    {
        ModelRouter.Resolve(ProxyDialect.Responses, ResponsesOnly)!.UpstreamPath.Should().Be("/responses");
        ModelRouter.Resolve(ProxyDialect.Responses, Dual).Should().BeNull();
    }

    [Fact]
    public void A_pinned_model_cannot_serve_the_responses_dialect()
    {
        // Pinned mode has no endpoint metadata, so we cannot claim Responses support.
        ModelRouter.Resolve(ProxyDialect.Responses, NoMetadata).Should().BeNull();
    }

    [Fact]
    public void A_pinned_model_is_treated_as_chat_completions_capable()
    {
        var route = ModelRouter.Resolve(ProxyDialect.ChatCompletions, NoMetadata);

        route.Should().NotBeNull();
        route!.Kind.Should().Be(ProxyRouteKind.Passthrough);
        route.UpstreamPath.Should().Be("/chat/completions");
    }

    [Fact]
    public void Servable_lists_the_ids_that_can_serve_a_dialect()
    {
        var catalog = new ProxyModelCatalog("claude-opus-4.8", [Dual, ResponsesOnly, ResponsesAndChat]);

        ModelRouter
            .Servable(ProxyDialect.AnthropicMessages, catalog)
            .Should()
            .Equal("claude-opus-4.8", "gpt-5.3-codex", "gpt-5.4");
        ModelRouter.Servable(ProxyDialect.Responses, catalog).Should().Equal("gpt-5.3-codex", "gpt-5.4");
        ModelRouter.Servable(ProxyDialect.ChatCompletions, catalog).Should().Equal("claude-opus-4.8", "gpt-5.4");
    }
}
