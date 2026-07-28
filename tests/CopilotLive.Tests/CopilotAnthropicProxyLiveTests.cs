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

        // Named up front, so an upstream 400 fails as "the call did not succeed" rather than as a
        // confusing "incomplete_details was missing" further down.
        rawStatus.Should().Be(HttpStatusCode.OK, "a truncated reply is still a successful call: {0}", Truncate(rawBody, 400));

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
    ///     Establishes the asymmetry the README rests on: Copilot NEVER volunteers reasoning summaries,
    ///     and does produce them when asked. That is why <c>AnthropicToResponsesRequest</c> sends
    ///     <c>reasoning</c> on every translated request — without it the translators' <c>thinking</c>
    ///     arms would be unreachable. Both captures are text-only on a prompt that rewards thinking; a
    ///     forced tool call short-circuits reasoning and would fake a negative.
    ///
    ///     The "never volunteers" half runs on ONE model, and it is weaker evidence than a green
    ///     assertion makes it look. This is the only request in the suite that sends NO reasoning field
    ///     at all, so the shape has never been tried on a second model — and the model it picks is the
    ///     cheapest tier, which Copilot defaults to <c>effort: "none"</c>. A turn that never reasons has
    ///     nothing to summarise, so an empty result here cannot separate "Copilot volunteers no
    ///     summaries" from "this model never reasons at all". The assertion stays because a summary
    ///     arriving unasked would still be a genuine regression, not because it settles the general
    ///     claim; widening it would mean sweeping this shape across the catalog.
    ///
    ///     The "produces when asked" half walks the catalog instead, because whether any ONE
    ///     turn reasons is a coin flip — asserting it on a single sample failed a full-suite run.
    /// </summary>
    [SkippableFact]
    public async Task Responses_stream_reports_when_reasoning_summaries_arrive()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(600));

        var model = await PickResponsesOnlyModelAsync(cts.Token);
        _output.WriteLine($"model: {model}");

        var silent = await CaptureResponsesStreamAsync(
            ReasoningResponsesRequest(model, ReasoningRequest.None),
            cts.Token
        );
        DumpCapture("raw streaming /responses, no reasoning field sent", silent);

        var silentSummaries = ReasoningEventsIn(silent);
        _output.WriteLine(
            "OBSERVED reasoning events, no reasoning field sent: "
                + (silentSummaries.Count == 0 ? "<none>" : string.Join(", ", silentSummaries))
        );

        // Status first: without it an upstream 400 prints "<none>" and reads as a genuine "Copilot
        // volunteered nothing", which is the exact claim this half of the probe is the evidence for.
        silent.Status.Should().Be(HttpStatusCode.OK);
        silentSummaries
            .Should()
            .BeEmpty("summaries arriving unasked would mean the translated route needs no reasoning field");

        var askedSummaries = new List<string>();
        foreach (var candidate in await ResponsesCapableModelsAsync(cts.Token))
        {
            var asked = await CaptureResponsesStreamAsync(
                ReasoningResponsesRequest(candidate, ReasoningRequest.EffortAndSummary),
                cts.Token
            );

            asked.Status.Should().Be(HttpStatusCode.OK, "{0} must accept reasoning {{effort, summary}}", candidate);
            askedSummaries = ReasoningEventsIn(asked);

            _output.WriteLine(
                $"{candidate,-24} reasoning {{effort: medium, summary: auto}} -> "
                    + (askedSummaries.Count == 0 ? "<none>" : string.Join(", ", askedSummaries))
            );

            if (askedSummaries.Count > 0)
            {
                DumpCapture($"raw streaming /responses, {candidate}, reasoning {{effort: medium, summary: auto}}", asked);
                break;
            }
        }

        askedSummaries
            .Should()
            .NotBeEmpty("no served model produced a summary even when asked, so asking has stopped working");
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

        // The echo, not just the status: Copilot reports 1 / 0.98 as its defaults when neither field is
        // sent, so getting the sent values back is what distinguishes "honoured" from "silently reset".
        ReadEcho(rawBody, "temperature").Should().Be("0.7");
        ReadEcho(rawBody, "top_p").Should().Be("0.5");
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
    ///     SAFEGUARD (a) for shipping an unconditional <c>reasoning</c> field: is
    ///     <c>{"summary": "auto"}</c> on its own accepted everywhere, and does it actually PRODUCE
    ///     summaries? The earlier evidence sent <c>effort</c> alongside it, and Copilot defaults some
    ///     models to <c>effort: "none"</c> — a turn that never reasons cannot summarise.
    ///
    ///     ITS ONLY FAILURE MODE IS A MODEL ANSWERING SOMETHING OTHER THAN 200. Acceptance is the
    ///     invariant, and it is asserted per model inside the loop. Productivity is recorded and never
    ///     asserted: sweeps minutes apart saw a different single model produce summaries, and one
    ///     full-suite run saw every swept model inert at once. An earlier revision asserted "some model
    ///     produced summaries" and failed on exactly that run. So accepted-but-inert is what this probe
    ///     DOCUMENTS, not what it catches — it is the evidence for the README's claim that summary-alone
    ///     is unreliable, and the reason the shipped code derives an effort from the client instead of
    ///     relying on this.
    /// </summary>
    [SkippableFact]
    public async Task Summary_auto_alone_is_accepted_everywhere_but_is_not_dependable()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(600));

        var models = await ResponsesCapableModelsAsync(cts.Token);
        Skip.If(models.Count == 0, "This Copilot account exposes no /responses model the proxy will serve.");

        // Swept across every model the proxy serves through /responses ALONE — dual-endpoint models are
        // excluded, see ResponsesCapableModelsAsync — rather than just the cheap one: Copilot's DEFAULT
        // effort is a per-model property, and "summary alone is inert" on a model that defaults to
        // effort "none" says nothing about a model that defaults to a real one.
        var inert = new List<string>();
        var productive = new List<string>();

        foreach (var model in models)
        {
            var capture = await CaptureResponsesStreamAsync(
                ReasoningResponsesRequest(model, ReasoningRequest.SummaryOnly),
                cts.Token
            );

            var reasoningEvents = ReasoningEventsIn(capture);
            _output.WriteLine(
                $"{model,-24} -> {(int)capture.Status} {capture.Status}  "
                    + $"effort={EchoedReasoningEffort(capture)}  "
                    + $"reasoning events: {(reasoningEvents.Count == 0 ? "<none>" : string.Join(", ", reasoningEvents))}"
            );

            capture.Status.Should().Be(HttpStatusCode.OK, $"{model} must accept reasoning:{{summary:auto}}");
            (reasoningEvents.Count == 0 ? inert : productive).Add(model);
        }

        _output.WriteLine($"OBSERVED summaries produced by:  {(productive.Count == 0 ? "<none>" : string.Join(", ", productive))}");
        _output.WriteLine($"OBSERVED accepted but inert on:  {(inert.Count == 0 ? "<none>" : string.Join(", ", inert))}");

        // No assertion follows, deliberately. An all-inert sweep is a real, observed outcome of this
        // request shape, not a regression, so `productive` is not asserted on; and a coverage assertion
        // here would be a tautology — the loop above adds every model to exactly one of the two lists
        // and never breaks, so `inert + productive == models` restates itself and cannot fail. The
        // per-model 200 inside the loop is the whole contract. What the shipped code depends on is
        // covered by `Thinking_enabled_makes_a_none_effort_model_reason`.
    }

    /// <summary>
    ///     SAFEGUARD (b): does ANY model this proxy serves through <c>/responses</c> reject a
    ///     <c>reasoning</c> field? Several probes in this file use one cheap model, so an unconditional
    ///     request field would otherwise be shipped on one model's evidence. This one sweeps every model
    ///     the proxy routes to <c>/responses</c> — non-reasoning-looking models included, dual-endpoint
    ///     models excluded because they never reach the translator, see
    ///     <see cref="ResponsesCapableModelsAsync" /> — and names any model that refuses.
    /// </summary>
    [SkippableFact]
    public async Task Every_responses_model_accepts_a_reasoning_field()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));

        var models = await ResponsesCapableModelsAsync(cts.Token);
        Skip.If(models.Count == 0, "This Copilot account exposes no /responses model the proxy will serve.");

        _output.WriteLine($"sweeping {models.Count} /responses models: {string.Join(", ", models)}");

        var rejections = new List<string>();
        foreach (var model in models)
        {
            var (status, body) = await PostRawAsync(
                ModelRouter.ResponsesPath,
                new
                {
                    model,
                    store = false,
                    max_output_tokens = 16,
                    reasoning = new { summary = "auto" },
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

            _output.WriteLine($"{model,-24} -> {(int)status} {status}  reasoning echo: {ReadEcho(body, "reasoning")}");

            if (status != HttpStatusCode.OK)
            {
                rejections.Add($"{model} -> {(int)status} {Truncate(body, 300)}");
                _output.WriteLine($"    REJECTED: {Truncate(body, 500)}");
            }
        }

        rejections
            .Should()
            .BeEmpty(
                "an unconditional reasoning field is only safe if every served /responses model accepts it"
            );
    }

    /// <summary>
    ///     Does every served model accept every effort the budget mapping can produce? The mapping turns
    ///     a client's <c>thinking.budget_tokens</c> into <c>low</c> / <c>medium</c> / <c>high</c>, so a
    ///     model rejecting any one of them would 400 a request the client is entitled to send.
    /// </summary>
    [SkippableFact]
    public async Task Every_responses_model_accepts_every_mapped_effort()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(600));

        var models = await ResponsesCapableModelsAsync(cts.Token);
        Skip.If(models.Count == 0, "This Copilot account exposes no /responses model the proxy will serve.");

        var rejections = new List<string>();
        foreach (var model in models)
        {
            foreach (var effort in MappedEfforts)
            {
                var (status, body) = await PostRawAsync(
                    ModelRouter.ResponsesPath,
                    new
                    {
                        model,
                        store = false,
                        max_output_tokens = 16,
                        reasoning = new { effort, summary = "auto" },
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

                _output.WriteLine($"{model,-24} effort={effort,-6} -> {(int)status} {status}  echo: {ReadEcho(body, "reasoning")}");

                if (status != HttpStatusCode.OK)
                {
                    rejections.Add($"{model} effort={effort} -> {(int)status} {Truncate(body, 300)}");
                }
            }
        }

        rejections
            .Should()
            .BeEmpty("the budget mapping can emit any of these, so a model rejecting one would 400 a legitimate request");
    }

    /// <summary>
    ///     THE PAYOFF for mapping <c>thinking.budget_tokens</c> onto an effort. Some models default to
    ///     <c>effort: "none"</c> and provably never reason, so summaries alone can never produce a
    ///     <c>thinking</c> block for them. This drives exactly those models through the ANTHROPIC
    ///     endpoint with extended thinking enabled and reports whether one appears.
    ///
    ///     The mapping clearly works — models that could NEVER reason before now usually do — but it is
    ///     not a guarantee, and WHICH model falls silent is not stable either. Over five runs, two had
    ///     all four models produce a block. On the other three at least one stayed silent:
    ///     <c>gpt-5.4</c> on two of them and <c>gpt-5.4-nano</c> on the third, and on the two of those
    ///     with a full per-model split recorded the remaining three models still produced one. So this
    ///     asserts that SOME rescued model reasoned, which is the claim the README makes, and logs the
    ///     per-model split. Do not tighten this to "all models": that was tried, and the third run
    ///     refuted it — nor to "all but <c>gpt-5.4</c>", which the fifth run would have refuted.
    ///
    ///     Statuses are checked first, or a 400 would masquerade as "this model cannot think".
    /// </summary>
    [SkippableFact]
    public async Task Thinking_enabled_makes_a_none_effort_model_reason()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(900));

        var models = await ResponsesCapableModelsAsync(cts.Token);
        Skip.If(models.Count == 0, "This Copilot account exposes no /responses model the proxy will serve.");

        // Read the defaults live rather than naming models: which model defaults to "none" is Copilot's
        // choice and has already changed once during this task.
        var neverReason = new List<string>();
        foreach (var model in models)
        {
            var (status, body) = await PostRawAsync(
                ModelRouter.ResponsesPath,
                new
                {
                    model,
                    store = false,
                    max_output_tokens = 16,
                    reasoning = new { summary = "auto" },
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

            status.Should().Be(HttpStatusCode.OK, "{0} must answer before its default effort can be read", model);

            if (ReadEchoedReasoningEffort(body) == "none")
            {
                neverReason.Add(model);
            }
        }

        _output.WriteLine($"models defaulting to effort \"none\": {string.Join(", ", neverReason)}");
        // A skip here is not a clean pass. This probe is the ONLY end-to-end enforcement of the
        // budget-to-effort mapping, and it works by rescuing a model that provably cannot reason on its
        // own — without one, a thinking block would not prove the effort we sent caused it. So say out
        // loud what disappears, rather than substituting a weaker assertion on some other model.
        Skip.If(
            neverReason.Count == 0,
            "No served model defaults to effort \"none\", so nothing here is being rescued and the ONLY "
                + "end-to-end check of the budget-to-effort mapping just went silent. If Copilot has "
                + "stopped defaulting any model to \"none\", the payoff cannot be demonstrated this way at "
                + "all and the mapping needs a different live check — do not weaken this one to keep it "
                + "running."
        );

        var reasoned = new List<string>();
        var stayedSilent = new List<string>();
        foreach (var model in neverReason)
        {
            // Shaped like Claude Code's own extended-thinking request: budget below max_tokens, and a
            // 10240 budget lands in the medium bucket.
            var (status, body) = await SendProxyAsync(
                "/v1/messages",
                new
                {
                    model,
                    max_tokens = 21333,
                    stream = true,
                    thinking = new { type = "enabled", budget_tokens = 10240 },
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = "A bat and a ball cost $1.10 together. The bat costs $1.00 more than "
                                + "the ball. How much does the ball cost? Answer with the amount only.",
                        },
                    },
                },
                cts.Token
            );

            status.Should().Be(HttpStatusCode.OK, "{0} failed the translated route with: {1}", model, Truncate(body, 400));

            var thought = body.Contains("\"type\":\"thinking\"", StringComparison.Ordinal);
            _output.WriteLine($"{model,-24} thinking enabled -> {(thought ? "THINKING BLOCK" : "no thinking block")}");
            (thought ? reasoned : stayedSilent).Add(model);
        }

        _output.WriteLine($"OBSERVED reasoned once asked:    {(reasoned.Count == 0 ? "<none>" : string.Join(", ", reasoned))}");
        _output.WriteLine($"OBSERVED still silent when asked: {(stayedSilent.Count == 0 ? "<none>" : string.Join(", ", stayedSilent))}");

        reasoned
            .Should()
            .NotBeEmpty(
                "these models default to effort \"none\" and can never emit a thinking block on their "
                    + "own, so if none of them reasons even when thinking is enabled, mapping the "
                    + "client's budget onto an effort has bought nothing"
            );
    }

    /// <summary>The efforts <c>BuildReasoning</c>'s budget mapping can emit.</summary>
    private static readonly string[] MappedEfforts = ["low", "medium", "high"];

    /// <summary>Reads <c>reasoning.effort</c> out of a NON-streaming Responses reply.</summary>
    private static string ReadEchoedReasoningEffort(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("reasoning", out var reasoning)
                && reasoning.ValueKind == JsonValueKind.Object
                && reasoning.TryGetProperty("effort", out var effort)
                ? effort.ToString()
                : "<absent>";
        }
        catch (JsonException)
        {
            return "<unparseable>";
        }
    }

    /// <summary>
    ///     Drives the FULL translated route with a request naming no reasoning of any kind — all Claude
    ///     Code sends when extended thinking is off. The request translator adds
    ///     <c>reasoning: {"summary": "auto"}</c> by itself and the stream translator reframes whatever
    ///     comes back, so this exercises both halves against every served model.
    ///
    ///     WHAT IT ASSERTS IS THE 200, NOT A THINKING BLOCK. The upstream shape it produces —
    ///     summary-only, no effort — is byte-for-byte the one
    ///     <see cref="Summary_auto_alone_is_accepted_everywhere_but_is_not_dependable" /> sends, just
    ///     through the Anthropic front door instead of raw <c>/responses</c>. That probe records an
    ///     all-inert sweep as a real outcome rather than a regression, and this file must not hold the
    ///     opposite verdict on an identical observation, so the per-model split is logged and never
    ///     asserted. An earlier revision threw when every model stayed silent; a run where none reasons
    ///     is exactly what the other probe has already observed happening.
    ///
    ///     Nothing is lost by that. That a client sending no <c>thinking</c> field still gets
    ///     <c>summary: "auto"</c> and no <c>effort</c> is pinned deterministically at fixture level by
    ///     <c>AnthropicToResponsesRequestTests.Always_asks_for_reasoning_summaries</c>, and the chain
    ///     from summary events to an Anthropic <c>thinking</c> block is ENFORCED end-to-end by
    ///     <see cref="Thinking_enabled_makes_a_none_effort_model_reason" />, which sends an effort and is
    ///     far more reliable for that reason.
    /// </summary>
    [SkippableFact]
    public async Task Translated_route_serves_every_model_without_a_reasoning_hint()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(600));

        var models = await ResponsesCapableModelsAsync(cts.Token);
        Skip.If(models.Count == 0, "This Copilot account exposes no /responses model the proxy will serve.");

        var thought = new List<string>();
        var silent = new List<string>();
        foreach (var model in models)
        {
            // Deliberately NOT asking for reasoning anywhere in this request: the point is that the
            // translator adds it. A client with extended thinking switched off sends exactly this.
            var (status, body) = await SendProxyAsync(
                "/v1/messages",
                new
                {
                    model,
                    max_tokens = 512,
                    stream = true,
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = "A bat and a ball cost $1.10 together. The bat costs $1.00 more than "
                                + "the ball. How much does the ball cost? Answer with the amount only.",
                        },
                    },
                },
                cts.Token
            );

            status.Should().Be(HttpStatusCode.OK, "{0} failed the translated route with: {1}", model, Truncate(body, 400));

            if (body.Contains("\"type\":\"thinking\"", StringComparison.Ordinal))
            {
                _output.WriteLine($"{model,-24} -> 200 OK, thinking block");

                // The first one only, as the evidence sample: these streams run to hundreds of lines
                // and the per-model split below is what a reader of this log actually needs.
                if (thought.Count == 0)
                {
                    _output.WriteLine(Truncate(body, 1500));
                }

                thought.Add(model);
                continue;
            }

            _output.WriteLine($"{model,-24} -> 200 OK, no thinking block");
            silent.Add(model);
        }

        // Recorded, never asserted — see the summary above. An all-silent sweep of this request shape
        // is an outcome `Summary_auto_alone_is_accepted_everywhere_but_is_not_dependable` has already
        // observed on the raw endpoint, and this probe cannot call the same observation a regression.
        _output.WriteLine($"OBSERVED a thinking block from:  {(thought.Count == 0 ? "<none>" : string.Join(", ", thought))}");
        _output.WriteLine($"OBSERVED no thinking block from: {(silent.Count == 0 ? "<none>" : string.Join(", ", silent))}");
    }

    /// <summary>
    ///     Settles whether Copilot reports its cached prefix INSIDE the total <c>input_tokens</c> or
    ///     alongside it. The README warns that the proxy over-reports cached input, and the size of that
    ///     over-report depends entirely on which of the two it is — <c>cached_tokens: 0</c>, the only
    ///     thing earlier probes ever saw, cannot tell them apart.
    ///
    ///     Method: send the same long prefix twice. The second call should hit the cache, and then
    ///     <c>input_tokens</c> either stays put (inclusive) or drops by roughly the cached count
    ///     (exclusive). Verdict is logged rather than asserted, because whether Copilot caches at all on
    ///     a given day is its decision, not this test's — but both calls MUST succeed, or the run proves
    ///     nothing and says so.
    /// </summary>
    [SkippableFact]
    public async Task Cached_input_tokens_are_reported_inside_the_total()
    {
        Skip.IfNot(_fixture.Available, _fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        var model = await PickResponsesOnlyModelAsync(cts.Token);

        // Automatic prompt caching has a minimum prefix length, so this one probe cannot be tiny.
        // Deterministic, because a cache hit needs the two prefixes to be byte-identical.
        var prefix = string.Join(
            "\n",
            Enumerable.Range(0, 400).Select(i => $"Fact {i}: the value of item number {i} is {i * 7 % 43}.")
        );

        var usages = new List<(int Input, int Cached, int Total)>();
        foreach (var attempt in new[] { "first", "second" })
        {
            var (status, body) = await PostRawAsync(
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
                            content = new[] { new { type = "input_text", text = $"{prefix}\n\nReply with: ok" } },
                        },
                    },
                },
                cts.Token
            );

            status.Should().Be(HttpStatusCode.OK, "the {0} call must succeed or the comparison is meaningless", attempt);

            using var doc = JsonDocument.Parse(body);
            var usage = doc.RootElement.GetProperty("usage");
            var input = usage.GetProperty("input_tokens").GetInt32();
            var total = usage.GetProperty("total_tokens").GetInt32();
            var cached = usage.TryGetProperty("input_tokens_details", out var details)
                && details.TryGetProperty("cached_tokens", out var cachedTokens)
                ? cachedTokens.GetInt32()
                : 0;

            _output.WriteLine($"{attempt,-7} call -> input_tokens: {input}  cached_tokens: {cached}  total_tokens: {total}");
            usages.Add((input, cached, total));
        }

        var (firstInput, _, _) = usages[0];
        var (secondInput, secondCached, _) = usages[1];

        if (secondCached == 0)
        {
            _output.WriteLine(
                "OBSERVED: Copilot reported no cache hit on the repeated prefix, so this run CANNOT "
                    + "distinguish an inclusive from an exclusive cached_tokens. Nothing is proven either way."
            );
            return;
        }

        // Inclusive: input_tokens counts the cached prefix too, so it barely moves between the calls.
        // Exclusive: it drops by about the cached count.
        var verdict = Math.Abs(secondInput - firstInput) <= secondCached / 2 ? "INSIDE the total" : "ALONGSIDE the total";
        _output.WriteLine(
            $"OBSERVED: cached_tokens {secondCached} is reported {verdict} "
                + $"(input_tokens {firstInput} -> {secondInput})."
        );
    }

    /// <summary>
    ///     Every model the proxy serves ONLY through <c>/responses</c>, cheap tiers first.
    ///
    ///     Excluding models that also advertise <c>/v1/messages</c> is not cosmetic:
    ///     <c>ModelRouter.Resolve</c> tries its Messages-passthrough arm FIRST, so an Anthropic request
    ///     naming a dual-endpoint model never reaches the translator. A sweep that included one could
    ///     report success for a model that never exercised the code under test.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResponsesCapableModelsAsync(CancellationToken cancellationToken)
    {
        var catalog = await _fixture.GetCatalogAsync(cancellationToken);
        return
        [
            .. catalog
                .Where(m => m.Advertises(ModelRouter.ResponsesPath))
                .Where(m => !m.Advertises(ModelRouter.MessagesPath))
                .Where(m => !ProxyExcludedVendors.Contains(m.Vendor, StringComparer.OrdinalIgnoreCase))
                .Select(m => m.Id)
                .OrderBy(id => CheapModelHints.Any(h => id.Contains(h, StringComparison.OrdinalIgnoreCase)) ? 0 : 1)
                .ThenBy(id => id, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>The reasoning-related payload types seen in a capture, first occurrence order.</summary>
    private static List<string> ReasoningEventsIn(RawSseCapture capture) =>
        [.. capture.PayloadTypes.Where(t => t.Contains("reasoning", StringComparison.Ordinal))];

    /// <summary>
    ///     The <c>reasoning.effort</c> Copilot echoed on the stream's first frame. This is the field
    ///     that decides whether a summary request does anything: at <c>"none"</c> the turn never reasons.
    /// </summary>
    private static string EchoedReasoningEffort(RawSseCapture capture)
    {
        foreach (var line in capture.Lines.Where(l => l.StartsWith("data:", StringComparison.Ordinal)))
        {
            try
            {
                using var doc = JsonDocument.Parse(line["data:".Length..].Trim());
                if (
                    doc.RootElement.TryGetProperty("response", out var response)
                    && response.TryGetProperty("reasoning", out var reasoning)
                    && reasoning.ValueKind == JsonValueKind.Object
                    && reasoning.TryGetProperty("effort", out var effort)
                )
                {
                    return effort.ToString();
                }
            }
            catch (JsonException)
            {
                // Not every frame carries a parseable response envelope; keep looking.
            }
        }

        return "<absent>";
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
    private static object ReasoningResponsesRequest(string model, ReasoningRequest reasoning)
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

        // effort as well as summary in the third case: Copilot defaults these models to effort "none",
        // and a turn that never reasons cannot summarise.
        object? reasoningField = reasoning switch
        {
            ReasoningRequest.SummaryOnly => new { summary = "auto" },
            ReasoningRequest.EffortAndSummary => new { effort = "medium", summary = "auto" },
            _ => null,
        };

        if (reasoningField is not null)
        {
            request["reasoning"] = reasoningField;
        }

        return request;
    }

    /// <summary>How a probe asks for reasoning: not at all, summary only, or summary at a set effort.</summary>
    private enum ReasoningRequest
    {
        None,
        SummaryOnly,
        EffortAndSummary,
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
