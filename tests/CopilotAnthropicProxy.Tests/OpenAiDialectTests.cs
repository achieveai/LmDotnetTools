using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class OpenAiDialectTests
{
    private const string DiscoveryJson = """
        {"data":[
          {"id":"claude-opus-4.8","vendor":"Anthropic","supported_endpoints":["/v1/messages","/chat/completions"]},
          {"id":"gpt-5.4","vendor":"OpenAI","supported_endpoints":["/responses","/chat/completions"]},
          {"id":"gpt-5.3-codex","vendor":"OpenAI","supported_endpoints":["/responses"]}
        ]}
        """;

    /// <summary>
    ///     Builds a factory whose upstream answers startup discovery from <see cref="DiscoveryJson"/>
    ///     and hands every other request to <paramref name="onProxied"/>, recording the path it hit.
    /// </summary>
    private static ProxyWebAppFactory Factory(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> onProxied) =>
        new(
            async (request, _) =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Get && path.EndsWith("/models", StringComparison.Ordinal))
                {
                    return TestUpstream.Json(DiscoveryJson);
                }

                return await onProxied(request, path);
            },
            model: null
        );

    [Fact]
    public async Task Chat_completions_forwards_to_the_upstream_chat_completions_path()
    {
        string? seenPath = null;
        await using var factory = Factory(
            (_, path) =>
            {
                seenPath = path;
                return Task.FromResult(TestUpstream.Json("""{"id":"x","choices":[]}"""));
            }
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "claude-opus-4.8", messages = Array.Empty<object>() }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seenPath.Should().Be("/chat/completions");
    }

    [Fact]
    public async Task Chat_completions_renames_max_tokens_for_a_non_anthropic_model()
    {
        string? body = null;
        await using var factory = Factory(
            async (request, _) =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return TestUpstream.Json("""{"id":"x","choices":[]}""");
            }
        );
        using var client = factory.CreateClient();

        _ = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new
            {
                model = "gpt-5.4",
                max_tokens = 256,
                messages = Array.Empty<object>(),
            }
        );

        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.TryGetProperty("max_tokens", out _).Should().BeFalse();
        sent.RootElement.GetProperty("max_completion_tokens").GetInt32().Should().Be(256);
    }

    [Fact]
    public async Task Chat_completions_keeps_max_tokens_for_an_anthropic_model()
    {
        string? body = null;
        await using var factory = Factory(
            async (request, _) =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return TestUpstream.Json("""{"id":"x","choices":[]}""");
            }
        );
        using var client = factory.CreateClient();

        _ = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new
            {
                model = "claude-opus-4.8",
                max_tokens = 256,
                messages = Array.Empty<object>(),
            }
        );

        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(256);
        sent.RootElement.TryGetProperty("max_completion_tokens", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Responses_forwards_to_the_upstream_responses_path()
    {
        string? seenPath = null;
        await using var factory = Factory(
            (_, path) =>
            {
                seenPath = path;
                return Task.FromResult(TestUpstream.Json("""{"id":"resp_1","output":[]}"""));
            }
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/responses",
            new { model = "gpt-5.3-codex", input = Array.Empty<object>() }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seenPath.Should().Be("/responses");
    }

    [Fact]
    public async Task Responses_strips_hosted_tools_before_forwarding()
    {
        // Codex CLI advertises the hosted image_generation tool on every request and cannot be told
        // not to, so this filter is the difference between Codex working through the proxy and a
        // hard 400 from Copilot.
        string? forwardedBody = null;
        await using var factory = Factory(
            async (request, _) =>
            {
                forwardedBody = await request.Content!.ReadAsStringAsync();
                return TestUpstream.Json("""{"id":"resp_1","output":[]}""");
            }
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/responses",
            new
            {
                model = "gpt-5.3-codex",
                input = Array.Empty<object>(),
                tools = new object[] { new { type = "image_generation" }, new { type = "function", name = "shell" } },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var sent = JsonDocument.Parse(forwardedBody!);
        var tools = sent.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        tools.Should().HaveCount(1);
        tools[0].GetProperty("name").GetString().Should().Be("shell");
    }

    [Fact]
    public async Task Responses_returns_an_openai_shaped_404_for_a_model_that_cannot_serve_it()
    {
        await using var factory = Factory((_, _) => throw new InvalidOperationException("must not be forwarded"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/responses",
            new { model = "claude-opus-4.8", input = Array.Empty<object>() }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.TryGetProperty("type", out _).Should().BeFalse("an OpenAI error has no top-level type");

        var err = error.RootElement.GetProperty("error");
        err.GetProperty("type").GetString().Should().Be("not_found_error");
        err.GetProperty("param").ValueKind.Should().Be(JsonValueKind.Null);
        err.GetProperty("code").ValueKind.Should().Be(JsonValueKind.Null);

        var message = err.GetProperty("message").GetString();
        message.Should().Contain("claude-opus-4.8");
        message.Should().Contain("gpt-5.4").And.Contain("gpt-5.3-codex", "the 404 must name what IS servable");
    }

    [Fact]
    public async Task Messages_serves_a_model_without_messages_support_by_translating_to_responses()
    {
        // Replaces an earlier test that pinned the interim "translation is not implemented yet" 404 —
        // that branch is what this endpoint's translated path exists to remove. gpt-5.4 advertises
        // /responses and /chat/completions but NOT /v1/messages, so an Anthropic Messages request for it
        // is translated rather than routed away. TranslatedMessagesTests covers the translation itself;
        // this keeps the dialect x model table in this file complete.
        string? seenPath = null;
        await using var factory = Factory(
            (_, path) =>
            {
                seenPath = path;
                return Task.FromResult(TestUpstream.Json("""{"id":"resp_2","model":"gpt-5.4","output":[]}"""));
            }
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            new
            {
                model = "gpt-5.4",
                max_tokens = 64,
                messages = Array.Empty<object>(),
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seenPath.Should().Be("/responses");

        using var message = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        message.RootElement.GetProperty("type").GetString().Should().Be("message");
        message.RootElement.GetProperty("model").GetString().Should().Be("gpt-5.4");
    }

    [Fact]
    public async Task The_unprefixed_twins_are_bound_too()
    {
        await using var factory = Factory((_, _) => Task.FromResult(TestUpstream.Json("""{"id":"x","choices":[]}""")));
        using var client = factory.CreateClient();

        var chat = await client.PostAsJsonAsync(
            "/chat/completions",
            new { model = "claude-opus-4.8", messages = Array.Empty<object>() }
        );
        var responses = await client.PostAsJsonAsync(
            "/responses",
            new { model = "gpt-5.3-codex", input = Array.Empty<object>() }
        );

        chat.StatusCode.Should().Be(HttpStatusCode.OK);
        responses.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
