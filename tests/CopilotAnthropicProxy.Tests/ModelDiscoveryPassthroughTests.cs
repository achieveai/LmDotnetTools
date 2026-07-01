using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>
///     End-to-end coverage for the no-override discovery path: GET /v1/models lists every
///     /v1/messages-capable Copilot model, and POST /v1/messages passes a recognized model through
///     unchanged instead of always rewriting to the default.
/// </summary>
public sealed class ModelDiscoveryPassthroughTests
{
    private const string DiscoveryJson = """
        {"data":[
            {"id":"claude-opus-4.8","supported_endpoints":["/v1/messages","/chat/completions"]},
            {"id":"claude-sonnet-4.5","supported_endpoints":["/v1/messages"]},
            {"id":"gpt-5.4","supported_endpoints":["/responses","ws:/responses"]}
        ]}
        """;

    /// <summary>
    ///     A real (sanitized) <c>GET /models</c> response body captured from
    ///     <c>api.enterprise.githubcopilot.com</c> — 34 models, of which 7 support <c>/v1/messages</c>,
    ///     including three concurrent <c>claude-opus-*</c> versions (4.6, 4.7, 4.8).
    /// </summary>
    private static string RealModelsResponseJson =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "copilot-models-real-response.json"));

    /// <summary>
    ///     A factory with <c>COPILOT_ANTHROPIC_MODEL</c> unset (discovery mode). The fake upstream answers
    ///     the startup <c>GET /models</c> call itself; <paramref name="onMessages"/> handles everything else.
    /// </summary>
    private static ProxyWebAppFactory DiscoveryFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> onMessages
    )
    {
        return new ProxyWebAppFactory(
            (req, ct) =>
                req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/models"
                    ? Task.FromResult(TestUpstream.Json(DiscoveryJson))
                    : onMessages(req, ct),
            model: null
        );
    }

    [Fact]
    public async Task Models_endpoint_lists_the_real_captured_copilot_response_messages_capable_models()
    {
        await using var factory = new ProxyWebAppFactory(
            (req, ct) =>
                req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/models"
                    ? Task.FromResult(TestUpstream.Json(RealModelsResponseJson))
                    : Task.FromResult(TestUpstream.Json("{}")),
            model: null
        );
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var ids = node["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>());
        ids.Should()
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
    public async Task Models_endpoint_lists_only_messages_capable_models_when_discovering()
    {
        await using var factory = DiscoveryFactory((req, ct) => Task.FromResult(TestUpstream.Json("{}")));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var ids = node["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>());
        ids.Should().BeEquivalentTo(["claude-opus-4.8", "claude-sonnet-4.5"], "gpt-5.4 does not support /v1/messages");
    }

    [Fact]
    public async Task Passthrough_keeps_a_recognized_non_default_model_unchanged()
    {
        string? forwardedBody = null;
        await using var factory = DiscoveryFactory(
            async (req, ct) =>
            {
                forwardedBody = await req.Content!.ReadAsStringAsync(ct);
                return TestUpstream.Json("{\"type\":\"message\"}");
            }
        );
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/messages",
            new StringContent("{\"model\":\"claude-sonnet-4.5\",\"max_tokens\":5}", Encoding.UTF8, "application/json")
        );

        response.IsSuccessStatusCode.Should().BeTrue();
        JsonNode.Parse(forwardedBody!)!["model"]!.GetValue<string>().Should().Be("claude-sonnet-4.5");
    }

    [Fact]
    public async Task Unrecognized_model_falls_back_to_the_discovered_default()
    {
        string? forwardedBody = null;
        await using var factory = DiscoveryFactory(
            async (req, ct) =>
            {
                forwardedBody = await req.Content!.ReadAsStringAsync(ct);
                return TestUpstream.Json("{\"type\":\"message\"}");
            }
        );
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/messages",
            new StringContent("{\"model\":\"not-a-known-model\",\"max_tokens\":5}", Encoding.UTF8, "application/json")
        );

        response.IsSuccessStatusCode.Should().BeTrue();
        JsonNode.Parse(forwardedBody!)!["model"]!.GetValue<string>().Should().Be("claude-opus-4.8");
    }

    [Fact]
    public async Task Passthrough_match_is_case_insensitive_and_normalizes_to_the_catalog_casing()
    {
        string? forwardedBody = null;
        await using var factory = DiscoveryFactory(
            async (req, ct) =>
            {
                forwardedBody = await req.Content!.ReadAsStringAsync(ct);
                return TestUpstream.Json("{\"type\":\"message\"}");
            }
        );
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/messages",
            new StringContent("{\"model\":\"CLAUDE-SONNET-4.5\",\"max_tokens\":5}", Encoding.UTF8, "application/json")
        );

        response.IsSuccessStatusCode.Should().BeTrue();
        JsonNode.Parse(forwardedBody!)!["model"]!.GetValue<string>().Should().Be("claude-sonnet-4.5");
    }
}
