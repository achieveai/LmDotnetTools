using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The <see cref="IMultiTurnAgent"/> adapter that drives one review turn against a running
/// <b>LmStreaming.Sample</b> review host over the S2S REST API (via <see cref="LmStreamingS2SClient"/>),
/// so the review is a real LmStreaming-hosted conversation (parent loop + <c>code-reviewer:*</c> sub-agent
/// tree) rather than an in-process loop. It satisfies exactly the slice of the interface that
/// <see cref="AgentTextCollector"/> drives — one <see cref="ExecuteRunAsync"/> — and stays thin:
/// <list type="number">
/// <item>lazily <b>provision</b> a conversation once (caching the server-minted <c>thread-{Guid:N}</c>),</item>
/// <item><b>send</b> the review input as one user message (getting an <c>inputId</c> to poll by),</item>
/// <item><b>poll</b> <see cref="LmStreamingS2SClient.GetStatusByInputIdAsync"/> to a terminal status with a
///   bounded backoff and an overall timeout, and</item>
/// <item>yield the review text from <c>status.Response</c> as a <b>single finalized</b>
///   <see cref="TextMessage"/> (<see cref="Role.Assistant"/>, non-thinking) — the final assistant text is
///   already resolved server-side, so no delta reassembly is needed and one yield feeds the collector.</item>
/// </list>
/// <para>
/// <see cref="ThreadId"/> surfaces the server-minted conversation id (the deep-link target the executor
/// appends to the posted PR comment); <see cref="CurrentRunId"/> comes from the polled status. The
/// background-loop members (<see cref="SendAsync"/>/<see cref="TrySendAsync"/>, <see cref="SubscribeAsync"/>,
/// <see cref="RunAsync"/>, <see cref="StopAsync"/>, <see cref="IsRunning"/>) are implemented minimally: the
/// daemon only ever drives this agent collect-only through <see cref="ExecuteRunAsync"/>.
/// </para>
/// </summary>
internal sealed class S2SReviewAgent : IMultiTurnAgent
{
    /// <summary>Terminal run statuses that end the poll loop (matches <c>ConversationRunStatus</c> names).</summary>
    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Completed",
        "Errored",
        "Interrupted",
    };

    private readonly LmStreamingS2SClient _client;
    private readonly string _workspaceId;
    private readonly string _providerId;
    private readonly string _modeId;
    private readonly string? _title;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollMaxInterval;
    private readonly TimeSpan _overallTimeout;
    private readonly ILogger<S2SReviewAgent> _logger;

    private string? _threadId;
    private string? _currentRunId;

    public S2SReviewAgent(
        LmStreamingS2SClient client,
        string workspaceId,
        string providerId,
        string modeId,
        string? title,
        ILogger<S2SReviewAgent> logger,
        TimeSpan? pollInterval = null,
        TimeSpan? pollMaxInterval = null,
        TimeSpan? overallTimeout = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(modeId);
        _workspaceId = workspaceId;
        _providerId = providerId;
        _modeId = modeId;
        _title = title;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Live workspace-agent reviews are long (a run can reach turn 17 of 150), so the poll must be patient
        // but not busy: start tight, back off geometrically to a ceiling, and bound the whole wait.
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _pollMaxInterval = pollMaxInterval ?? TimeSpan.FromSeconds(15);
        _overallTimeout = overallTimeout ?? TimeSpan.FromMinutes(30);
    }

    /// <summary>The server-minted run id from the last polled status (null before the first run completes).</summary>
    public string? CurrentRunId => _currentRunId;

    /// <summary>
    /// The server-minted conversation id (<c>thread-{Guid:N}</c>) once provisioned. Before the first run it is
    /// an empty string (the agent has not yet talked to the review host); the collector reads it only after the
    /// run, by which point it is the real minted id the deep-link points at.
    /// </summary>
    public string ThreadId => _threadId ?? string.Empty;

    /// <summary>Always false — this adapter has no background loop; it is driven collect-only.</summary>
    public bool IsRunning => false;

    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userInput);

        var input = ExtractUserText(userInput);
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("S2SReviewAgent.ExecuteRunAsync received no user text to send.");
        }

        var threadId = await EnsureProvisionedAsync(ct).ConfigureAwait(false);
        var inputId = await _client.SendMessageAsync(threadId, input, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "S2S review message queued on thread {ThreadId} (inputId {InputId}); polling to terminal.",
            threadId,
            inputId);

        var status = await PollToTerminalAsync(threadId, inputId, ct).ConfigureAwait(false);
        _currentRunId = status.RunId;

        if (!string.Equals(status.Status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            // Errored/Interrupted still surface whatever partial text the host resolved (may be null); log the
            // non-success terminal so an operator can correlate an empty/short review with a failed hosted run.
            _logger.LogError(
                "S2S review run {RunId} on thread {ThreadId} reached terminal status {Status} (not Completed).",
                status.RunId,
                threadId,
                status.Status);
        }

        var reviewText = status.ResponseText;
        if (string.IsNullOrEmpty(reviewText))
        {
            _logger.LogWarning(
                "S2S review run {RunId} on thread {ThreadId} returned no response text (status {Status}).",
                status.RunId,
                threadId,
                status.Status);
            yield break;
        }

        // ONE finalized assistant message. AgentTextCollector prefers a finalized TextMessage over streamed
        // deltas, so this single yield is the collected review text verbatim — no delta/GenerationId mirroring.
        yield return new TextMessage
        {
            Text = reviewText,
            Role = Role.Assistant,
            IsThinking = false,
            ThreadId = threadId,
            RunId = status.RunId,
        };
    }

    /// <summary>
    /// Provisions the conversation on first use and caches the minted thread id, so a second
    /// <see cref="ExecuteRunAsync"/> (e.g. a would-be enforcement turn) reuses the same thread rather than
    /// opening a new one. Best-effort sets a human-readable title once, right after provisioning.
    /// </summary>
    private async Task<string> EnsureProvisionedAsync(CancellationToken ct)
    {
        if (_threadId is not null)
        {
            return _threadId;
        }

        var threadId = await _client
            .ProvisionAsync(_workspaceId, _providerId, _modeId, ct)
            .ConfigureAwait(false);
        _threadId = threadId;
        _logger.LogInformation(
            "Provisioned S2S review conversation {ThreadId} (workspace {WorkspaceId}, provider {ProviderId}, mode {ModeId}).",
            threadId,
            _workspaceId,
            _providerId,
            _modeId);

        if (!string.IsNullOrWhiteSpace(_title))
        {
            try
            {
                await _client.UpdateMetadataAsync(threadId, _title, preview: null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A cosmetic title is not worth failing the review over.
                _logger.LogWarning(ex, "Could not set title on S2S review conversation {ThreadId}.", threadId);
            }
        }

        return threadId;
    }

    /// <summary>
    /// Polls the run status by input id until a terminal status, with geometric backoff capped at
    /// <see cref="_pollMaxInterval"/> and an overall <see cref="_overallTimeout"/>. Throws
    /// <see cref="TimeoutException"/> if the run does not finish in time.
    /// </summary>
    private async Task<S2SStatusResult> PollToTerminalAsync(string threadId, string inputId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _overallTimeout;
        var interval = _pollInterval;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var status = await _client.GetStatusByInputIdAsync(threadId, inputId, ct).ConfigureAwait(false);
            if (TerminalStatuses.Contains(status.Status))
            {
                return status;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"S2S review run on thread {threadId} did not reach a terminal status within "
                    + $"{_overallTimeout.TotalMinutes:0} minutes (last status: {status.Status}).");
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);

            // Geometric backoff to the ceiling — patient with long hosted runs without hammering the host.
            var next = interval + interval;
            interval = next > _pollMaxInterval ? _pollMaxInterval : next;
        }
    }

    /// <summary>Concatenates the user turn's non-empty text messages (the collector sends exactly one).</summary>
    private static string ExtractUserText(UserInput userInput)
    {
        var parts = userInput.Messages
            .OfType<TextMessage>()
            .Where(static m => m.Role == Role.User && !string.IsNullOrEmpty(m.Text))
            .Select(static m => m.Text);
        return string.Join("\n", parts);
    }

    // ── Background-loop members: not used on the collect-only review drive ──────────────────────────

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "S2SReviewAgent is driven collect-only via ExecuteRunAsync; background SendAsync is not supported.");

    public ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "S2SReviewAgent is driven collect-only via ExecuteRunAsync; background TrySendAsync is not supported.");

    public async IAsyncEnumerable<IMessage> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // No background loop → no out-of-band message stream. Yield nothing (a valid empty async sequence).
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public Task RunAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task StopAsync(TimeSpan? timeout = null) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
