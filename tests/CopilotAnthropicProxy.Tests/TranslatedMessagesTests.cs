using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>
///     End-to-end coverage for the TRANSLATED branch of <c>/v1/messages</c>: an Anthropic Messages
///     request for a model that only advertises <c>/responses</c> is rewritten into a Responses
///     request, forwarded, and the reply translated back — buffered and streaming.
/// </summary>
public class TranslatedMessagesTests
{
    private const string DiscoveryJson = """
        {"data":[
          {"id":"claude-opus-4.8","vendor":"Anthropic","supported_endpoints":["/v1/messages","/chat/completions"]},
          {"id":"gpt-5.3-codex","vendor":"OpenAI","supported_endpoints":["/responses"]}
        ]}
        """;

    private static ProxyWebAppFactory Factory(
        Func<HttpRequestMessage, string, Task<HttpResponseMessage>> onProxied,
        int? idleTimeoutSeconds = null,
        int? keepAliveSeconds = null
    ) =>
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
            model: null,
            idleTimeoutSeconds: idleTimeoutSeconds,
            keepAliveSeconds: keepAliveSeconds
        );

    private static object TranslatedRequest(object messages, int maxTokens = 1024, bool stream = false) =>
        new
        {
            model = "gpt-5.3-codex",
            max_tokens = maxTokens,
            stream,
            messages,
        };

    /// <summary>
    ///     Splits an SSE body into ordered <c>(event, data)</c> pairs. Substring assertions cannot see a
    ///     duplicated or misordered frame, so every streaming test asserts the exact event SEQUENCE.
    /// </summary>
    private static IReadOnlyList<(string Event, string Data)> Frames(string sse)
    {
        var frames = new List<(string, string)>();
        foreach (var block in sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? name = null;
            var data = string.Empty;
            foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    name = line[7..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line[6..];
                }
            }

            if (name is not null)
            {
                frames.Add((name, data));
            }
        }

        return frames;
    }

    [Fact]
    public async Task A_responses_only_model_is_reached_via_the_responses_endpoint()
    {
        string? path = null;
        string? body = null;

        await using var factory = Factory(
            async (request, seen) =>
            {
                path = seen;
                body = await request.Content!.ReadAsStringAsync();
                return TestUpstream.Json(
                    """
                    {"id":"resp_1","model":"gpt-5.3-codex",
                     "output":[{"type":"message","content":[{"type":"output_text","text":"Hi there"}]}],
                     "usage":{"input_tokens":4,"output_tokens":2}}
                    """
                );
            }
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            new
            {
                model = "gpt-5.3-codex",
                max_tokens = 1024,
                messages = new[] { new { role = "user", content = "Hello" } },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        path.Should().Be("/responses");

        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("max_output_tokens").GetInt32().Should().Be(1024);
        sent.RootElement.GetProperty("store").GetBoolean().Should().BeFalse();
        sent.RootElement.GetProperty("model").GetString().Should().Be("gpt-5.3-codex");

        using var received = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        received.RootElement.GetProperty("type").GetString().Should().Be("message");
        received.RootElement.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Hi there");
    }

    [Fact]
    public async Task A_streaming_request_is_translated_frame_by_frame()
    {
        await using var factory = Factory(
            (_, _) =>
                Task.FromResult(
                    TestUpstream.Sse(
                        string.Concat(
                            """
                            event: response.created
                            data: {"type":"response.created","response":{"id":"resp_2","model":"gpt-5.3-codex"}}


                            """.ReplaceLineEndings("\n"),
                            """
                            event: response.output_text.delta
                            data: {"type":"response.output_text.delta","delta":"Hello"}


                            """.ReplaceLineEndings("\n"),
                            """
                            event: response.completed
                            data: {"type":"response.completed","response":{"output":[],"usage":{"input_tokens":4,"output_tokens":1}}}


                            """.ReplaceLineEndings("\n")
                        )
                    )
                )
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            new
            {
                model = "gpt-5.3-codex",
                max_tokens = 1024,
                stream = true,
                messages = new[] { new { role = "user", content = "Hello" } },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        var stream = await response.Content.ReadAsStringAsync();
        stream.Should().Contain("event: message_start");
        stream.Should().Contain("\"type\":\"text_delta\",\"text\":\"Hello\"");
        stream.Should().Contain("event: message_stop");
        stream.Should().NotContain("response.created", "upstream frames must not leak through");

        // Exact sequence: a substring assertion cannot see a repeated message_start or a delta emitted
        // outside its block, which is the whole failure mode of a frame-by-frame translator.
        Frames(stream)
            .Select(f => f.Event)
            .Should()
            .Equal(
                "message_start",
                "content_block_start",
                "content_block_delta",
                "content_block_stop",
                "message_delta",
                "message_stop"
            );
    }

    [Fact]
    public async Task The_model_validation_probe_returns_a_well_formed_empty_message()
    {
        // Claude Code's first request against any new model: max_tokens 1, one text block, no retries.
        string? body = null;

        await using var factory = Factory(
            async (request, _) =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return TestUpstream.Json(
                    """{"id":"resp_3","model":"gpt-5.3-codex","output":[],"usage":{"input_tokens":3,"output_tokens":0}}"""
                );
            }
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            new
            {
                model = "gpt-5.3-codex",
                max_tokens = 1,
                messages = new[]
                {
                    new { role = "user", content = new[] { new { type = "text", text = "Hi" } } },
                },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var sent = JsonDocument.Parse(body!);
        sent.RootElement.GetProperty("max_output_tokens")
            .GetInt32()
            .Should()
            .Be(16, "Responses rejects max_output_tokens below 16 and Claude Code would reject the model");

        using var received = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        received.RootElement.GetProperty("type").GetString().Should().Be("message");
        received.RootElement.GetProperty("content").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task An_upstream_error_is_reported_in_the_anthropic_error_shape()
    {
        await using var factory = Factory(
            (_, _) =>
                Task.FromResult(
                    TestUpstream.Json(
                        """{"error":{"message":"model is overloaded"}}""",
                        HttpStatusCode.ServiceUnavailable
                    )
                )
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            new
            {
                model = "gpt-5.3-codex",
                max_tokens = 100,
                messages = new[] { new { role = "user", content = "Hi" } },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("type").GetString().Should().Be("error");
        error.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Contain("overloaded");
    }

    [Fact]
    public async Task An_upstream_error_body_of_an_unexpected_shape_still_produces_an_error_envelope()
    {
        // "error" as a STRING rather than an object: indexing it as one throws InvalidOperationException,
        // which would turn a clean upstream error into an unhandled 500. The upstream status is 429 and
        // not 500 precisely so that an unhandled 500 cannot masquerade as a correctly relayed one.
        await using var factory = Factory(
            (_, _) => Task.FromResult(TestUpstream.Json("""{"error":"boom"}""", HttpStatusCode.TooManyRequests))
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            TranslatedRequest(new[] { new { role = "user", content = "Hi" } })
        );

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the upstream status must survive the reshape");

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("type").GetString().Should().Be("error");
        error.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Contain("boom");
    }

    [Fact]
    public async Task An_oversized_upstream_error_message_is_capped_before_it_reaches_the_client()
    {
        // Upstream-authored text is relayed verbatim, so its LENGTH is the upstream's choice unless the
        // proxy caps it. A backend that echoes the offending request back inside `error.message` would
        // otherwise turn one bad call into a megabyte of client-visible noise.
        var huge = new string('x', 4096);
        await using var factory = Factory(
            (_, _) =>
                Task.FromResult(
                    TestUpstream.Json(
                        "{\"error\":{\"message\":\"" + huge + "\"}}",
                        HttpStatusCode.ServiceUnavailable
                    )
                )
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            TranslatedRequest(new[] { new { role = "user", content = "Hi" } })
        );

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("error").GetProperty("message").GetString().Should().HaveLength(500);
    }

    [Fact]
    public async Task An_untranslatable_upstream_reply_is_reported_as_400_not_500()
    {
        // ResponsesToAnthropicJson.Translate documents ArgumentException for a reply it cannot read, and
        // documents that this caller branches on it to answer 400 rather than leaking a 500.
        await using var factory = Factory((_, _) => Task.FromResult(TestUpstream.Json("<html>not json</html>")));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            TranslatedRequest(new[] { new { role = "user", content = "Hi" } })
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("type").GetString().Should().Be("error");
        error.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("invalid_request_error");
    }

    [Fact]
    public async Task A_request_the_translator_cannot_read_is_rejected_with_400_without_calling_upstream()
    {
        var upstreamCalls = 0;
        await using var factory = Factory(
            (_, _) =>
            {
                upstreamCalls++;
                return Task.FromResult(TestUpstream.Json("{}"));
            }
        );
        using var client = factory.CreateClient();

        // max_tokens as a string: the request translator reads it as a number and throws. The client's
        // own body is at fault, so this is a 400 — and the upstream must never see it.
        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            new
            {
                model = "gpt-5.3-codex",
                max_tokens = "lots",
                messages = new[] { new { role = "user", content = "Hi" } },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        upstreamCalls.Should().Be(0, "a body we cannot translate must never reach Copilot");

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("invalid_request_error");
    }

    [Fact]
    public async Task An_echoed_unsigned_thinking_block_is_dropped_rather_than_forwarded_or_rejected()
    {
        // The streaming translator emits `thinking` blocks with no `signature` (fabricating one is
        // forbidden), so a client that echoes assistant content back sends an UNSIGNED thinking block
        // straight into the request translator on the next turn.
        string? body = null;

        await using var factory = Factory(
            async (request, _) =>
            {
                body = await request.Content!.ReadAsStringAsync();
                return TestUpstream.Json("""{"id":"resp_4","model":"gpt-5.3-codex","output":[]}""");
            }
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            TranslatedRequest(
                new object[]
                {
                    new { role = "user", content = "Hi" },
                    new
                    {
                        role = "assistant",
                        content = new object[]
                        {
                            new { type = "thinking", thinking = "weighing the options" },
                            new { type = "text", text = "Hello" },
                        },
                    },
                    new { role = "user", content = "Again" },
                }
            )
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        body.Should().NotBeNull();
        var forwarded = body!;
        forwarded.Should()
            .NotContain("thinking", "an unsigned thinking block cannot be replayed and must not be forwarded");
        forwarded.Should().NotContain("weighing the options");

        using var sent = JsonDocument.Parse(forwarded);
        var input = sent.RootElement.GetProperty("input");
        input.GetArrayLength().Should().Be(3, "the assistant turn survives, carrying only its text block");
        input[1].GetProperty("role").GetString().Should().Be("assistant");
        input[1].GetProperty("content").GetArrayLength().Should().Be(1);
        input[1].GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Hello");
    }

    [Fact]
    public async Task Count_tokens_is_not_served_for_a_responses_only_model()
    {
        var upstreamCalls = 0;
        await using var factory = Factory(
            (_, _) =>
            {
                upstreamCalls++;
                return Task.FromResult(TestUpstream.Json("{}"));
            }
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages/count_tokens",
            new
            {
                model = "gpt-5.3-codex",
                messages = new[] { new { role = "user", content = "Hi" } },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        upstreamCalls.Should().Be(0, "a token count must never be answered by running a generation");

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("not_found_error");
    }

    [Fact]
    public async Task A_slow_but_steady_stream_is_not_killed_for_taking_longer_than_the_idle_timeout()
    {
        // Three frames 1.2s apart is 3.6s of wall clock against a 3s idle timeout. The timeout measures
        // the GAP BETWEEN BYTES, so a long generation must survive; a total-duration clock would cut it
        // off after the first frame. The per-gap margin is 1.8s — deliberately wide, because the failure
        // this test would otherwise produce on a loaded machine is a flake, not a finding.
        var paced = new PacedStream(
            TimeSpan.FromMilliseconds(1200),
            "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_5\",\"model\":\"gpt-5.3-codex\"}}\n\n",
            "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"slow\"}\n\n",
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n"
        );

        await using var factory = Factory(
            (_, _) => Task.FromResult(TestUpstream.SseStream(paced)),
            idleTimeoutSeconds: 3
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            TranslatedRequest(new[] { new { role = "user", content = "Hi" } }, stream: true)
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var stream = await response.Content.ReadAsStringAsync();
        Frames(stream)
            .Select(f => f.Event)
            .Should()
            .Equal(
                "message_start",
                "content_block_start",
                "content_block_delta",
                "content_block_stop",
                "message_delta",
                "message_stop"
            );
    }

    [Fact]
    public async Task A_silent_upstream_is_covered_by_keep_alive_pings()
    {
        var gated = new GatedStream(
            "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_6\",\"model\":\"gpt-5.3-codex\"}}\n\n",
            "event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n"
        );

        await using var factory = Factory((_, _) => Task.FromResult(TestUpstream.SseStream(gated)), keepAliveSeconds: 1);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(TranslatedRequest(new[] { new { role = "user", content = "Hi" } }, stream: true)),
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();

        var seen = await ReadUntilAsync(body, s => s.Contains("event: ping", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
        seen.Should().Contain("event: message_start");
        seen.Should()
            .Contain("event: ping", "a silent upstream must be covered by pings or an intermediary drops the connection");

        gated.Release();
        var rest = await new StreamReader(body).ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
        rest.Should().Contain("event: message_stop", "real frames still flow after the keep-alives");
    }

    [Fact]
    public async Task A_streaming_request_answered_with_a_non_streaming_reply_is_reported_as_502()
    {
        // Copilot answering `stream: true` with application/json would translate to ZERO frames, i.e. an
        // empty 200 indistinguishable from a successful empty turn. Nothing has been written at this
        // point, so the envelope is still writable and the failure is visible instead of silent.
        await using var factory = Factory(
            (_, _) => Task.FromResult(TestUpstream.Json("""{"id":"resp_7","model":"gpt-5.3-codex","output":[]}"""))
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            TranslatedRequest(new[] { new { role = "user", content = "Hi" } }, stream: true)
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        response.Content.Headers.ContentType!.MediaType.Should()
            .Be("application/json", "the reply is an error envelope, not the SSE stream that was asked for");

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("type").GetString().Should().Be("error");
        error.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("api_error");
    }

    [Fact]
    public async Task A_stream_that_produces_no_frame_before_the_idle_timeout_answers_a_504_envelope()
    {
        // The upstream opens a real SSE response and then goes silent. Keep-alive is disabled on purpose:
        // a ping would START the response, and the 504 envelope is only writable while nothing has been
        // written. The lone SSE comment line keeps the stream open (an empty prefix would read as EOF and
        // complete the turn normally) while producing no Anthropic frame.
        var silent = new CancellationObservingStream(": awaiting-first-token\n");

        await using var factory = Factory(
            (_, _) => Task.FromResult(TestUpstream.SseStream(silent)),
            idleTimeoutSeconds: 1,
            keepAliveSeconds: 0
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            TranslatedRequest(new[] { new { role = "user", content = "Hi" } }, stream: true)
        );

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("type").GetString().Should().Be("error");
        error.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("api_error");
    }

    [Fact]
    public async Task A_mid_stream_upstream_failure_truncates_without_a_message_stop()
    {
        // Two upstream events reach the client, then the connection dies. The relay must stop there: a
        // synthetic message_stop would tell the client the turn ended normally, and a fabricated error
        // frame is not something the Anthropic stream grammar has a place for.
        var dropping = new ThrowingStream(
            "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_8\",\"model\":\"gpt-5.3-codex\"}}\n\n"
                + "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"half a th\"}\n\n"
        );

        await using var factory = Factory((_, _) => Task.FromResult(TestUpstream.SseStream(dropping)));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            TranslatedRequest(new[] { new { role = "user", content = "Hi" } }, stream: true)
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the failure arrived after the headers were sent");

        var stream = await response.Content.ReadAsStringAsync();
        stream.Should().Contain("half a th", "frames translated before the failure still reach the client");
        Frames(stream)
            .Select(f => f.Event)
            .Should()
            .Equal(
                "message_start",
                "content_block_start",
                "content_block_delta"
            );
    }

    [Fact]
    public async Task A_buffered_reply_that_stalls_mid_body_answers_a_504_envelope()
    {
        // 200 + headers, then the body stops arriving. This read pulls from the socket (the reply is only
        // headers-read so far) and the proxy's HttpClient has NO timeout, so if the idle token is not
        // handed to it nothing at all ends the request.
        var stalled = new CancellationObservingStream("""{"id":"resp_9","model":"gpt-5.3-codex",""");

        await using var factory = Factory(
            (_, _) => Task.FromResult(TestUpstream.JsonStream(stalled)),
            idleTimeoutSeconds: 1
        );
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            TranslatedRequest(new[] { new { role = "user", content = "Hi" } })
        );

        response.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("api_error");

        await stalled.Cancelled.WaitAsync(TimeSpan.FromSeconds(10));
        stalled.Cancelled.IsCompletedSuccessfully.Should()
            .BeTrue("the idle deadline must reach the upstream read, not merely abandon it");
    }

    [Fact]
    public async Task A_buffered_reply_whose_connection_drops_is_reported_as_502()
    {
        // A body that dies half-way through is an upstream fault, not a translation fault: it must be the
        // same 502 envelope the raw path produces, not an unhandled exception surfacing as a bare 500.
        var dropping = new ThrowingStream("""{"id":"resp_10","model":"gpt-5.3-codex",""");

        await using var factory = Factory((_, _) => Task.FromResult(TestUpstream.JsonStream(dropping)));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/v1/messages",
            TranslatedRequest(new[] { new { role = "user", content = "Hi" } })
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("type").GetString().Should().Be("error");
        error.RootElement.GetProperty("error").GetProperty("type").GetString().Should().Be("api_error");
    }

    [Fact]
    public async Task A_client_disconnect_cancels_the_translated_upstream_read()
    {
        // The translated relay must hand the client's abort to the upstream read, or a client that hangs
        // up leaves a generation billing away against a socket nobody is reading.
        var upstreamStream = new CancellationObservingStream(
            "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_11\",\"model\":\"gpt-5.3-codex\"}}\n\n"
        );

        await using var factory = Factory((_, _) => Task.FromResult(TestUpstream.SseStream(upstreamStream)));
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(TranslatedRequest(new[] { new { role = "user", content = "Hi" } }, stream: true)),
        };

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();

        // Drain message_start so the relay is parked on the next upstream read.
        var buffer = new byte[256];
        _ = await body.ReadAsync(buffer);

        body.Dispose();
        response.Dispose();

        await upstreamStream.Cancelled.WaitAsync(TimeSpan.FromSeconds(10));
        upstreamStream.Cancelled.IsCompletedSuccessfully.Should().BeTrue();
    }

    /// <summary>Reads the response stream until <paramref name="predicate"/> holds or the timeout elapses.</summary>
    private static async Task<string> ReadUntilAsync(Stream body, Func<string, bool> predicate, TimeSpan timeout)
    {
        var accumulated = new StringBuilder();
        var buffer = new byte[256];
        using var cts = new CancellationTokenSource(timeout);
        while (!predicate(accumulated.ToString()))
        {
            int read;
            try
            {
                read = await body.ReadAsync(buffer, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            accumulated.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return accumulated.ToString();
    }
}

/// <summary>
///     Emits each chunk only after a fixed gap has elapsed, so a stream can take longer in TOTAL than
///     the proxy's idle timeout while never leaving a gap between bytes that long.
/// </summary>
internal sealed class PacedStream : Stream
{
    private readonly Queue<byte[]> _chunks;
    private readonly TimeSpan _gap;
    private byte[]? _current;
    private int _offset;

    public PacedStream(TimeSpan gap, params string[] chunks)
    {
        _gap = gap;
        _chunks = new Queue<byte[]>(chunks.Select(Encoding.UTF8.GetBytes));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_current is null || _offset == _current.Length)
        {
            if (_chunks.Count == 0)
            {
                return 0;
            }

            await Task.Delay(_gap, cancellationToken).ConfigureAwait(false);
            _current = _chunks.Dequeue();
            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
