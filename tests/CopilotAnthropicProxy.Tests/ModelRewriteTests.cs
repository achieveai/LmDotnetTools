using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>Verifies the raw JSON-node model rewrite preserves every other field and injects when absent.</summary>
public sealed class ModelRewriteTests
{
    [Fact]
    public void TryRewriteModel_overwrites_existing_model()
    {
        var body = Encoding.UTF8.GetBytes("{\"model\":\"claude-3-5-haiku\",\"max_tokens\":5}");

        var ok = ProxyModelResolver.TryRewriteModel(body, "opus-x", out var rewritten, out var incoming);

        ok.Should().BeTrue();
        incoming.Should().Be("claude-3-5-haiku");
        var node = JsonNode.Parse(rewritten)!.AsObject();
        node["model"]!.GetValue<string>().Should().Be("opus-x");
        node["max_tokens"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public void TryRewriteModel_injects_model_when_absent()
    {
        var body = Encoding.UTF8.GetBytes("{\"max_tokens\":5}");

        var ok = ProxyModelResolver.TryRewriteModel(body, "opus-x", out var rewritten, out var incoming);

        ok.Should().BeTrue();
        incoming.Should().BeNull();
        JsonNode.Parse(rewritten)!["model"]!.GetValue<string>().Should().Be("opus-x");
    }

    [Fact]
    public void TryRewriteModel_preserves_cache_control_thinking_system_and_unknown_fields()
    {
        const string json = """
        {
          "model": "claude-3-5-haiku-20241022",
          "max_tokens": 100,
          "thinking": { "type": "enabled", "budget_tokens": 1024 },
          "system": [ { "type": "text", "text": "sys", "cache_control": { "type": "ephemeral" } } ],
          "messages": [ { "role": "user", "content": "hi" } ],
          "anthropic_unknown": { "nested": [1, 2, 3] }
        }
        """;

        var ok = ProxyModelResolver.TryRewriteModel(
            Encoding.UTF8.GetBytes(json), "opus-x", out var rewritten, out _);

        ok.Should().BeTrue();
        var node = JsonNode.Parse(rewritten)!.AsObject();
        node["model"]!.GetValue<string>().Should().Be("opus-x");
        node["thinking"]!["budget_tokens"]!.GetValue<int>().Should().Be(1024);
        node["system"]![0]!["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
        node["anthropic_unknown"]!["nested"]!.AsArray().Should().HaveCount(3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    public void TryRewriteModel_returns_false_for_non_object_bodies(string body)
    {
        var ok = ProxyModelResolver.TryRewriteModel(
            Encoding.UTF8.GetBytes(body), "opus-x", out _, out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void TryRewriteModel_strips_context_management_field()
    {
        const string json = """
        {
          "model": "claude-3-5-haiku",
          "max_tokens": 5,
          "context_management": { "edits": [ { "type": "clear_tool_uses_20250919" } ] }
        }
        """;

        var ok = ProxyModelResolver.TryRewriteModel(
            Encoding.UTF8.GetBytes(json), "opus-x", out var rewritten, out _);

        ok.Should().BeTrue();
        var node = JsonNode.Parse(rewritten)!.AsObject();
        node.ContainsKey("context_management").Should().BeFalse();
        node["model"]!.GetValue<string>().Should().Be("opus-x");
        node["max_tokens"]!.GetValue<int>().Should().Be(5);
    }

    [Fact]
    public void TryRewriteModel_is_a_no_op_when_context_management_is_absent()
    {
        var body = Encoding.UTF8.GetBytes("{\"model\":\"x\",\"max_tokens\":5}");

        var ok = ProxyModelResolver.TryRewriteModel(body, "opus-x", out var rewritten, out _);

        ok.Should().BeTrue();
        JsonNode.Parse(rewritten)!.AsObject().ContainsKey("context_management").Should().BeFalse();
    }

    [Theory]
    [InlineData("/v1/messages")]
    [InlineData("/v1/messages/count_tokens")]
    public async Task Forwarded_request_strips_beta_header_and_context_management_but_preserves_other_fields(
        string path)
    {
        HttpRequestMessage? forwarded = null;
        string? forwardedBody = null;
        await using var factory = new ProxyWebAppFactory(async (req, ct) =>
        {
            forwarded = req;
            forwardedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return TestUpstream.Json(path.EndsWith("count_tokens") ? "{\"input_tokens\":7}" : "{\"type\":\"message\"}");
        });
        using var client = factory.CreateClient();

        const string json = """
        {
          "model": "claude-3-5-haiku-20241022",
          "max_tokens": 100,
          "thinking": { "type": "enabled", "budget_tokens": 1024 },
          "context_management": { "edits": [ { "type": "clear_tool_uses_20250919" } ] },
          "system": [ { "type": "text", "text": "sys", "cache_control": { "type": "ephemeral" } } ],
          "tools": [ { "name": "get_weather", "input_schema": { "type": "object" } } ],
          "messages": [ { "role": "user", "content": "hi" } ],
          "anthropic_unknown": { "nested": [1, 2, 3] }
        }
        """;

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("anthropic-beta", "context-management-2025-06-27");

        using var response = await client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue();
        forwarded!.Headers.Contains("anthropic-beta")
            .Should()
            .BeFalse("Copilot rejects the whole request if any beta value is unrecognized");

        var node = JsonNode.Parse(forwardedBody!)!.AsObject();
        node.ContainsKey("context_management").Should().BeFalse();
        node["thinking"]!["budget_tokens"]!.GetValue<int>().Should().Be(1024);
        node["system"]![0]!["cache_control"]!["type"]!.GetValue<string>().Should().Be("ephemeral");
        node["tools"]![0]!["name"]!.GetValue<string>().Should().Be("get_weather");
        node["anthropic_unknown"]!["nested"]!.AsArray().Should().HaveCount(3);
    }

    [Fact]
    public async Task Forwarded_request_body_has_the_configured_model()
    {
        string? forwardedBody = null;
        await using var factory = new ProxyWebAppFactory(async (req, ct) =>
        {
            forwardedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return TestUpstream.Json("{\"type\":\"message\"}");
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/messages",
            new StringContent("{\"model\":\"whatever\",\"max_tokens\":5}", Encoding.UTF8, "application/json"));

        response.IsSuccessStatusCode.Should().BeTrue();
        forwardedBody.Should().NotBeNull();
        JsonNode.Parse(forwardedBody!)!["model"]!.GetValue<string>()
            .Should().Be(ProxyWebAppFactory.ConfiguredModel);
    }
}
