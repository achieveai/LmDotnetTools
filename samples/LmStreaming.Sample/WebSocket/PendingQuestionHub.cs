using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.Identity;

namespace LmStreaming.Sample.WebSocket;

/// <summary>
/// One <c>/ws/events</c> subscriber: the socket, plus the principal that opened it.
/// </summary>
/// <remarks>
/// The principal is CAPTURED, not read ambiently. A broadcast runs on the hub's own pump, outside
/// any HTTP request, where <see cref="IPrincipalAccessor.Current"/> is null — so an event filtered
/// against the ambient principal would either leak to everybody or reach nobody. Same reasoning as
/// <c>MultiTurnAgentPool.AgentEntry.CallerCredential</c>.
/// </remarks>
public sealed class QuestionEventSubscriber : IDisposable
{
    private readonly RegisteredWebSocketConnection _connection;

    internal QuestionEventSubscriber(RegisteredWebSocketConnection connection, Principal? principal)
    {
        _connection = connection;
        Principal = principal;
    }

    /// <summary>Registry-unique id for this subscriber.</summary>
    public string ConnectionId => _connection.ConnectionId;

    /// <summary>The principal that opened the socket, or null when enforcement is off.</summary>
    public Principal? Principal { get; }

    internal Task<bool> TrySendTextAsync(string json, CancellationToken ct) => _connection.TrySendTextAsync(json, ct);

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// The app-wide pending-question channel: an <see cref="IPendingQuestionObserver"/> on one side and
/// every connected <c>/ws/events</c> subscriber on the other.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The client used to find a question raised in another conversation by
/// sweeping transcripts on a timer: a partial sweep every 30 seconds and a full one every 5 minutes.
/// That is as fast as it can be, because the only cheap signal a client has —
/// <c>ConversationSummary.lastUpdated</c> — is written when a run COMPLETES, and a run parked on a
/// question has not completed. So a question raised in a conversation the user is not looking at was
/// invisible for up to five minutes. The loop knows the instant it parks; this carries that instant
/// to every open tab.
/// </para>
/// <para>
/// <b>One pump, in order.</b> Raises and settles are queued on an unbounded channel and applied by a
/// single consumer, so the pending map and the broadcasts share one total order: a client can never
/// be told a question settled before it was told the question existed. The observer side therefore
/// does no I/O and never blocks the agent loop, which is the contract
/// <see cref="IPendingQuestionObserver"/> states.
/// </para>
/// <para>
/// <b>Registration is inside that order.</b> <see cref="SubscribeAsync"/> takes the same gate the
/// pump holds while it applies an event, so a subscriber's snapshot is the map as of a point BETWEEN
/// two events, never during one. Without that, an event landing between "read the map" and "send the
/// snapshot" would be delivered and then overwritten by a snapshot that predates it.
/// </para>
/// <para>
/// <b>Authorization is per event, per subscriber.</b> The channel is app-wide but the conversations
/// on it are not: an event names a root thread, and a subscriber is told about it only if
/// <see cref="ConversationAuthorizer"/> would let them READ that conversation. With
/// <c>Identity:Enforce</c> off the authorizer short-circuits and everyone sees everything, exactly as
/// every other route behaves in that configuration.
/// </para>
/// </remarks>
public sealed class PendingQuestionHub : IPendingQuestionObserver, IAsyncDisposable
{
    /// <summary>Frame discriminator for the on-connect listing of everything already pending.</summary>
    public const string SnapshotType = "snapshot";

    /// <summary>Frame discriminator for a question that has just parked.</summary>
    public const string PendingType = "question_pending";

    /// <summary>Frame discriminator for a question that has stopped waiting.</summary>
    public const string SettledType = "question_settled";

    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptionsFactory.CreateForProduction();

    private readonly Channel<QuestionEvent> _events = Channel.CreateUnbounded<QuestionEvent>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    private readonly ConcurrentDictionary<string, QuestionEventSubscriber> _subscribers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingQuestion> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly WebSocketConnectionRegistry _sockets = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PendingQuestionHub> _logger;
    private readonly Task _pump;

