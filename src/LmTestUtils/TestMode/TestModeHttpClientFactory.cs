using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

/// <summary>
///     Creates HttpClient instances backed by test-mode SSE handlers.
/// </summary>
public static class TestModeHttpClientFactory
{
    public static HttpClient CreateOpenAiTestClient(
        ILoggerFactory? loggerFactory = null,
        RequestCaptureBase? capture = null,
        HttpStatusCode[]? statusSequence = null,
        int wordsPerChunk = 5,
        int chunkDelayMs = 0,
        string baseAddress = "http://test-mode/v1"
    )
    {
        loggerFactory ??= NullLoggerFactory.Instance;

        HttpMessageHandler handler = new TestSseMessageHandler(loggerFactory.CreateLogger<TestSseMessageHandler>())
        {
            WordsPerChunk = wordsPerChunk,
            ChunkDelayMs = chunkDelayMs,
        };

        if (statusSequence is { Length: > 0 })
        {
            handler = new StatusSequenceDelegatingHandler(statusSequence, handler);
        }

        if (capture != null)
        {
            handler = new RequestCaptureDelegatingHandler(capture, handler);
        }

        return new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
    }

    public static HttpClient CreateAnthropicTestClient(
        ILoggerFactory? loggerFactory = null,
        RequestCaptureBase? capture = null,
        HttpStatusCode[]? statusSequence = null,
        int wordsPerChunk = 5,
        int chunkDelayMs = 0,
        string baseAddress = "http://test-mode/v1"
    )
    {
        loggerFactory ??= NullLoggerFactory.Instance;

        HttpMessageHandler handler = new AnthropicTestSseMessageHandler(
            loggerFactory.CreateLogger<AnthropicTestSseMessageHandler>()
        )
        {
            WordsPerChunk = wordsPerChunk,
            ChunkDelayMs = chunkDelayMs,
        };

        if (statusSequence is { Length: > 0 })
        {
            handler = new StatusSequenceDelegatingHandler(statusSequence, handler);
        }

        if (capture != null)
        {
            handler = new RequestCaptureDelegatingHandler(capture, handler);
        }

        return new HttpClient(handler) { BaseAddress = new Uri(baseAddress) };
    }
}

internal sealed class RequestCaptureDelegatingHandler : DelegatingHandler
{
    private readonly RequestCaptureBase _capture;

    public RequestCaptureDelegatingHandler(RequestCaptureBase capture, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        await _capture.CaptureAsync(request);
        return await base.SendAsync(request, cancellationToken);
    }
}

internal sealed class StatusSequenceDelegatingHandler : DelegatingHandler
{
    private readonly HttpStatusCode[] _statusSequence;
    private int _requestIndex = -1;

    public StatusSequenceDelegatingHandler(HttpStatusCode[] statusSequence, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _statusSequence = statusSequence ?? throw new ArgumentNullException(nameof(statusSequence));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var currentIndex = Interlocked.Increment(ref _requestIndex);
        if (currentIndex < _statusSequence.Length)
        {
            var statusCode = _statusSequence[currentIndex];
            if (statusCode != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent($"Error {(int)statusCode}"),
                };
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
