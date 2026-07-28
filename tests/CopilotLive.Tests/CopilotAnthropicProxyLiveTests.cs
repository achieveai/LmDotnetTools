using System.Net;
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

    // =================================================================================================
    // The translated route: Anthropic Messages IN, OpenAI Responses OUT.
    //
    // Everything below drives a model this Copilot account exposes ONLY through /responses, so the
    // proxy has to translate rather than relay bytes. These are the only tests anywhere in the repo
    // that can confirm the live event NAMES and payload SHAPES the translators assume — a fixture can
    // only prove the translators agree with themselves.
    //
    // The proxy must run in DISCOVERY mode for any of this to be reachable: a catalog pinned through
    // COPILOT_ANTHROPIC_MODEL carries no endpoint metadata, and a metadata-free model always routes as
    // Anthropic-Messages passthrough. CreateDiscoveringProxy clears the variable for that reason.
    // =================================================================================================

    /// <summary>
    ///     THE GATE for the translated route: drive a Responses-only model through the ANTHROPIC
    ///     endpoint and require a well-formed Anthropic stream back. This is the one quadrant that is
    ///     real translation rather than passthrough.
    /// </summary>
    [SkippableFact]
    public async Task Anthropic_endpoint_streams_a_responses_only_model()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var model = await PickResponsesOnlyModelAsync(cts.Token);
        _output.WriteLine($"model: {model}");

        var body = await PostProxyAsync(
            "/v1/messages",
            new
            {
                model,
                max_tokens = 128,
                stream = true,
                messages = new[] { new { role = "user", content = "Reply with the single word: ok" } },
            },
            cts.Token
        );

        _output.WriteLine(body);

        body.Should().Contain("event: message_start");
        body.Should().Contain("content_block_delta");
        body.Should().Contain("event: message_stop");
        body.Should().NotContain("response.created", "upstream Responses frames must not leak through");
    }

    /// <summary>
    ///     Confirms Claude Code's model-validation probe survives translation. It sends max_tokens: 1
    ///     with maxRetries: 0 as the FIRST request against any new model; a 400 here makes the model
    ///     look unusable before the user ever gets a turn.
    /// </summary>
    [SkippableFact]
    public async Task Anthropic_endpoint_survives_the_max_tokens_one_validation_probe()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var model = await PickResponsesOnlyModelAsync(cts.Token);
        _output.WriteLine($"model: {model}");

        var body = await PostProxyAsync(
            "/v1/messages",
            new
            {
                model,
                max_tokens = 1,
                messages = new[]
                {
                    new { role = "user", content = new[] { new { type = "text", text = "Hi" } } },
                },
            },
            cts.Token
        );

        _output.WriteLine(body);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("type").GetString().Should().Be("message");
        doc.RootElement.GetProperty("role").GetString().Should().Be("assistant");
        doc.RootElement.TryGetProperty("stop_reason", out _).Should().BeTrue();

        // Task 7 could only document what incomplete_details MIGHT look like, so record what actually
        // came back rather than asserting a reason this repo has never observed.
        _output.WriteLine($"OBSERVED stop_reason: {doc.RootElement.GetProperty("stop_reason").GetString()}");
    }

    /// <summary>
    ///     Settles the one shape Task 7 could only guess at: what a TRUNCATED Responses reply looks
    ///     like. <c>ResponsesToAnthropicJson.DeriveStopReason</c> maps
    ///     <c>incomplete_details.reason == "max_output_tokens"</c> onto Anthropic's <c>max_tokens</c>,
    ///     and every completed reply this repo has seen carries <c>incomplete_details: null</c> — so
    ///     the mapping's only input has never been observed. Asks raw for the field's live spelling,
    ///     then checks the proxy derives the right stop_reason from it.
    /// </summary>
    [SkippableFact]
    public async Task Truncated_reply_reports_incomplete_details_and_maps_to_max_tokens()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var model = await PickResponsesOnlyModelAsync(cts.Token);
        _output.WriteLine($"model: {model}");

        const string LongAnswer = "Count slowly from 1 to 200, one number per line. Do not stop early.";

        var (rawStatus, rawBody) = await PostRawAsync(
            ModelRouter.ResponsesPath,
            new
            {
                model,
                store = false,
                max_output_tokens = 16,
                input = new[]
                {
                    new
                    {
                        type = "message",
                        role = "user",
                        content = new[] { new { type = "input_text", text = LongAnswer } },
                    },
                },
            },
            cts.Token
        );

        _output.WriteLine($"RAW {ModelRouter.ResponsesPath} max_output_tokens=16 -> {(int)rawStatus} {rawStatus}");
        _output.WriteLine($"OBSERVED raw status field:       {ReadEcho(rawBody, "status")}");
        _output.WriteLine($"OBSERVED raw incomplete_details: {ReadEcho(rawBody, "incomplete_details")}");

        var body = await PostProxyAsync(
            "/v1/messages",
            new
            {
                model,
                max_tokens = 16,
                messages = new[] { new { role = "user", content = LongAnswer } },
            },
            cts.Token
        );

        _output.WriteLine(Truncate(body, 1500));

        using var doc = JsonDocument.Parse(body);
        var stopReason = doc.RootElement.GetProperty("stop_reason").GetString();
        _output.WriteLine($"OBSERVED proxy stop_reason:      {stopReason}");

        // Observed 2026-07-27 and asserted from here on: the spelling DeriveStopReason reads is real.
        ReadEcho(rawBody, "incomplete_details")
            .Should()
            .Contain("max_output_tokens", "DeriveStopReason keys the max_tokens mapping off this exact reason");
        stopReason.Should().Be("max_tokens");
    }

    /// <summary>
    ///     A tool call end to end through the translated route. Every other streaming test is plain
    ///     text, and <c>ResponsesToAnthropicSse</c>'s tool arms hinge on event names nothing in this
    ///     repo has ever observed live. The translator deliberately DROPS a
    ///     <c>function_call_arguments.delta</c> when no tool_use block is open rather than inventing an
    ///     id and name, so a mis-guessed <c>output_item.added</c> spelling would make tool calls vanish
    ///     with no diagnostic at all. This test is the only thing that catches that.
    /// </summary>
    [SkippableFact]
    public async Task Anthropic_endpoint_streams_a_tool_call()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var model = await PickResponsesOnlyModelAsync(cts.Token);
        _output.WriteLine($"model: {model}");

        var body = await PostProxyAsync(
            "/v1/messages",
            new
            {
                model,
                max_tokens = 512,
                stream = true,
                tools = new[]
                {
                    new
                    {
                        name = "get_weather",
                        description = "Get the current weather for a city.",
                        input_schema = new
                        {
                            type = "object",
                            properties = new { city = new { type = "string", description = "City name" } },
                            required = new[] { "city" },
                        },
                    },
                },
                tool_choice = new { type = "any" },
                messages = new[] { new { role = "user", content = "What is the weather in Paris?" } },
            },
            cts.Token
        );

        _output.WriteLine(body);

        body.Should().Contain("event: message_start");
        body.Should()
            .Contain(
                "\"type\":\"tool_use\"",
                "a forced tool call must reach the client as an Anthropic tool_use block"
            );
        body.Should()
            .Contain(
                "input_json_delta",
                "the tool's arguments are streamed; dropping them leaves an un-callable tool_use block"
            );
        body.Should().Contain("event: message_stop");
    }

    /// <summary>
    ///     Confirms the SSE framing assumptions the whole streaming translator rests on, by reading a
    ///     RAW Responses stream (no proxy, no provider stack): the event NAMES, the Content-Type Task 9
    ///     answers 502 for when it is missing, and — critically — whether any single event ever carries
    ///     more than one <c>data:</c> line. The proxy's splitter feeds each <c>data:</c> line to the
    ///     translator as a complete payload, so a spec-legal multi-line event would arrive in fragments,
    ///     fail to parse, and be dropped in silence.
    /// </summary>
    [SkippableFact]
    public async Task Responses_stream_uses_the_event_names_the_translator_assumes()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var model = await PickResponsesOnlyModelAsync(cts.Token);
        _output.WriteLine($"model: {model}");

        var capture = await CaptureResponsesStreamAsync(
            ToolCallingResponsesRequest(model, requestReasoningSummaries: true),
            cts.Token
        );

        DumpCapture("raw streaming /responses, tool forced, reasoning summaries requested", capture);

        capture.Status.Should().Be(HttpStatusCode.OK);
        capture
            .ContentType.Should()
            .StartWith("text/event-stream", "the proxy answers 502 for a streaming reply that is not SSE");
        capture
            .MaxDataLinesPerEvent.Should()
            .Be(1, "the proxy treats every data: line as one complete payload; a multi-line event is dropped");
        capture
            .PayloadTypes.Should()
            .Contain(
                "response.output_item.added",
                "ResponsesToAnthropicSse opens its tool_use block on this event and on nothing else"
            );
        capture
            .PayloadTypes.Should()
            .Contain(
                "response.function_call_arguments.delta",
                "the tool arguments arrive on this event; a different spelling drops them silently"
            );
    }

    /// <summary>
    ///     Establishes whether Copilot EVER emits reasoning summaries, and if so under what request.
    ///     It matters because <c>AnthropicToResponsesRequest</c> sends no <c>reasoning</c> field at all:
    ///     if summaries only arrive when explicitly requested, the translators' <c>thinking</c> arms are
    ///     unreachable on the translated route. Two captures, both text-only on a prompt that rewards
    ///     thinking (a forced tool call short-circuits reasoning): one silent, one asking for summaries
    ///     at a non-default effort. Recorded rather than asserted — the point is the observation.
    /// </summary>
    [SkippableFact]
    public async Task Responses_stream_reports_when_reasoning_summaries_arrive()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        var model = await PickResponsesOnlyModelAsync(cts.Token);
        _output.WriteLine($"model: {model}");

        var silent = await CaptureResponsesStreamAsync(
            ReasoningResponsesRequest(model, askForSummaries: false),
            cts.Token
        );
        DumpCapture("raw streaming /responses, no reasoning field sent (what the proxy sends)", silent);

        var asked = await CaptureResponsesStreamAsync(
            ReasoningResponsesRequest(model, askForSummaries: true),
            cts.Token
        );
        DumpCapture("raw streaming /responses, reasoning {effort: medium, summary: auto}", asked);

        static List<string> SummaryEvents(RawSseCapture capture) =>
            [.. capture.PayloadTypes.Where(t => t.Contains("reasoning", StringComparison.Ordinal))];

        var silentSummaries = SummaryEvents(silent);
        var askedSummaries = SummaryEvents(asked);

        _output.WriteLine(
            "OBSERVED reasoning events, no reasoning field sent: "
                + (silentSummaries.Count == 0 ? "<none>" : string.Join(", ", silentSummaries))
        );
        _output.WriteLine(
            "OBSERVED reasoning events, summaries requested:     "
                + (askedSummaries.Count == 0 ? "<none>" : string.Join(", ", askedSummaries))
        );
    }

    /// <summary>
    ///     Settles whether <c>temperature</c> / <c>top_p</c> may be passed through to a Responses-only
    ///     model. Reasoning models on the Responses API are documented to reject a non-default
    ///     <c>temperature</c>, and <c>AnthropicToResponsesRequest</c> copies both across, so this is
    ///     probed live rather than guessed: raw first (what the API itself says), then through the
    ///     proxy (where the passthrough decision actually bites).
    /// </summary>
    [SkippableFact]
    public async Task Responses_only_model_accepts_a_non_default_temperature()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var model = await PickResponsesOnlyModelAsync(cts.Token);
        _output.WriteLine($"model: {model}");

        var (rawStatus, rawBody) = await PostRawAsync(
            ModelRouter.ResponsesPath,
            new
            {
                model,
                store = false,
                temperature = 0.7,
                top_p = 0.5,
                max_output_tokens = 16,
                input = new[]
                {
                    new
                    {
                        type = "message",
                        role = "user",
                        content = new[] { new { type = "input_text", text = "Reply with: ok" } },
                    },
                },
            },
            cts.Token
        );

        _output.WriteLine($"RAW /responses temperature=0.7 top_p=0.5 -> {(int)rawStatus} {rawStatus}");
        _output.WriteLine($"RAW echoed temperature: {ReadEcho(rawBody, "temperature")}");
        _output.WriteLine($"RAW echoed top_p: {ReadEcho(rawBody, "top_p")}");
        _output.WriteLine(Truncate(rawBody, 2000));

        var (proxyStatus, proxyBody) = await SendProxyAsync(
            "/v1/messages",
            new
            {
                model,
                max_tokens = 16,
                temperature = 0.7,
                top_p = 0.5,
                messages = new[] { new { role = "user", content = "Reply with: ok" } },
            },
            cts.Token
        );

        _output.WriteLine($"PROXY /v1/messages temperature=0.7 top_p=0.5 -> {(int)proxyStatus} {proxyStatus}");
        _output.WriteLine(Truncate(proxyBody, 2000));

        rawStatus.Should().Be(HttpStatusCode.OK, "the Responses API's own verdict on a non-default temperature");
        proxyStatus.Should().Be(HttpStatusCode.OK, "the proxy copies temperature and top_p straight across");
    }

    /// <summary>
    ///     <c>count_tokens</c> has no Responses counterpart, so the proxy answers an honest 404 rather
    ///     than running a billed generation or inventing a number. Not a regression: the pre-translation
    ///     proxy 404'd these too. Claude Code calls this endpoint, so the shape of the refusal is part
    ///     of the contract.
    /// </summary>
    [SkippableFact]
    public async Task Anthropic_count_tokens_404s_for_a_responses_only_model()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var model = await PickResponsesOnlyModelAsync(cts.Token);
        _output.WriteLine($"model: {model}");

        var (status, body) = await SendProxyAsync(
            "/v1/messages/count_tokens",
            new
            {
                model,
                messages = new[] { new { role = "user", content = "Hi" } },
            },
            cts.Token
        );

        _output.WriteLine($"status: {(int)status} {status}");
        _output.WriteLine(body);

        status.Should().Be(HttpStatusCode.NotFound);
        body.Should().Contain("not_found_error");
    }

    /// <summary>Codex's quadrant: a Responses request must reach Copilot unchanged.</summary>
    [SkippableFact]
    public async Task Responses_endpoint_passes_through()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var model = await PickResponsesOnlyModelAsync(cts.Token);
        _output.WriteLine($"model: {model}");

        var body = await PostProxyAsync(
            "/v1/responses",
            new
            {
                model,
                store = false,
                input = new[]
                {
                    new
                    {
                        type = "message",
                        role = "user",
                        content = new[] { new { type = "input_text", text = "Reply with: ok" } },
                    },
                },
            },
            cts.Token
        );

        _output.WriteLine(body);
        body.Should().Contain("\"output\"");
    }

    /// <summary>
    ///     Picks a model this account serves ONLY through <c>/responses</c>, read from the live catalog
    ///     rather than guessed from the id — guessing by name is what made an earlier probe pick a
    ///     Responses-only model when it wanted a dual-endpoint one. Vendors the proxy refuses (Google,
    ///     Microsoft) are excluded here too, or the proxy would answer 404 for a model that genuinely is
    ///     Responses-only. Cheap tiers first: these tests spend real tokens.
    ///     <c>COPILOT_OPENAI_MODEL</c> overrides, for pinning one specific model by hand.
    /// </summary>
    private async Task<string> PickResponsesOnlyModelAsync(CancellationToken cancellationToken)
    {
        var pinned = Environment.GetEnvironmentVariable("COPILOT_OPENAI_MODEL");
        if (!string.IsNullOrWhiteSpace(pinned))
        {
            return pinned;
        }

        var catalog = await _fixture.GetCatalogAsync(cancellationToken);
        var candidates = catalog
            .Where(m => m.Advertises(ModelRouter.ResponsesPath))
            .Where(m => !m.Advertises(ModelRouter.ChatCompletionsPath))
            .Where(m => !m.Advertises(ModelRouter.MessagesPath))
            .Where(m => !ProxyExcludedVendors.Contains(m.Vendor, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Skip.If(candidates.Count == 0, "This Copilot account exposes no Responses-only model the proxy will serve.");

        foreach (var hint in CheapModelHints)
        {
            var cheap = candidates.FirstOrDefault(m => m.Id.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (cheap is not null)
            {
                return cheap.Id;
            }
        }

        return candidates[0].Id;
    }

    /// <summary>Vendors <c>ProxyModelResolver</c> filters out of the catalog; picking one would 404.</summary>
    private static readonly string[] ProxyExcludedVendors = ["Google", "Microsoft"];

    /// <summary>Substrings that mark the cheap tier of a model family, most preferred first.</summary>
    private static readonly string[] CheapModelHints = ["nano", "mini"];

    /// <summary>
    ///     A Responses request that forces a tool call, optionally asking for reasoning summaries.
    ///     Returned as <see cref="object"/> so the two probes cannot drift apart on anything but the one
    ///     field under test.
    /// </summary>
    private static object ToolCallingResponsesRequest(string model, bool requestReasoningSummaries)
    {
        var request = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = true,
            ["store"] = false,
            ["max_output_tokens"] = 512,
            ["tool_choice"] = "required",
            ["tools"] = new object[]
            {
                new
                {
                    type = "function",
                    name = "get_weather",
                    description = "Get the current weather for a city.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { city = new { type = "string", description = "City name" } },
                        required = new[] { "city" },
                    },
                },
            },
            ["input"] = new object[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new[] { new { type = "input_text", text = "What is the weather in Paris?" } },
                },
            },
        };

        if (requestReasoningSummaries)
        {
            // effort as well as summary: Copilot defaults these models to effort "none", so asking only
            // for a summary yields a turn that never reasons and therefore never summarises.
            request["reasoning"] = new { effort = "medium", summary = "auto" };
        }

        return request;
    }

    /// <summary>
    ///     A text-only Responses request on a prompt that rewards thinking, optionally asking for
    ///     reasoning summaries at a non-default effort. No tools: <c>tool_choice: "required"</c> makes
    ///     the model answer with a call immediately, which is exactly when it does not reason.
    /// </summary>
    private static object ReasoningResponsesRequest(string model, bool askForSummaries)
    {
        var request = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["stream"] = true,
            ["store"] = false,
            ["max_output_tokens"] = 512,
            ["input"] = new object[]
            {
                new
                {
                    type = "message",
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "input_text",
                            text = "A bat and a ball cost $1.10 together. The bat costs $1.00 more than the ball. "
                                + "How much does the ball cost? Answer with the amount only.",
                        },
                    },
                },
            },
        };

        if (askForSummaries)
        {
            // effort as well as summary: Copilot defaults these models to effort "none", so asking only
            // for a summary yields a turn that never reasons and therefore never summarises.
            request["reasoning"] = new { effort = "medium", summary = "auto" };
        }

        return request;
    }

    /// <summary>Everything a raw Responses SSE stream reveals about the framing the translator assumes.</summary>
    private sealed record RawSseCapture(
        HttpStatusCode Status,
        string? ContentType,
        IReadOnlyList<string> Lines,
        IReadOnlyList<string> EventNames,
        IReadOnlyList<string> PayloadTypes,
        int MaxDataLinesPerEvent
    );

    /// <summary>
    ///     Issues a RAW streaming <c>POST /responses</c> — no proxy, no provider stack — and captures
    ///     the response Content-Type, every <c>event:</c> name, every payload <c>type</c>, and the most
    ///     <c>data:</c> lines any single event carried.
    /// </summary>
    private async Task<RawSseCapture> CaptureResponsesStreamAsync(object payload, CancellationToken cancellationToken)
    {
        using var http = CopilotHttpClientFactory.Create(
            _fixture.Options.BaseUrl,
            _fixture.TokenProvider,
            _fixture.Session,
            _fixture.Options,
            timeout: TimeSpan.FromSeconds(90)
        );

        using var request = new HttpRequestMessage(HttpMethod.Post, ModelRouter.ResponsesPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        var lines = new List<string>();
        var eventNames = new List<string>();
        var payloadTypes = new List<string>();
        var maxDataLines = 0;
        var dataLinesInEvent = 0;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lines.Add(line);

            if (line.Length == 0)
            {
                dataLinesInEvent = 0;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                RecordFirstSeen(eventNames, line[6..].Trim());
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            dataLinesInEvent++;
            maxDataLines = Math.Max(maxDataLines, dataLinesInEvent);
            RecordFirstSeen(payloadTypes, ReadPayloadType(line[5..].Trim()));
        }

        return new RawSseCapture(
            response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            lines,
            eventNames,
            payloadTypes,
            maxDataLines
        );
    }

    /// <summary>Appends in first-seen order, skipping blanks and repeats, so the dump is deterministic.</summary>
    private static void RecordFirstSeen(List<string> seen, string value)
    {
        if (value.Length > 0 && !seen.Contains(value, StringComparer.Ordinal))
        {
            seen.Add(value);
        }
    }

    /// <summary>Reads an SSE payload's <c>type</c>, labelling anything that is not a JSON object.</summary>
    private static string ReadPayloadType(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String)
            {
                return type.GetString() ?? "(null type)";
            }

            return "(json, no string type)";
        }
        catch (JsonException)
        {
            return $"(not json) {Truncate(payload, 60)}";
        }
    }

    private void DumpCapture(string label, RawSseCapture capture)
    {
        _output.WriteLine($"--- {label} ---");
        _output.WriteLine($"status:                       {(int)capture.Status} {capture.Status}");
        _output.WriteLine($"content-type:                 {capture.ContentType ?? "(none)"}");
        _output.WriteLine($"max data: lines in one event: {capture.MaxDataLinesPerEvent}");
        _output.WriteLine($"event: names:                 {string.Join(", ", capture.EventNames)}");
        _output.WriteLine($"payload types:                {string.Join(", ", capture.PayloadTypes)}");
        _output.WriteLine("raw frames:");
        _output.WriteLine(Truncate(string.Join("\n", capture.Lines), 20000));
    }

    /// <summary>POSTs JSON straight to Copilot, bypassing the proxy entirely.</summary>
    private async Task<(HttpStatusCode Status, string Body)> PostRawAsync(
        string path,
        object payload,
        CancellationToken cancellationToken
    )
    {
        using var http = CopilotHttpClientFactory.Create(
            _fixture.Options.BaseUrl,
            _fixture.TokenProvider,
            _fixture.Session,
            _fixture.Options,
            timeout: TimeSpan.FromSeconds(90)
        );

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(path, content, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>POSTs JSON to a freshly booted proxy in discovery mode and returns status plus body.</summary>
    private static async Task<(HttpStatusCode Status, string Body)> SendProxyAsync(
        string path,
        object payload,
        CancellationToken cancellationToken
    )
    {
        await using var factory = CreateDiscoveringProxy();
        using var client = factory.CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(path, content, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>As <see cref="SendProxyAsync"/>, but requires a 200 and returns just the body.</summary>
    private static async Task<string> PostProxyAsync(string path, object payload, CancellationToken cancellationToken)
    {
        var (status, body) = await SendProxyAsync(path, payload, cancellationToken);
        status.Should().Be(HttpStatusCode.OK, "POST {0} failed with: {1}", path, Truncate(body, 2000));
        return body;
    }

    /// <summary>
    ///     Boots the proxy in DISCOVERY mode. Clearing <c>COPILOT_ANTHROPIC_MODEL</c> is load-bearing:
    ///     a pinned catalog carries no endpoint metadata, so every model in it routes as Anthropic
    ///     passthrough and the translated route becomes unreachable.
    /// </summary>
    private static ProxyHost CreateDiscoveringProxy()
    {
        Environment.SetEnvironmentVariable("COPILOT_ANTHROPIC_MODEL", null);
        return new ProxyHost();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + $"\n... [{value.Length - max} more chars]";

    /// <summary>
    ///     Reads a top-level scalar back out of a Responses reply so a probe can report what the API
    ///     ECHOED rather than what it was sent — the difference between "accepted" and "silently reset
    ///     to the default" is invisible in the status code alone.
    /// </summary>
    private static string ReadEcho(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var value) ? value.ToString() : "<absent>";
        }
        catch (JsonException)
        {
            return "<unparseable>";
        }
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
