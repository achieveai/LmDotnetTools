namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// Deterministic test seams for the slow-consumer drop/recovery scenarios: park the outbound pump,
/// hold a provider turn open, observe the sockets and the REST catch-up. Every wait in these helpers
/// is a condition wait — nothing here sleeps.
/// </summary>
internal static class StreamDropSignals
{
    /// <summary>
    /// Awaits a scenario signal, turning a hang into a named failure instead of an anonymous
    /// "test timed out" (the scenarios chain several of these, so the name is what makes a
    /// failure diagnosable).
    /// </summary>
    public static async Task WaitForAsync(this Task signal, string what, int timeoutMs = 30_000)
    {
        try
        {
            await signal.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Timed out after {timeoutMs} ms waiting for {what}.");
        }
    }
}

/// <summary>
/// Parks <c>ChatWebSocketManager</c>'s outbound pump so a subscriber is registered but drains nothing.
/// </summary>
/// <remarks>
/// This is what turns "the browser was slow" into arithmetic: with the pump parked, the agent's bounded
/// per-subscriber channel can only absorb its capacity, so publishing more than that MUST evict the
/// subscriber. Once <see cref="Release"/> is called the gate is permanently open, so the replacement
/// socket opened by the client's recovery is never parked.
/// </remarks>
internal sealed class PumpGate
{
    private readonly Func<string, bool> _applies;
    private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <param name="applies">
    /// Selects which stream to park by its thread id (<c>subagent-{agentId}</c> for a sub-agent focus
    /// view, the conversation id otherwise), so a scenario gates one view and leaves the rest live.
    /// </param>
    public PumpGate(Func<string, bool> applies)
    {
        _applies = applies;
    }

    /// <summary>Completes once the selected pump has actually parked (subscriber registered, draining stopped).</summary>
    public Task Parked => _parked.Task;

    public void Release()
    {
        _released.TrySetResult();
    }