    /// <summary>Creates the hub and starts its pump.</summary>
    /// <param name="scopes">Scope factory, used to resolve the conversation store and the authorizer per event.</param>
    /// <param name="logger">Logger.</param>
    public PendingQuestionHub(IServiceScopeFactory scopes, ILogger<PendingQuestionHub> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _logger = logger;
        _pump = RunPumpAsync(_shutdown.Token);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Queue and return. Everything this event implies — the store read for the conversation's title,
    /// the authorization pass, the fan-out — happens on the pump, because this runs inline on the
    /// agent loop's own thread as the run parks.
    /// </remarks>
    public void OnQuestionRaised(PendingQuestionNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        _ = _events.Writer.TryWrite(new QuestionEvent(notice, null, null));
    }

    /// <inheritdoc />
    public void OnQuestionSettled(string rootThreadId, string toolCallId) =>
        _ = _events.Writer.TryWrite(new QuestionEvent(null, rootThreadId, toolCallId));

    /// <summary>
    /// Registers an accepted <c>/ws/events</c> socket and sends it the snapshot of everything already
    /// pending that <paramref name="principal"/> may read.
    /// </summary>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="principal">The principal that opened it, or null when enforcement is off.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The subscriber, to be released with <see cref="Unsubscribe"/> on teardown.</returns>
    public async Task<QuestionEventSubscriber> SubscribeAsync(
        System.Net.WebSockets.WebSocket socket,
        Principal? principal,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(socket);

        var connection = _sockets.Register("events", socket);
        var subscriber = new QuestionEventSubscriber(connection, principal);

        await _applyGate.WaitAsync(ct);
        try
        {
            _subscribers[subscriber.ConnectionId] = subscriber;
            var visible = new List<PendingQuestion>();
            foreach (var question in _pending.Values)
            {
                if (await MayReadAsync(principal, question.RootThreadId, ct))
                {
                    visible.Add(question);
                }
            }

            _ = await subscriber.TrySendTextAsync(SnapshotFrame(visible), ct);
        }
        finally
        {
            _ = _applyGate.Release();
        }

        return subscriber;
    }

    /// <summary>Releases a subscriber (idempotent).</summary>
    /// <param name="subscriber">The subscriber returned by <see cref="SubscribeAsync"/>.</param>
    public void Unsubscribe(QuestionEventSubscriber subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);

        _ = _subscribers.TryRemove(subscriber.ConnectionId, out _);
        _sockets.Unregister(subscriber.ConnectionId);
    }

    /// <summary>
    /// The questions currently parked, in no particular order. Exposed for tests and diagnostics; the
    /// wire contract is the frames, not this.
    /// </summary>
    public IReadOnlyList<PendingQuestion> PendingSnapshot()
    {
        lock (_pending)
        {
            return [.. _pending.Values];
        }
    }

    /// <summary>
    /// Completes when every event queued before this call has been applied and broadcast. Exists so a
    /// test can assert on delivery without racing the pump; production code never needs it.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task DrainAsync(CancellationToken ct = default)
    {
        // A barrier queued BEHIND the events already there, completed by the pump once it reaches it.
        // Polling the queue instead would say "empty" while the last event was still mid-broadcast.
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_events.Writer.TryWrite(new QuestionEvent(null, null, null, barrier)))
        {
            barrier.TrySetResult();
        }

        return barrier.Task.WaitAsync(ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        await _shutdown.CancelAsync();
        try
        {
            await _pump;
        }
        catch (OperationCanceledException) { }

        while (_events.Reader.TryRead(out var pendingItem))
        {
            _ = pendingItem.Barrier?.TrySetResult();
        }

        _shutdown.Dispose();
        _applyGate.Dispose();
    }

