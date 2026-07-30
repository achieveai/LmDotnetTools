using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using FluentAssertions;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.CopilotLive.Tests;

/// <summary>
///     Capability probes for Copilot's <c>/chat/completions</c> endpoint.
///
///     These exist to settle a design question that the captured <c>/models</c> fixture can only
///     answer with evidence, not proof: the catalog ADVERTISES <c>/chat/completions</c> for Claude
///     and for some GPT models, but advertising an endpoint is not the same as honoring it.
///
///     The answer decides how much translation code the CopilotAnthropicProxy sample needs:
///     if <c>/chat/completions</c> is honored, opencode-style clients are a passthrough route and
///     no Chat Completions translation layer has to exist at all.
///
///     Deliberately issues RAW HTTP rather than going through our provider stack, so the result
///     describes the Copilot backend and not our own mappers.
/// </summary>
[Collection(CopilotLiveCollection.Name)]
public sealed class CopilotChatCompletionsProbeTests
{
    private readonly CopilotLiveFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CopilotChatCompletionsProbeTests(CopilotLiveFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    ///     THE GATE: can a Claude model be driven over the OpenAI Chat Completions wire format,
    ///     with streaming and a tool? This is exactly what opencode and most OpenAI SDKs emit.
    /// </summary>
    [SkippableFact]
    public async Task ChatCompletions_serves_a_claude_model_with_streaming_and_tools()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var model = await PickByEndpointAsync("claude", "/chat/completions", supported: true, cts.Token);
        _output.WriteLine($"model: {model}");

        var (status, body) = await PostChatCompletionsAsync(model, cts.Token);
        _output.WriteLine($"status: {(int)status} {status}");
        _output.WriteLine(Truncate(body, 4000));

        status
            .Should()
            .Be(
                HttpStatusCode.OK,
                "Copilot advertises /chat/completions for Claude models; if this is not 200 the proxy "
                    + "must translate Chat Completions -> /v1/messages instead of passing through"
            );

        body.Should().Contain("data:", "a streaming Chat Completions response is SSE");
        body.Should().Contain("tool_calls", "the prompt forces a tool call; args must survive the wire");
        body.Should().Contain("[DONE]", "Chat Completions streams terminate with the [DONE] sentinel");
    }

    /// <summary>
    ///     A GPT model that also advertises <c>/chat/completions</c>. Pins a live-confirmed asymmetry
    ///     the proxy must handle: Claude accepts <c>max_tokens</c> on this endpoint, but GPT models
    ///     reject it and demand <c>max_completion_tokens</c>. An opencode-style client sends
    ///     <c>max_tokens</c>, so a naive byte-level passthrough to a GPT model 400s.
    /// </summary>
    [SkippableFact]
    public async Task ChatCompletions_serves_a_dual_endpoint_gpt_model_but_renames_the_token_limit()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var model = await PickByEndpointAsync("gpt", "/chat/completions", supported: true, cts.Token);
        _output.WriteLine($"model: {model}");

        var (legacyStatus, legacyBody) = await PostChatCompletionsAsync(model, cts.Token, "max_tokens");
        _output.WriteLine($"[max_tokens]            status: {(int)legacyStatus} {legacyStatus}");
        _output.WriteLine(Truncate(legacyBody, 600));

        legacyStatus
            .Should()
            .Be(
                HttpStatusCode.BadRequest,
                "GPT models on Copilot reject max_tokens; this is the asymmetry the proxy must absorb"
            );
        legacyBody.Should().Contain("max_completion_tokens");

        var (status, body) = await PostChatCompletionsAsync(model, cts.Token, "max_completion_tokens");
        _output.WriteLine($"[max_completion_tokens] status: {(int)status} {status}");
        _output.WriteLine(Truncate(body, 3000));

