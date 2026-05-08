using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
///     <see cref="HttpContent"/> that serializes a sequence of <see cref="ResponseEvent"/>
///     values produced by <see cref="OpenAiResponsesEventStreamWriter"/> as SSE-framed JSON.
///     Each event becomes a single <c>data: {json}\n\n</c> envelope; the framing intentionally
///     mirrors the OpenAI Responses API exactly.
/// </summary>
public sealed class OpenAiResponsesSseStreamHttpContent : HttpContent
{
    private const int DefaultChunkDelayMs = 50;

    private readonly IReadOnlyList<ResponseEvent> _events;
    private readonly int _chunkDelayMs;
    private readonly ILogger _logger;

    public OpenAiResponsesSseStreamHttpContent(
        IReadOnlyList<ResponseEvent> events,
        int chunkDelayMs = DefaultChunkDelayMs,
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(events);

        _events = events;
        _chunkDelayMs = Math.Max(0, chunkDelayMs);
        _logger = logger
            ?? LoggerFactory
                .Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
                .CreateLogger<OpenAiResponsesSseStreamHttpContent>();

        Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
    }

    /// <summary>
    ///     Convenience: build directly from an <see cref="InstructionPlan"/> via the writer.
    /// </summary>
    public static OpenAiResponsesSseStreamHttpContent FromPlan(
        InstructionPlan plan,
        string? model = null,
        int wordsPerChunk = 5,
        int chunkDelayMs = DefaultChunkDelayMs
    )
    {
        var events = OpenAiResponsesEventStreamWriter.Write(plan, model, wordsPerChunk);
        return new OpenAiResponsesSseStreamHttpContent(events, chunkDelayMs);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        return SerializeCoreAsync(stream, CancellationToken.None);
    }

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
    {
        var pipe = new Pipe();
        _ = Task.Run(
            async () =>
            {
                Exception? error = null;
                try
                {
                    using var writerStream = pipe.Writer.AsStream(false);
                    await SerializeCoreAsync(writerStream, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Responses SSE stream cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Responses SSE stream creation failed");
                    error = ex;
                }
                finally
                {
                    await pipe.Writer.CompleteAsync(error).ConfigureAwait(false);
                }
            },
            CancellationToken.None
        );

        return pipe.Reader.AsStream(false);
    }

    protected override Task<Stream> CreateContentReadStreamAsync()
    {
        return Task.FromResult(CreateContentReadStream(CancellationToken.None));
    }

    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(CreateContentReadStream(cancellationToken));
    }

    private async Task SerializeCoreAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = false,
        };

        foreach (var ev in _events)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var json = ResponseEventParser.ToJsonObject(ev).ToJsonString();
            await writer.WriteAsync("data: ").ConfigureAwait(false);
            await writer.WriteAsync(json).ConfigureAwait(false);
            await writer.WriteAsync("\n\n").ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (_chunkDelayMs > 0)
            {
                await Task.Delay(_chunkDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
