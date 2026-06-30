using System.Text;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>Verifies raw SSE passthrough, client-cancellation propagation, and mid-stream error handling.</summary>
public sealed class StreamingTests
{
    private static StringContent RequestBody() =>
        new("{\"model\":\"x\",\"max_tokens\":5,\"stream\":true}", Encoding.UTF8, "application/json");

    [Fact]
    public async Task Sse_response_is_relayed_verbatim_with_no_buffering_header()
    {
        const string sse =
            "event: message_start\ndata: {\"type\":\"message_start\"}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"hi\"}}\n\n"
            + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

        await using var factory = new ProxyWebAppFactory((req, ct) => Task.FromResult(TestUpstream.Sse(sse)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/messages", RequestBody());

        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Headers.GetValues("X-Accel-Buffering").Should().ContainSingle().Which.Should().Be("no");
        (await response.Content.ReadAsStringAsync()).Should().Be(sse);
    }

    [Fact]
    public async Task Client_disconnect_cancels_the_upstream_read()
    {
        var upstreamStream = new CancellationObservingStream("event: ping\ndata: {}\n\n");
        await using var factory = new ProxyWebAppFactory((req, ct) =>
            Task.FromResult(TestUpstream.SseStream(upstreamStream)));
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = RequestBody() };

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStreamAsync();

        // Drain the first frame so the proxy is blocked on the next upstream read.
        var buffer = new byte[256];
        _ = await body.ReadAsync(buffer);

        // Simulate the client disconnecting mid-stream; this aborts the request (HttpContext.RequestAborted).
        body.Dispose();
        response.Dispose();

        // The proxy's linked token must cancel the upstream read.
        await upstreamStream.Cancelled.WaitAsync(TimeSpan.FromSeconds(10));
        upstreamStream.Cancelled.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task First_sse_frame_is_flushed_before_upstream_completes()
    {
        // Upstream emits frame "a", then blocks until released, then emits frame "b". If the proxy
        // buffered the whole upstream before flushing, the first client read would block until Release()
        // and time out. Observing frame "a" before releasing proves incremental flush.
        var gated = new GatedStream(
            "event: a\ndata: {\"type\":\"content_block_delta\",\"i\":0}\n\n",
            "event: b\ndata: {\"type\":\"content_block_delta\",\"i\":1}\n\n");
        await using var factory = new ProxyWebAppFactory((req, ct) =>
            Task.FromResult(TestUpstream.SseStream(gated)));
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = RequestBody() };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();

        var buffer = new byte[256];
        var read = await body.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Encoding.UTF8.GetString(buffer, 0, read).Should().Contain("event: a",
            "the first frame must reach the client before the upstream produces the rest");

        gated.Release();
        var rest = await new StreamReader(body).ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
        rest.Should().Contain("event: b");
    }

    [Fact]
    public async Task Mid_stream_upstream_failure_truncates_without_fabricating_frames()
    {
        // Raw passthrough: a mid-stream upstream failure (after some frames were relayed) must NOT
        // synthesize an SSE error or a (misleading) message_stop — the stream is simply truncated.
        var upstreamStream = new ThrowingStream("event: content_block_delta\ndata: {\"type\":\"content_block_delta\"}\n\n");
        await using var factory = new ProxyWebAppFactory((req, ct) =>
            Task.FromResult(TestUpstream.SseStream(upstreamStream)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/messages", RequestBody());
        var text = await response.Content.ReadAsStringAsync();

        text.Should().Contain("content_block_delta", "frames received before the failure are relayed verbatim");
        text.Should().NotContain("event: error", "raw passthrough must not fabricate an SSE error frame");
        text.Should().NotContain("message_stop", "a truncated/failed stream must not claim normal completion");
    }
}
