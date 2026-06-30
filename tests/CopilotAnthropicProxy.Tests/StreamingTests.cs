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
    public async Task Mid_stream_upstream_failure_writes_terminal_sse_error_and_message_stop()
    {
        var upstreamStream = new ThrowingStream("event: content_block_delta\ndata: {\"type\":\"content_block_delta\"}\n\n");
        await using var factory = new ProxyWebAppFactory((req, ct) =>
            Task.FromResult(TestUpstream.SseStream(upstreamStream)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/messages", RequestBody());
        var text = await response.Content.ReadAsStringAsync();

        text.Should().Contain("content_block_delta", "the frames received before the failure are relayed");
        text.Should().Contain("event: error");
        text.Should().Contain("\"type\":\"api_error\"");
        text.Should().Contain("event: message_stop");
    }
}
