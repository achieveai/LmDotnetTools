using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.CopilotLive.Tests;

/// <summary>
///     Live smoke tests that boot the real <c>CopilotAnthropicProxy.Sample</c> host (real Copilot token
///     provider + real upstream transport) and drive it over its own HTTP surface. Skipped automatically
///     when no Copilot credential is present. NOT part of LmDotnetTools.sln, so CI never runs these.
/// </summary>
[Collection(CopilotLiveCollection.Name)]
public sealed class CopilotAnthropicProxyLiveTests
{
    private readonly CopilotLiveFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CopilotAnthropicProxyLiveTests(CopilotLiveFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [SkippableFact]
    public async Task Proxy_non_streaming_messages_returns_assistant_text()
    {
        Skip.IfNot(
            new CliCredentialCopilotTokenProvider().ResolveToken() is not null,
            "No GitHub Copilot credential found; skipping the live proxy smoke test.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var model = await _fixture.ResolveAnthropicModelAsync(cts.Token);
        _output.WriteLine($"Proxy model: {model}");

        await using var factory = CreateProxy(model);
        using var client = factory.CreateClient();

        const string body =
            "{\"model\":\"will-be-rewritten\",\"max_tokens\":64,\"temperature\":0,"
            + "\"messages\":[{\"role\":\"user\",\"content\":\"Reply with the single word: READY\"}]}";

        using var response = await client.PostAsync(
            "/v1/messages", new StringContent(body, Encoding.UTF8, "application/json"), cts.Token);

        _ = response.EnsureSuccessStatusCode();
        var text = ExtractText(await response.Content.ReadAsStringAsync(cts.Token));
        _output.WriteLine($"Reply: {text}");
        text.Should().NotBeNullOrWhiteSpace();
    }

    [SkippableFact]
    public async Task Proxy_streaming_messages_yields_content_block_delta()
    {
        Skip.IfNot(
            new CliCredentialCopilotTokenProvider().ResolveToken() is not null,
            "No GitHub Copilot credential found; skipping the live proxy smoke test.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var model = await _fixture.ResolveAnthropicModelAsync(cts.Token);

        await using var factory = CreateProxy(model);
        using var client = factory.CreateClient();

        const string body =
            "{\"model\":\"will-be-rewritten\",\"max_tokens\":128,\"temperature\":0,\"stream\":true,"
            + "\"messages\":[{\"role\":\"user\",\"content\":\"Count from 1 to 5, separated by spaces.\"}]}";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        _ = response.EnsureSuccessStatusCode();

        var sse = await response.Content.ReadAsStringAsync(cts.Token);
        _output.WriteLine($"SSE: {sse}");
        sse.Should().Contain("content_block_delta", "the streaming endpoint should emit incremental deltas");
    }

    /// <summary>Boots the proxy host with the resolved model pinned via the environment variable.</summary>
    private static ProxyHost CreateProxy(string model)
    {
        Environment.SetEnvironmentVariable("COPILOT_ANTHROPIC_MODEL", model);
        return new ProxyHost();
    }

    /// <summary>Extracts concatenated <c>text</c> blocks from an Anthropic non-streaming message body.</summary>
    private static string ExtractText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type)
                && type.GetString() == "text"
                && block.TryGetProperty("text", out var text))
            {
                _ = builder.Append(text.GetString());
            }
        }

        return builder.ToString();
    }

    /// <summary>
    ///     A <see cref="WebApplicationFactory{TEntryPoint}"/> over the real proxy that clears the pinned
    ///     model env var on dispose so a later test does not inherit it.
    /// </summary>
    private sealed class ProxyHost : WebApplicationFactory<Program>
    {
        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing)
                {
                    Environment.SetEnvironmentVariable("COPILOT_ANTHROPIC_MODEL", null);
                }
            }
        }
    }
}
