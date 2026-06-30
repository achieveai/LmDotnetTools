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
    private static HttpClient UpstreamReturning(string modelsJson)
    {
        var handler = new FakeHttpMessageHandler((req, ct) => Task.FromResult(TestUpstream.Json(modelsJson)));
        return new HttpClient(handler) { BaseAddress = new Uri("https://upstream.test") };
    }

    [Fact]
    public void ParseModelIds_reads_openai_shaped_data_array()
    {
        ProxyModelResolver.ParseModelIds("{\"data\":[{\"id\":\"claude-sonnet-4.5\"},{\"id\":\"claude-opus-4.8\"}]}")
            .Should().Equal("claude-sonnet-4.5", "claude-opus-4.8");
    }

    [Fact]
    public void ParseModelIds_reads_bare_array()
    {
        ProxyModelResolver.ParseModelIds("[{\"id\":\"a\"},{\"id\":\"b\"}]").Should().Equal("a", "b");
    }

    [Fact]
    public void ParseModelIds_ignores_entries_without_a_string_id()
    {
        ProxyModelResolver.ParseModelIds("{\"data\":[{\"id\":\"keep\"},{\"name\":\"no-id\"},{\"id\":123},{\"id\":\"\"}]}")
            .Should().Equal("keep");
    }

    [Fact]
    public async Task ResolveAsync_returns_override_without_calling_upstream()
    {
        var handler = new FakeHttpMessageHandler((req, ct) =>
            throw new InvalidOperationException("upstream must not be called when an override is set"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://upstream.test") };

        var model = await ProxyModelResolver.ResolveAsync(
            client, modelOverride: "my-pinned-model", NullLogger.Instance, CancellationToken.None);

        model.Should().Be("my-pinned-model");
    }

    [Fact]
    public async Task ResolveAsync_picks_the_opus_claude_id_from_models()
    {
        using var client = UpstreamReturning(
            "{\"data\":[{\"id\":\"gpt-4o\"},{\"id\":\"claude-sonnet-4.5\"},{\"id\":\"claude-opus-4.8\"}]}");

        var model = await ProxyModelResolver.ResolveAsync(
            client, modelOverride: null, NullLogger.Instance, CancellationToken.None);

        model.Should().Be("claude-opus-4.8");
    }

    [Fact]
    public async Task ResolveAsync_throws_when_no_opus_model_is_available()
    {
        using var client = UpstreamReturning("{\"data\":[{\"id\":\"claude-sonnet-4.5\"},{\"id\":\"gpt-4o\"}]}");

        var act = () => ProxyModelResolver.ResolveAsync(
            client, modelOverride: null, NullLogger.Instance, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("claude-sonnet-4.5", "the error lists the available Claude ids to pick from");
    }
}