    /// <summary>Matches <c>ChatWebSocketManager.OutboundPumpGate</c>.</summary>
    public Task WaitAsync(string threadId, CancellationToken cancellationToken)
    {
        if (_released.Task.IsCompleted || !_applies(threadId))
        {
            return Task.CompletedTask;
        }

        _parked.TrySetResult();
        return _released.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>
/// Holds one provider turn open: signals when the request arrives and only answers it when released.
/// </summary>
/// <remarks>
/// Two jobs in one seam. The arrival signal is a proof — the loop only asks for turn N+1 once turn N
/// has finished streaming — so a scenario can know a burst has been fully published without polling.
/// Holding the response then keeps the backend run in-flight, which is the precondition the client's
/// resume path checks before it re-subscribes.
/// </remarks>
internal sealed class HeldProviderTurn : DelegatingHandler
{
    private readonly int _turnOrdinal;
    private readonly Func<string, bool>? _matchesBody;
    private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _seen;

    /// <param name="inner">The scripted provider handler to forward to.</param>
    /// <param name="turnOrdinal">Which matching request to hold, 1-based.</param>
    /// <param name="matchesBody">
    /// Optional filter over the request body, so a scenario with more than one agent talking to the same
    /// scripted provider can count only one of them (match on the role's system-prompt marker). Null
    /// counts every request, which is what a single-agent scenario wants.
    /// </param>
    public HeldProviderTurn(HttpMessageHandler inner, int turnOrdinal, Func<string, bool>? matchesBody = null)
    {
        InnerHandler = inner;
        _turnOrdinal = turnOrdinal;
        _matchesBody = matchesBody;
    }

    /// <summary>Completes when the held turn has been requested (i.e. the previous turn finished streaming).</summary>
    public Task Arrived => _arrived.Task;

    public void Release()
    {
        _released.TrySetResult();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (await IsHeldTurnAsync(request, cancellationToken))
        {
            _arrived.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task<bool> IsHeldTurnAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_matchesBody is { } matches)
        {
            if (request.Content is null)
            {
                return false;
            }

            // Buffer before reading: the scripted responder reads the same body after we forward.
            await request.Content.LoadIntoBufferAsync();
            if (!matches(await request.Content.ReadAsStringAsync(cancellationToken)))
            {
                return false;
            }
        }

        return Interlocked.Increment(ref _seen) == _turnOrdinal;
    }

    protected override void Dispose(bool disposing)
    {
        // Never leave a scenario's cleanup blocked on a turn nobody released.
        _released.TrySetResult();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Passive Playwright WebSocket route: forwards every frame unchanged and records what the browser saw.
/// </summary>
/// <remarks>
/// Deliberately does not shape traffic. The scenarios assert the real wire (how many sockets, which
/// frames arrived on the dropped one), so the route must be an observer, not a participant.
/// </remarks>
internal sealed class WebSocketObserver
{
    /// <summary>The end-of-stream sentinel the server sends when a stream finished normally.</summary>
    public const string DoneFrame = "\"$type\":\"done\"";

    private readonly Func<int>? _sampleAtOpen;
    private readonly List<List<string>> _framesPerConnection = [];
    private readonly TaskCompletionSource _secondConnection =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondConnectionStreaming =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondConnectionDone =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new();
    private int _sampleAtSecondConnection = NotSampled;

    /// <summary>Sentinel for "no sample was taken", so an assertion against it fails rather than passes.</summary>
    public const int NotSampled = -1;

    /// <param name="sampleAtOpen">
    /// Read synchronously as each connection is created, so a scenario can prove ORDERING: a counter
    /// read after awaiting <see cref="SecondConnection"/> would also include whatever the client did
    /// after the socket existed. Null when a scenario asserts no ordering.
    /// </param>
    public WebSocketObserver(Func<int>? sampleAtOpen = null)
    {
        _sampleAtOpen = sampleAtOpen;
    }

    /// <summary>Completes when the client opens a second socket — i.e. it replaced the dropped stream.</summary>
    public Task SecondConnection => _secondConnection.Task;

    /// <summary>
    /// Completes when the second socket receives the end-of-stream sentinel. This is the settled-state
    /// signal for a view with no spinner of its own (the sub-agent focus transcript renders no typing
    /// indicator — see <c>MessageList.showTypingIndicator</c>), so reading its DOM before this would
    /// race content the replacement stream has not delivered yet.
    /// </summary>
    public Task SecondConnectionDone => _secondConnectionDone.Task;

    /// <summary>
    /// The <c>sampleAtOpen</c> counter as it stood the instant the second socket was created, or
    /// <see cref="NotSampled"/> when no counter was supplied / no second socket exists.
    /// </summary>
    public int SampleAtSecondConnection => Volatile.Read(ref _sampleAtSecondConnection);

    /// <summary>
    /// Completes when the second socket receives its first server frame. Opening a socket is not the
    /// same as being subscribed: the server only pumps once the subscription is registered, so this —
    /// not <see cref="SecondConnection"/> — is the signal to wait on before publishing content that
    /// the replacement stream is required to deliver live.
    /// </summary>
    public Task SecondConnectionStreaming => _secondConnectionStreaming.Task;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _framesPerConnection.Count;
            }
        }
    }

    /// <summary>Server→client text frames seen on the connection at <paramref name="index"/> (0-based).</summary>
    public IReadOnlyList<string> Frames(int index)
    {
        lock (_sync)
        {
            return [.. _framesPerConnection[index]];
        }
    }

    /// <summary>Matches the handler argument of <c>IPage.RouteWebSocketAsync</c>.</summary>
    public void Attach(IWebSocketRoute route)
    {
        List<string> frames = [];
        int ordinal;
        lock (_sync)
        {
            _framesPerConnection.Add(frames);
            ordinal = _framesPerConnection.Count;
        }

        if (ordinal == 2)
        {
            // Sampled BEFORE the open is announced, so the value cannot include later client work.
            Volatile.Write(ref _sampleAtSecondConnection, _sampleAtOpen?.Invoke() ?? NotSampled);
            _secondConnection.TrySetResult();
        }

        var server = route.ConnectToServer();
        server.OnMessage(frame =>
        {
            if (frame.Text is { } text)
            {
                lock (_sync)
                {
                    frames.Add(text);
                }

                if (ordinal == 2)
                {
                    _secondConnectionStreaming.TrySetResult();
                    if (text.Contains(DoneFrame, StringComparison.Ordinal))
                    {
                        _secondConnectionDone.TrySetResult();
                    }
                }

                route.Send(text);
            }
            else
            {
                route.Send(frame.Binary ?? []);
            }
        });
        route.OnMessage(frame =>
        {
            if (frame.Text is { } text)
            {
                server.Send(text);
            }
            else
            {
                server.Send(frame.Binary ?? []);
            }
        });
    }
}

/// <summary>
/// Counts the client's REST history reads, the observable half of "resync reloads before it re-subscribes".
/// </summary>
internal sealed class RestMessagesObserver
{
    private int _count;

    /// <param name="page">The page whose requests are watched.</param>
    /// <param name="urlMustContain">
    /// Narrows what counts, so a scenario measures the catch-up of the view under test rather than any
    /// history read the page happens to make (e.g. <c>/api/conversations/subagent-</c> for a focus view).
    /// </param>
    public RestMessagesObserver(IPage page, string urlMustContain = "/api/conversations/")
    {
        page.Request += (_, request) =>
        {
            if (request.Url.Contains(urlMustContain, StringComparison.Ordinal)
                && request.Url.Contains("/messages", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _count);
            }
        };
    }

    public int Count => Volatile.Read(ref _count);
}
