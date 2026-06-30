using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils.Http;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
///     Test-local transport that records every <see cref="HttpRequestMessage"/> the client sends
///     (count, request instance, cloned body string, Accept header), optionally fails the first
///     <see cref="FailFirst"/> attempts with <see cref="FailStatus"/>, then serves a fixed Responses
///     SSE stream. Responses and their body streams are tracked so disposal can be asserted.
/// </summary>
internal sealed class RecordingResponsesSseHandler : HttpMessageHandler
{
    private readonly string _sse;
    private int _count;

    public RecordingResponsesSseHandler(
        string sse,
        int failFirst = 0,
        HttpStatusCode failStatus = HttpStatusCode.BadGateway
    )
    {
        _sse = sse ?? throw new ArgumentNullException(nameof(sse));
        FailFirst = failFirst;
        FailStatus = failStatus;
    }

    /// <summary>Number of leading attempts to fail before serving the SSE stream.</summary>
    public int FailFirst { get; }

    /// <summary>Status returned for the failing attempts.</summary>
    public HttpStatusCode FailStatus { get; }

    /// <summary>Total number of <see cref="SendAsync"/> invocations observed.</summary>
    public int SendCount => Volatile.Read(ref _count);

    /// <summary>The request instance captured for each attempt (used to assert per-attempt freshness).</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>The cloned request body string captured for each attempt.</summary>
    public List<string> Bodies { get; } = [];

    /// <summary>The Accept header captured for each attempt.</summary>
    public List<string> AcceptHeaders { get; } = [];

    /// <summary>The (tracking) responses returned for each attempt.</summary>
    public List<TrackingHttpResponseMessage> Responses { get; } = [];

    /// <summary>Optional callback invoked with the 1-based attempt number before the response is built.</summary>
    public Action<int>? OnSend { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var n = Interlocked.Increment(ref _count);

        Requests.Add(request);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(CancellationToken.None);
        Bodies.Add(body);
        AcceptHeaders.Add(request.Headers.Accept.ToString());

        OnSend?.Invoke(n);

        TrackingHttpResponseMessage response;
        if (n <= FailFirst)
        {
            response = new TrackingHttpResponseMessage(FailStatus)
            {
                Content = new StringContent($"error {(int)FailStatus}"),
            };
        }
        else
        {
            var stream = new TrackingStream(Encoding.UTF8.GetBytes(_sse));
            response = new TrackingHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
                BodyStream = stream,
            };
        }

        Responses.Add(response);
        return response;
    }
}

/// <summary>Minimal in-memory <see cref="ILogger"/> that captures emitted entries for assertions.</summary>
internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        Entries.Add((logLevel, formatter(state, exception)));
    }
}