    private async Task RunPumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _events.Reader.ReadAllAsync(ct))
            {
                await _applyGate.WaitAsync(ct);
                try
                {
                    await ApplyAsync(item, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A failed store read or a wedged socket must not end the pump — the next event
                    // still has to be delivered.
                    _logger.LogWarning(ex, "Pending-question event could not be applied.");
                }
                finally
                {
                    _ = _applyGate.Release();
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ApplyAsync(QuestionEvent item, CancellationToken ct)
    {
        if (item.Barrier is { } barrier)
        {
            _ = barrier.TrySetResult();
            return;
        }

        if (item.Notice is { } notice)
        {
            var question = new PendingQuestion
            {
                RootThreadId = notice.RootThreadId,
                AgentId = notice.AgentId,
                ChildThreadId = notice.AgentId is null ? null : notice.ThreadId,
                ToolCallId = notice.ToolCallId,
                Prompt = FirstQuestionText(notice.FunctionArgs),
                ConversationTitle = await TitleOfAsync(notice.RootThreadId, ct),
                AgentName = notice.AgentName,
                RaisedAtUtc = notice.RaisedAtUtc,
            };

            lock (_pending)
            {
                _pending[Key(notice.RootThreadId, notice.ToolCallId)] = question;
            }

            await BroadcastAsync(question.RootThreadId, PendingFrame(question), ct);
            return;
        }

        var key = Key(item.RootThreadId!, item.ToolCallId!);
        bool removed;
        lock (_pending)
        {
            removed = _pending.Remove(key);
        }

        // Broadcast even when nothing was held: a restart can settle a question this process never
        // saw raised, and a client that DID see it raised (before the restart) still needs the
        // removal. The frame carries no content beyond the two ids, so it leaks nothing a client
        // that cannot read the conversation could use.
        if (!removed)
        {
            _logger.LogDebug(
                "Settled pending question {ToolCallId} on {RootThreadId} that this process had not recorded.",
                item.ToolCallId,
                item.RootThreadId
            );
        }

        await BroadcastAsync(item.RootThreadId!, SettledFrame(item.RootThreadId!, item.ToolCallId!), ct);
    }

    private async Task BroadcastAsync(string rootThreadId, string json, CancellationToken ct)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            if (await MayReadAsync(subscriber.Principal, rootThreadId, ct))
            {
                _ = await subscriber.TrySendTextAsync(json, ct);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="principal"/> may READ <paramref name="rootThreadId"/>, decided by the
    /// same <see cref="ConversationAuthorizer"/> the REST routes and <c>/ws</c> use.
    /// </summary>
    /// <remarks>
    /// Read, not Write, because this channel confers nothing: it says a question is waiting and where
    /// to go to answer it. Answering still happens over <c>/ws</c>, which demands Write of its own.
    /// </remarks>
    private async Task<bool> MayReadAsync(Principal? principal, string rootThreadId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<ConversationAuthorizer>();
        if (!authorizer.IsEnforced)
        {
            return true;
        }

        if (principal is null)
        {
            return false;
        }

        var store = scope.ServiceProvider.GetRequiredService<IConversationStore>();
        var metadata = await store.LoadMetadataAsync(rootThreadId, ct);
        var result = await authorizer.AuthorizeAsync(principal, rootThreadId, metadata, AccessAction.Read, ct);
        return result.Allowed;
    }

    private async Task<string?> TitleOfAsync(string rootThreadId, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IConversationStore>();
            var metadata = await store.LoadMetadataAsync(rootThreadId, ct);
            return metadata?.Properties?.TryGetValue("title", out var title) == true ? title?.ToString() : null;
        }
        catch (Exception ex)
        {
            // A title is decoration. The client already knows the conversation id and fills the real
            // title from its own listing on the refresh this event triggers.
            _logger.LogDebug(ex, "Could not read the title of {RootThreadId} for a question event.", rootThreadId);
            return null;
        }
    }

    /// <summary>
    /// The text of the FIRST question in an <c>AskUserQuestion</c> batch, or a safe label when the
    /// arguments cannot be read. Mirrors the client's own <c>promptFor</c> so a pushed entry and a
    /// swept one read identically.
    /// </summary>
    internal static string FirstQuestionText(string functionArgs)
    {
        try
        {
            using var doc = JsonDocument.Parse(functionArgs);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return DefaultPrompt;
            }

            if (
                doc.RootElement.TryGetProperty("questions", out var questions)
                && questions.ValueKind == JsonValueKind.Array
                && questions.GetArrayLength() > 0
            )
            {
                var first = questions[0];
                if (first.TryGetProperty("prompt", out var prompt) && prompt.ValueKind == JsonValueKind.String)
                {
                    return prompt.GetString() ?? DefaultPrompt;
                }
            }

            return
                doc.RootElement.TryGetProperty("question", out var single) && single.ValueKind == JsonValueKind.String
                ? single.GetString() ?? DefaultPrompt
                : DefaultPrompt;
        }
        catch (JsonException)
        {
            return DefaultPrompt;
        }
    }

    private const string DefaultPrompt = "Question waiting for your answer";

    private static string Key(string rootThreadId, string toolCallId) => $"{rootThreadId}{toolCallId}";

    internal static string SnapshotFrame(IReadOnlyList<PendingQuestion> questions) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["$type"] = SnapshotType,
                ["questions"] = questions.Select(Payload).ToList(),
            },
            JsonOptions
        );

    internal static string PendingFrame(PendingQuestion question)
    {
        var payload = Payload(question);
        payload["$type"] = PendingType;
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    internal static string SettledFrame(string rootThreadId, string toolCallId) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["$type"] = SettledType,
                ["rootThreadId"] = rootThreadId,
                ["toolCallId"] = toolCallId,
            },
            JsonOptions
        );

    private static Dictionary<string, object?> Payload(PendingQuestion question) =>
        new(StringComparer.Ordinal)
        {
            ["rootThreadId"] = question.RootThreadId,
            ["agentId"] = question.AgentId,
            ["childThreadId"] = question.ChildThreadId,
            ["toolCallId"] = question.ToolCallId,
            ["prompt"] = question.Prompt,
            ["conversationTitle"] = question.ConversationTitle,
            ["agentName"] = question.AgentName,
            ["raisedAtUtc"] = question.RaisedAtUtc.UtcDateTime.ToString("O"),
        };

    private sealed record QuestionEvent(
        PendingQuestionNotice? Notice,
        string? RootThreadId,
        string? ToolCallId,
        TaskCompletionSource? Barrier = null
    );
}

/// <summary>One question the hub currently holds as waiting, as it goes on the wire.</summary>
public sealed record PendingQuestion
{
    /// <summary>The root conversation to navigate to in order to answer.</summary>
    public required string RootThreadId { get; init; }

    /// <summary>The sub-agent that asked (<c>agent-N</c>), or null when the root agent asked.</summary>
    public string? AgentId { get; init; }

    /// <summary>The sub-agent's transcript thread, or null when the root agent asked.</summary>
    public string? ChildThreadId { get; init; }

    /// <summary>The deferred call's id.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>The first question's text.</summary>
    public required string Prompt { get; init; }

    /// <summary>The conversation's title when one is stored.</summary>
    public string? ConversationTitle { get; init; }

    /// <summary>The asking sub-agent's display name when the spawning manager knew one.</summary>
    public string? AgentName { get; init; }

    /// <summary>When the call parked.</summary>
    public required DateTimeOffset RaisedAtUtc { get; init; }
}
