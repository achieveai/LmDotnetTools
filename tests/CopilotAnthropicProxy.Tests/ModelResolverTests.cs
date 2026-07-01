using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>
///     Direct unit tests for the startup model resolution that the fast WebApplicationFactory suite cannot
///     exercise (the factory always pins <c>COPILOT_ANTHROPIC_MODEL</c>, so only override mode runs there).
/// </summary>
public sealed class ModelResolverTests
{
    /// <summary>
    ///     A real (sanitized) <c>GET /models</c> response body captured from
    ///     <c>api.enterprise.githubcopilot.com</c> — 34 models, of which 7 support <c>/v1/messages</c>,
    ///     including three concurrent <c>claude-opus-*</c> versions (4.6, 4.7, 4.8) in that upstream order.
    /// </summary>
    private static string RealModelsResponseJson =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "copilot-models-real-response.json"));

    private static HttpClient UpstreamReturning(string modelsJson)
    {
        var handler = new FakeHttpMessageHandler((req, ct) => Task.FromResult(TestUpstream.Json(modelsJson)));
        return new HttpClient(handler) { BaseAddress = new Uri("https://upstream.test") };
    }

    [Fact]
    public void ParseModelIds_reads_openai_shaped_data_array()
    {
        ProxyModelResolver
            .ParseModelIds("{\"data\":[{\"id\":\"claude-sonnet-4.5\"},{\"id\":\"claude-opus-4.8\"}]}")
            .Should()
            .Equal("claude-sonnet-4.5", "claude-opus-4.8");
    }

    [Fact]
    public void ParseModelIds_reads_bare_array()
    {
        ProxyModelResolver.ParseModelIds("[{\"id\":\"a\"},{\"id\":\"b\"}]").Should().Equal("a", "b");
    }

    [Fact]
    public void ParseModelIds_ignores_entries_without_a_string_id()
    {
        ProxyModelResolver
            .ParseModelIds("{\"data\":[{\"id\":\"keep\"},{\"name\":\"no-id\"},{\"id\":123},{\"id\":\"\"}]}")
            .Should()
            .Equal("keep");
    }

    [Fact]
    public void ParseMessagesCapableModelIds_keeps_only_ids_whose_supported_endpoints_include_v1_messages()
    {
        const string json = """
            {"data":[
                {"id":"claude-opus-4.8","supported_endpoints":["/v1/messages","/chat/completions"]},
                {"id":"claude-sonnet-4.5","supported_endpoints":["/v1/messages"]},
                {"id":"gpt-5.4","supported_endpoints":["/responses","ws:/responses"]},
                {"id":"gemini-2.5-pro","supported_endpoints":["/chat/completions"]},
                {"id":"gpt-4o"}
            ]}
            """;

        ProxyModelResolver.ParseMessagesCapableModelIds(json).Should().Equal("claude-opus-4.8", "claude-sonnet-4.5");
    }

    [Fact]
    public async Task ResolveAsync_returns_override_without_calling_upstream()
    {
        var handler = new FakeHttpMessageHandler(
            (req, ct) => throw new InvalidOperationException("upstream must not be called when an override is set")
        );
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://upstream.test") };

        var catalog = await ProxyModelResolver.ResolveAsync(
            client,
            modelOverride: "my-pinned-model",
            NullLogger.Instance,
            CancellationToken.None
        );

        catalog.Default.Should().Be("my-pinned-model");
        catalog.Available.Should().Equal("my-pinned-model");
    }

    [Fact]
    public async Task ResolveAsync_picks_the_opus_claude_id_from_models()
    {
        using var client = UpstreamReturning(
            "{\"data\":["
                + "{\"id\":\"gpt-4o\",\"supported_endpoints\":[\"/chat/completions\"]},"
                + "{\"id\":\"claude-sonnet-4.5\",\"supported_endpoints\":[\"/v1/messages\"]},"
                + "{\"id\":\"claude-opus-4.8\",\"supported_endpoints\":[\"/v1/messages\"]}"
                + "]}"
        );

        var catalog = await ProxyModelResolver.ResolveAsync(
            client,
            modelOverride: null,
            NullLogger.Instance,
            CancellationToken.None
        );

        catalog.Default.Should().Be("claude-opus-4.8");
        catalog.Available.Should().Equal("claude-sonnet-4.5", "claude-opus-4.8");
    }

    [Fact]
    public async Task ResolveAsync_throws_when_no_opus_model_is_available()
    {
        using var client = UpstreamReturning(
            "{\"data\":["
                + "{\"id\":\"claude-sonnet-4.5\",\"supported_endpoints\":[\"/v1/messages\"]},"
                + "{\"id\":\"gpt-4o\",\"supported_endpoints\":[\"/chat/completions\"]}"
                + "]}"
        );

        var act = () =>
            ProxyModelResolver.ResolveAsync(client, modelOverride: null, NullLogger.Instance, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("claude-sonnet-4.5", "the error lists the available Claude ids to pick from");
    }

    [Fact]
    public async Task ResolveAsync_excludes_messages_incapable_models_from_the_opus_search_even_if_named_opus()
    {
        // A hypothetical "opus" model that only supports /chat/completions must NOT be picked as default,
        // since this proxy can only forward Anthropic-Messages-shaped requests.
        using var client = UpstreamReturning(
            "{\"data\":["
                + "{\"id\":\"claude-opus-chat-only\",\"supported_endpoints\":[\"/chat/completions\"]},"
                + "{\"id\":\"claude-sonnet-4.5\",\"supported_endpoints\":[\"/v1/messages\"]}"
                + "]}"
        );

        var act = () =>
            ProxyModelResolver.ResolveAsync(client, modelOverride: null, NullLogger.Instance, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(new[] { "claude-opus-4.6", "claude-opus-4.7", "claude-opus-4.8" }, "claude-opus-4.8")]
    [InlineData(new[] { "claude-opus-4.8", "claude-opus-4.7", "claude-opus-4.6" }, "claude-opus-4.8")]
    [InlineData(new[] { "claude-opus-4.7", "claude-opus-4.6" }, "claude-opus-4.7")]
    [InlineData(new[] { "claude-opus-4.9", "claude-opus-4.10" }, "claude-opus-4.10")]
    public void PickHighestVersionOpusId_picks_the_numerically_highest_version_regardless_of_list_order(
        string[] claudeIds,
        string expectedDefault
    )
    {
        // claude-opus-4.10 must beat claude-opus-4.9 numerically (not "4.10" < "4.9" as a string compare).
        ProxyModelResolver.PickHighestVersionOpusId(claudeIds).Should().Be(expectedDefault);
    }

    [Fact]
    public async Task ResolveAsync_picks_the_highest_version_opus_when_multiple_are_available()
    {
        // Real Copilot data ships three concurrent opus versions; the oldest (4.6) must NOT win just
        // because it appears first in the upstream list.
        using var client = UpstreamReturning(
            "{\"data\":["
                + "{\"id\":\"claude-opus-4.6\",\"supported_endpoints\":[\"/v1/messages\"]},"
                + "{\"id\":\"claude-opus-4.7\",\"supported_endpoints\":[\"/v1/messages\"]},"
                + "{\"id\":\"claude-opus-4.8\",\"supported_endpoints\":[\"/v1/messages\"]}"
                + "]}"
        );

        var catalog = await ProxyModelResolver.ResolveAsync(
            client,
            modelOverride: null,
            NullLogger.Instance,
            CancellationToken.None
        );

        catalog.Default.Should().Be("claude-opus-4.8");
    }

    [Fact]
    public void ParseMessagesCapableModelIds_matches_the_real_captured_copilot_response()
    {
        ProxyModelResolver
            .ParseMessagesCapableModelIds(RealModelsResponseJson)
            .Should()
            .Equal(
                "claude-opus-4.6",
                "claude-opus-4.7",
                "claude-opus-4.8",
                "claude-sonnet-4.6",
                "claude-sonnet-5",
                "claude-sonnet-4.5",
                "claude-haiku-4.5"
            );
    }

    [Fact]
    public async Task ResolveAsync_picks_claude_opus_4_8_as_default_from_the_real_captured_copilot_response()
    {
        using var client = UpstreamReturning(RealModelsResponseJson);

        var catalog = await ProxyModelResolver.ResolveAsync(
            client,
            modelOverride: null,
            NullLogger.Instance,
            CancellationToken.None
        );

        catalog.Default.Should().Be("claude-opus-4.8");
        catalog
            .Available.Should()
            .Equal(
                "claude-opus-4.6",
                "claude-opus-4.7",
                "claude-opus-4.8",
                "claude-sonnet-4.6",
                "claude-sonnet-5",
                "claude-sonnet-4.5",
                "claude-haiku-4.5"
            );
    }

    [Theory]
    [InlineData("claude-opus-4.8", "claude-opus-4.8")]
    [InlineData("CLAUDE-OPUS-4.8", "claude-opus-4.8")]
    [InlineData(null, "claude-opus-4.8")]
    [InlineData("", "claude-opus-4.8")]
    [InlineData("some-unknown-model", "claude-opus-4.8")]
    public void SelectOutboundModel_passes_through_matches_and_falls_back_to_default_otherwise(
        string? incoming,
        string expected
    )
    {
        var catalog = new ProxyModelCatalog("claude-opus-4.8", ["claude-sonnet-4.5", "claude-opus-4.8"]);

        ProxyModelResolver.SelectOutboundModel(incoming, catalog).Should().Be(expected);
    }

    [Fact]
    public void SelectOutboundModel_passes_through_a_non_default_available_model()
    {
        var catalog = new ProxyModelCatalog("claude-opus-4.8", ["claude-sonnet-4.5", "claude-opus-4.8"]);

        ProxyModelResolver.SelectOutboundModel("claude-sonnet-4.5", catalog).Should().Be("claude-sonnet-4.5");
    }

    [Fact]
    public void PeekModel_reads_the_model_field_without_mutating_the_body()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("{\"model\":\"claude-sonnet-4.5\",\"max_tokens\":5}");

        ProxyModelResolver.PeekModel(body).Should().Be("claude-sonnet-4.5");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"max_tokens\":5}")]
    [InlineData("[1,2,3]")]
    public void PeekModel_returns_null_when_the_model_field_is_absent_or_body_is_unparsable(string body)
    {
        ProxyModelResolver.PeekModel(System.Text.Encoding.UTF8.GetBytes(body)).Should().BeNull();
    }
}
