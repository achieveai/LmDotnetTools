using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>
///     End-to-end coverage for the no-override discovery path: GET /v1/models lists every servable
///     Copilot model, and POST /v1/messages passes a recognized model through unchanged instead of
///     always rewriting to the default.
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
    ///     <c>api.enterprise.githubcopilot.com</c> — 34 models, of which 13 are servable (advertise a
    ///     reachable endpoint and a non-excluded vendor), including three concurrent <c>claude-opus-*</c>
    ///     versions (4.6, 4.7, 4.8).
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
    public async Task Models_endpoint_lists_the_real_captured_copilot_response_servable_models()
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
                "gpt-5.3-codex",
                "gpt-5.4-mini",
                "gpt-5.4-nano",
                "gpt-5.4",
                "gpt-5.5",
                "gpt-5-mini",
                "claude-sonnet-4.5",
                "claude-haiku-4.5"
            );
    }

    [Fact]
    public async Task Models_endpoint_lists_only_servable_models_when_discovering()
    {
        await using var factory = DiscoveryFactory((req, ct) => Task.FromResult(TestUpstream.Json("{}")));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var ids = node["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>());
        ids.Should()
            .BeEquivalentTo(
                ["claude-opus-4.8", "claude-sonnet-4.5", "gpt-5.4"],
                "gpt-5.4 advertises /responses, which this proxy can also forward to"
            );
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
    public async Task Models_endpoint_serves_a_body_both_dialects_can_read()
    {
        await using var factory = DiscoveryFactory((req, ct) => Task.FromResult(TestUpstream.Json("{}")));
        using var client = factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/v1/models"));
        var root = doc.RootElement;

        root.GetProperty("object").GetString().Should().Be("list", "OpenAI clients key off this");
        root.GetProperty("has_more").GetBoolean().Should().BeFalse();

        var first = root.GetProperty("data")[0];
        first.GetProperty("type").GetString().Should().Be("model", "Anthropic clients key off this");
        first.GetProperty("object").GetString().Should().Be("model", "OpenAI clients key off this");
        first.GetProperty("id").GetString().Should().NotBeNullOrWhiteSpace();
        first.GetProperty("display_name").GetString().Should().NotBeNullOrWhiteSpace();
        first
            .GetProperty("owned_by")
            .GetString()
            .Should()
            .Be("copilot", "DiscoveryJson entries carry no \"vendor\" key, so owned_by falls back");
        first.GetProperty("created").GetInt64().Should().BeGreaterThan(0);
        first.GetProperty("created_at").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Models_endpoint_passes_a_populated_vendor_through_as_owned_by()
    {
        await using var factory = new ProxyWebAppFactory(
            (req, ct) =>
                req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/models"
                    ? Task.FromResult(TestUpstream.Json(RealModelsResponseJson))
                    : Task.FromResult(TestUpstream.Json("{}")),
            model: null
        );
        using var client = factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/v1/models"));
        var first = doc.RootElement.GetProperty("data")[0];

        // The real fixture's first servable entry is claude-opus-4.6 with "vendor":"Anthropic".
        first.GetProperty("id").GetString().Should().Be("claude-opus-4.6");
        first
            .GetProperty("owned_by")
            .GetString()
            .Should()
            .Be("Anthropic", "a populated vendor should pass through unchanged, not fall back to \"copilot\"");
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