        status
            .Should()
            .Be(
                HttpStatusCode.OK,
                "with the parameter renamed, a dual-endpoint GPT model is servable over /chat/completions"
            );
        body.Should().Contain("[DONE]");
    }

    /// <summary>
    ///     The premise the whole translation layer rests on: a Responses-ONLY model (no
    ///     <c>/chat/completions</c> among its advertised endpoints) is NOT servable over Chat
    ///     Completions. If Copilot served one anyway, every one of these models would be reachable by
    ///     passthrough and the Anthropic-in/Responses-out translator would be dead weight — so this is
    ///     asserted rather than merely recorded. An earlier revision only logged the outcome, which
    ///     meant the premise could quietly stop being true without any test noticing.
    /// </summary>
    [SkippableFact]
    public async Task ChatCompletions_rejects_a_responses_only_model()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var model = await PickByEndpointAsync("gpt", "/chat/completions", supported: false, cts.Token);
        _output.WriteLine($"model: {model}");

        var (status, body) = await PostChatCompletionsAsync(model, cts.Token);
        _output.WriteLine($"status: {(int)status} {status}");
        _output.WriteLine(Truncate(body, 2000));

        status
            .Should()
            .NotBe(
                HttpStatusCode.OK,
                "the catalog omits /chat/completions for this model, and the Anthropic-in -> Responses-out "
                    + "translation path exists precisely because such models cannot be served by passthrough"
            );
    }

    /// <summary>
    ///     The catalog contract <c>ProxyModelResolver.ParseServableModels</c> reads: entries carry a
    ///     non-empty string <c>id</c>, and <c>supported_endpoints</c> arrives as an ARRAY on at least one
    ///     of them. The parser's all-or-nothing fallback treats a catalog with no endpoint metadata
    ///     anywhere as legacy and keeps every id — so a live catalog that silently stopped publishing
    ///     the field would make the proxy advertise models it cannot route, and no fixture test can
    ///     notice that. Dumps the per-model endpoints as well, so the design keeps resting on live data.
    /// </summary>
    [SkippableFact]
    public async Task Advertised_endpoints_are_published_in_the_shape_the_resolver_reads()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var catalog = await GetCatalogAsync(cts.Token);
        foreach (var entry in catalog)
        {
            var endpoints = entry.Endpoints.Count == 0 ? "(none)" : string.Join(", ", entry.Endpoints);
            _output.WriteLine($"{entry.Id,-32} | {entry.Vendor,-14} | {endpoints}");
        }

        catalog.Should().NotBeEmpty("the proxy resolves its default model from this catalog at startup");
        catalog
            .Should()
            .OnlyContain(e => !string.IsNullOrWhiteSpace(e.Id), "an entry without an id is dropped unseen");
        catalog
            .Should()
            .Contain(
                e => e.Endpoints.Count > 0,
                "with no endpoint metadata anywhere the resolver falls back to keeping every id, "
                    + "including ones no route can serve"
            );
    }

    private async Task<(HttpStatusCode Status, string Body)> PostChatCompletionsAsync(
        string model,
        CancellationToken cancellationToken,
        string tokenLimitParameter = "max_tokens"
    )
    {
        using var http = CopilotHttpClientFactory.Create(
            _fixture.Options.BaseUrl,
            _fixture.TokenProvider,
            _fixture.Session,
            _fixture.Options,
            timeout: TimeSpan.FromSeconds(90)
        );

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = true,
            [tokenLimitParameter] = 256,
            ["messages"] = new object[]
            {
                new { role = "user", content = "What is the weather in Paris? Use the get_weather tool." },
            },
            ["tools"] = new object[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "get_weather",
                        description = "Get the current weather for a city.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                city = new { type = "string", description = "City name" },
                                units = new { type = "string", @enum = new[] { "c", "f" } },
                            },
                            required = new[] { "city" },
                        },
                    },
                },
            },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/chat/completions")
        {
            Content = JsonContent.Create(payload),
        };

        using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            )
            .ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var sb = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            _ = sb.AppendLine(line);
            if (sb.Length > 200_000)
            {
                break;
            }
        }

        return (response.StatusCode, sb.ToString());
    }

    /// <summary>
    ///     Picks a model of <paramref name="family"/> that DOES (or does not) advertise
    ///     <paramref name="endpoint"/>, read from the live catalog rather than guessed from the id.
    ///     Guessing by name is what made an earlier revision of this probe pick <c>gpt-5.4-mini</c>
    ///     (Responses-only) when it wanted a dual-endpoint model.
    /// </summary>
    private async Task<string> PickByEndpointAsync(
        string family,
        string endpoint,
        bool supported,
        CancellationToken cancellationToken
    )
    {
        var catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);

        var match = catalog
            .Where(m => m.Id.Contains(family, StringComparison.OrdinalIgnoreCase))
            .Where(m => m.Endpoints.Count > 0)
            .FirstOrDefault(m => m.Endpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase) == supported);

        Skip.If(
            match is null,
            $"No '{family}' model that {(supported ? "advertises" : "omits")} '{endpoint}' is available on this account."
        );

        return match.Id;
    }

    private async Task<IReadOnlyList<CatalogEntry>> GetCatalogAsync(CancellationToken cancellationToken)
    {
        using var http = CopilotHttpClientFactory.Create(
            _fixture.Options.BaseUrl,
            _fixture.TokenProvider,
            _fixture.Session,
            _fixture.Options
        );
        using var response = await http.GetAsync("/models", cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data)
            ? data
            : root;

        var entries = new List<CatalogEntry>();
        foreach (var item in list.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var vendor = item.TryGetProperty("vendor", out var vEl) ? vEl.GetString() ?? "?" : "?";
            var endpoints =
                item.TryGetProperty("supported_endpoints", out var epEl) && epEl.ValueKind == JsonValueKind.Array
                    ? epEl.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList()
                    : [];

            entries.Add(new CatalogEntry(id, vendor, endpoints));
        }

        return entries;
    }

    private sealed record CatalogEntry(string Id, string Vendor, IReadOnlyList<string> Endpoints);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + $"\n... [{value.Length - max} more chars]";
}
