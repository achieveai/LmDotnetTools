using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Recovery;

/// <summary>
/// Proves stream recovery over a REAL provider agent and a REAL HTTP transport, rather than a mock
/// agent that throws on command.
/// </summary>
/// <remarks>
/// <para>
/// Every other recovery test hands the loop the exact exception it classifies, which assumes the one
/// thing worth checking: that a genuine truncated SSE body still LOOKS like a truncated SSE body by
/// the time it leaves the provider stack. A single <c>catch</c> in the client or the agent that
/// re-wrapped the fault — into an <see cref="InvalidOperationException"/>, or worse a
/// <see cref="TaskCanceledException"/>, which the classifier reads as a user cancellation — would
/// silently turn every recoverable blip into a failed run, and no mock-agent test could see it.
/// </para>
/// <para>
/// OpenAI Responses is the vehicle because its client is the thinnest of the three over the SSE
/// enumeration; the property under test belongs to all of them.
/// </para>
/// </remarks>
public class ProviderStreamTruncationRecoveryTests
{
    /// <summary>Four words, so the reply is stable and the truncation point has deltas to cut.</summary>
    private const string ScriptedReplyPrompt = """
        <|instruction_start|>
        {"instruction_chain":[
            {"messages":[{"text_message":{"length":4}}]}
        ]}
        <|instruction_end|>
        """;

    [Fact]
    public async Task ATruncatedSseBodyFromARealProviderAgent_IsRecovered_NotFailed()
    {
        using var transport = new TruncateFirstResponseHandler();
        using var http = new HttpClient(transport) { BaseAddress = new Uri("http://mock.local/") };
        using var agent = new OpenAiResponsesAgent("truncation-agent", new OpenAiResponsesClient(http));

        await using var loop = new MultiTurnAgentLoop(
            agent,
            new FunctionRegistry(),
            $"thread-{Guid.NewGuid():N}",
            includeAskUserQuestionTool: false,
            includeNotifyClientTool: false,
            store: new InMemoryConversationStore()
        );

        // Bounded rather than unbounded: if recovery ever stopped happening, the run would sit waiting
        // on a stream that is never coming, and a test that hangs reports nothing.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = loop.RunAsync(cts.Token);

        var collected = new List<IMessage>();
        await foreach (
            var message in loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = ScriptedReplyPrompt, Role = Role.User }]),
                cts.Token
            )
        )
        {
            collected.Add(message);
        }

        // The transport cut the first body mid-delta. Reaching a second POST at all is the proof that
        // the resulting HttpIOException survived the client's SSE reader, the agent's event mapping,
        // and the loop's own catch filter with its ResponseEnded classification intact.
        transport
            .RequestCount.Should()
            .Be(
                2,
                "the truncated attempt is retried exactly once, which requires the interruption to have "
                    + "been classified as recoverable rather than fatal"
            );

        var abandoned = collected.OfType<GenerationAbandonedMessage>().Should().ContainSingle().Subject;
        var reply = collected
            .OfType<TextMessage>()
            .Where(m => m.Role == Role.Assistant)
            .Should()
            .ContainSingle("only the replacement attempt finishes a message")
            .Subject;

        reply.Text.Trim().Split(' ').Should().HaveCount(4, "the retry received the whole scripted reply");
        reply
            .GenerationId.Should()
            .NotBe(
                abandoned.GenerationId,
                "the replacement work is a new generation, so a client can discard the abandoned one wholesale"
            );

        collected
            .OfType<RunCompletedMessage>()
            .Should()
            .AllSatisfy(run => run.IsError.Should().BeFalse("a recovered interruption is invisible to the caller"));
    }

    /// <summary>
    /// Serves a real mock Responses stream, but cuts the first response short mid-delta and ends it
    /// the way a dropped connection does.
    /// </summary>
    /// <remarks>
    /// It delegates to <see cref="OpenAiResponsesTestSseMessageHandler"/> rather than hand-writing SSE
    /// so the bytes before the cut are exactly the bytes the provider stack normally parses — a
    /// hand-rolled prefix could fail for a reason that has nothing to do with truncation.
    /// </remarks>
    private sealed class TruncateFirstResponseHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new(new OpenAiResponsesTestSseMessageHandler { ChunkDelayMs = 0 });

        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var attempt = Interlocked.Increment(ref _requestCount);
            var full = await _inner.SendAsync(request, cancellationToken);
            if (attempt > 1)
            {
                return full;
            }

            using (full)
            {
                var body = await full.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new TruncatedSseStream(CutAfterFirstDelta(body))),
                };
            }
        }

        /// <summary>
        /// Everything up to and including the first text-delta frame. Cutting on a frame boundary
        /// rather than a byte count keeps the surviving prefix parseable, so the interruption is the
        /// only thing the test can be failing on.
        /// </summary>
        private static string CutAfterFirstDelta(string sse)
        {
            var delta = sse.IndexOf("response.output_text.delta", StringComparison.Ordinal);
            delta.Should().BeGreaterThan(-1, "the scripted reply streams its text as deltas");

            var frameEnd = sse.IndexOf("\n\n", delta, StringComparison.Ordinal);
            frameEnd.Should().BeGreaterThan(-1, "the delta frame is terminated");
            return sse[..(frameEnd + 2)];
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Emits a fixed prefix, then fails the way a connection dropped mid-body does.</summary>
    private sealed class TruncatedSseStream(string prefix) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        private int _position;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _position);
                _prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return ValueTask.FromResult(count);
            }

            // The exact exception HttpClient raises when a response body ends before its declared or
            // chunk-framed end. Constructing it here is what makes this a transport-level test.
            throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

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

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
