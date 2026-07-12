using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// A deterministic, stateful <see cref="HttpMessageHandler"/> that simulates the gateway's Bash tool
/// against an in-memory per-session sandbox. It classifies each submission by the LMSBX marker line
/// (via <see cref="CommandScripts.ParseRequest"/>) and drives a real filesystem-like state machine —
/// atomic claim/manifest, chunked base64 reads, reclaim (drop the large output but keep the bounded
/// completion marker), and a re-validated stale purge — so command tests assert on genuine behavior
/// (exact reassembled bytes, recovery, election, idempotent same-id reuse) rather than merely on how
/// many times a collaborator was called.
/// </summary>
internal sealed class FakeSandboxGateway : HttpMessageHandler
{
    internal enum RunMode
    {
        /// <summary>Atomic claim → run → commit manifest (or return an already-committed one). One side effect per operation.</summary>
        Normal,

        /// <summary>Commit the manifest (the side effect happens) then hang until cancellation — models a lost response after the command ran.</summary>
        HangAfterCommit,

        /// <summary>Hang until cancellation without any side effect — models a lost response before the command ran.</summary>
        HangNoSideEffect,

        /// <summary>Return an <c>isError</c> gateway-timeout result without a side effect — models the gateway execution timeout.</summary>
        ExecutionTimeout,

        /// <summary>Commit the manifest (the side effect happens) but report PENDING — models a submitter that must poll before the manifest is visible to it.</summary>
        PendingThenReady,

        /// <summary>Commit the manifest but keep every subsequent PROBE reporting PENDING until a deadline — models a command that stays PENDING for a sustained period before completing.</summary>
        PendingUntilDeadline,

        /// <summary>Claim (so PROBE reports PENDING) but never commit — models a peer/self that holds the claim indefinitely, driving the poll to its deadline.</summary>
        PendingNeverReady,

        /// <summary>Commit the manifest (the side effect happens) but return a body with NO sentinel line — models an ambiguous/malformed RUN response that must trigger manifest polling, not a resubmission.</summary>
        GarbageTextAfterCommit,
    }

    private sealed class OpState
    {
        public bool Claimed;
        public bool Committed;
        public CommandManifest? Manifest;
        public byte[] Stdout = [];
        public byte[] Stderr = [];
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, OpState> _ops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Exit, byte[] Stdout, byte[] Stderr)> _programmed = new(
        StringComparer.Ordinal
    );
    private readonly Dictionary<string, RunMode> _modes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _pendingDurations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _pendingUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _readFailuresRemaining = new(StringComparer.Ordinal);
    private readonly List<(string Name, long Lease, long Created)> _gcEntries = [];
    private readonly Dictionary<string, (long Lease, long Created)> _gcState = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rawManifestJson = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rawStatusLine = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (HttpStatusCode Status, bool CommitFirst)> _runHttpError = new(
        StringComparer.Ordinal
    );

    /// <summary>Every parsed submission, in order — lets a test assert exactly which roles were played.</summary>
    public List<CommandScriptRequest> Requests { get; } = [];

    /// <summary>Raw MCP request bodies, for asserting the credential never appears in a submitted script.</summary>
    public List<string> RequestBodies { get; } = [];

    /// <summary>Operation directories whose artifacts a RECLAIM or a re-validated GCPURGE removed (streams reclaimed, or directory purged).</summary>
    public List<string> CleanedOperations { get; } = [];

    /// <summary>Operation directories a RECLAIM processed (large output dropped, bounded completion marker retained).</summary>
    public List<string> ReclaimedOperations { get; } = [];

    /// <summary>
    /// The largest response body, in UTF-8 bytes, this gateway ever returned AFTER applying the real
    /// gateway's <c>exec</c> truncation — lets a transport test assert the manifest sentinel line (and
    /// every other wire line) genuinely stayed under the gateway limit rather than trusting it did.
    /// </summary>
    public int MaxObservedResponseBytes { get; private set; }

    public int RunSubmissionCount { get; private set; }

    /// <summary>Number of times the command actually ran (a RUN that won the claim and committed a fresh manifest).</summary>
    public int SideEffectCount { get; private set; }

    /// <summary>When set, a RECLAIM/GCPURGE is recorded but does not erase state — lets a concurrency test keep a committed manifest visible to a peer.</summary>
    public bool SuppressClean { get; set; }

    /// <summary>Programs the exit code and exact output bytes a RUN for <paramref name="operationDirectory"/> will produce.</summary>
    public void Program(string operationDirectory, int exitCode, byte[] stdout, byte[] stderr) =>
        _programmed[operationDirectory] = (exitCode, stdout, stderr);

    public void SetRunMode(string operationDirectory, RunMode mode) => _modes[operationDirectory] = mode;

    /// <summary>Programs a RUN that commits its manifest but keeps every subsequent PROBE reporting PENDING for <paramref name="duration"/> before revealing the manifest.</summary>
    public void SetSustainedPending(string operationDirectory, TimeSpan duration)
    {
        _modes[operationDirectory] = RunMode.PendingUntilDeadline;
        _pendingDurations[operationDirectory] = duration;
    }

    /// <summary>Makes the next <paramref name="count"/> READ submissions for <paramref name="operationDirectory"/> fail transiently (isError), so a test can prove idempotent read retry from the highest verified offset.</summary>
    public void FailReadsBeforeSuccess(string operationDirectory, int count) =>
        _readFailuresRemaining[operationDirectory] = count;

    /// <summary>Pre-populates a committed operation, as if a prior interrupted attempt left retained artifacts.</summary>
    public void SeedCompleted(
        string operationDirectory,
        string digest,
        int exitCode,
        byte[] stdout,
        byte[] stderr,
        long lease = long.MaxValue,
        long created = 1
    )
    {
        lock (_lock)
        {
            _ops[operationDirectory] = new OpState
            {
                Claimed = true,
                Committed = true,
                Stdout = stdout,
                Stderr = stderr,
                Manifest = BuildManifest(digest, exitCode, stdout, stderr, lease, created),
            };
        }
    }

    /// <summary>
    /// Pre-populates an ABANDONED claim: a directory that was claimed (so a PROBE reports PENDING) but
    /// whose submitter crashed before committing a manifest. Models the state a same-id retry must
    /// self-recover — the wrapper's guarded RUN re-elects one claimant and runs the (programmed) command
    /// exactly once, rather than resubmitting or waiting for the 24h sweep.
    /// </summary>
    public void SeedAbandonedClaim(string operationDirectory)
    {
        lock (_lock)
        {
            _ops[operationDirectory] = new OpState { Claimed = true, Committed = false };
        }
    }

    /// <summary>Adds an artifact directory to the GC listing with the given lease/created timestamps (also the state the re-validated purge re-checks).</summary>
    public void AddGcEntry(string name, long leaseUnixSeconds, long createdUnixSeconds)
    {
        _gcEntries.Add((name, leaseUnixSeconds, createdUnixSeconds));
        _gcState[name] = (leaseUnixSeconds, createdUnixSeconds);
    }

    /// <summary>Makes every PROBE (and the pre-probe) for <paramref name="operationDirectory"/> return a raw, hand-crafted manifest JSON — used to prove a malformed manifest maps to Protocol, never a raw exception.</summary>
    public void SeedRawManifestJson(string operationDirectory, string manifestJson) =>
        _rawManifestJson[operationDirectory] = manifestJson;

    /// <summary>Makes every PROBE (and the pre-probe) for <paramref name="operationDirectory"/> return a raw, unrecognized sentinel status line — used to prove the raw status is never echoed into an exception message.</summary>
    public void SetRawStatusLine(string operationDirectory, string statusLine) =>
        _rawStatusLine[operationDirectory] = statusLine;

    /// <summary>
    /// Makes the RUN submission for <paramref name="operationDirectory"/> fail at the transport layer with
    /// <paramref name="status"/> (a lost/errored response), optionally AFTER the side effect committed —
    /// models a 5xx returned once the gateway already ran the command, which must trigger manifest polling
    /// (never a resubmission) and preserve the operation id.
    /// </summary>
    public void SetRunHttpError(string operationDirectory, HttpStatusCode status, bool commitFirst) =>
        _runHttpError[operationDirectory] = (status, commitFirst);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        if (
            request.RequestUri is null
            || !request.RequestUri.AbsolutePath.EndsWith("/mcp", StringComparison.Ordinal)
            || request.Content is null
        )
        {
            return new HttpResponseMessage(HttpStatusCode.NotImplemented);
        }

        var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var id = root.GetProperty("id").GetInt32();
        var parameters = root.GetProperty("params");
        var command = parameters.GetProperty("arguments").GetProperty("command").GetString()!;
        var execSeconds = parameters.GetProperty("arguments").GetProperty("timeout").GetInt64();
        var script = CommandScripts.ParseRequest(command);

        lock (_lock)
        {
            RequestBodies.Add(body);
            Requests.Add(script);
        }

        if (script.Kind == CommandScriptKind.Run)
        {
            HttpStatusCode? forcedStatus = null;
            lock (_lock)
            {
                if (_runHttpError.TryGetValue(script.OperationDirectory, out var error))
                {
                    _runHttpError.Remove(script.OperationDirectory);
                    RunSubmissionCount++;
                    if (error.CommitFirst)
                    {
                        CommitIfNeeded(GetOrCreate(script.OperationDirectory), script, execSeconds);
                    }

                    forcedStatus = error.Status;
                }
            }

            if (forcedStatus is not null)
            {
                // A transport-level failure the SDK maps to Protocol — the RUN response never reaches the
                // sentinel parser, so this exercises the ambiguous-RUN recovery path.
                return new HttpResponseMessage(forcedStatus.Value)
                {
                    Content = new StringContent(string.Empty),
                };
            }
        }

        var (text, isError, hang) = Handle(script, execSeconds);
        if (hang)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        // Model the gateway's exec truncation on EVERY response, so a test can never rely on the fake
        // delivering an over-limit line whole. A manifest sentinel line that overflows would be cut
        // here, breaking its base64 and failing the SDK's parse.
        var wire = GatewayTruncation.Apply(text);
        lock (_lock)
        {
            MaxObservedResponseBytes = Math.Max(MaxObservedResponseBytes, Encoding.UTF8.GetByteCount(wire));
        }

        return McpSuccess(id, wire, isError);
    }

    private (string Text, bool IsError, bool Hang) Handle(CommandScriptRequest request, long executionSeconds)
    {
        lock (_lock)
        {
            return request.Kind switch
            {
                CommandScriptKind.Run => HandleRun(request, executionSeconds),
                CommandScriptKind.Probe => (ProbeText(request.OperationDirectory, GetOrCreate(request.OperationDirectory)), false, false),
                CommandScriptKind.Read => HandleRead(request),
                CommandScriptKind.Reclaim => HandleReclaim(request),
                CommandScriptKind.Gc => (GcListing(request.Max), false, false),
                CommandScriptKind.GcPurge => HandleGcPurge(request),
                _ => (CommandSentinel.None(), false, false),
            };
        }
    }

    private (string Text, bool IsError, bool Hang) HandleRun(CommandScriptRequest request, long executionSeconds)
    {
        RunSubmissionCount++;
        var mode = _modes.GetValueOrDefault(request.OperationDirectory, RunMode.Normal);
        var state = GetOrCreate(request.OperationDirectory);

        // An already-committed operation short-circuits to its manifest regardless of mode — the RUN
        // wrapper's fast path — so a reused operation id is answered from the retained marker, never re-run.
        if (state.Committed && mode is not (RunMode.PendingThenReady or RunMode.PendingUntilDeadline))
        {
            return (ManifestText(state), false, false);
        }

        switch (mode)
        {
            case RunMode.ExecutionTimeout:
                return ("Error: Command timed out after " + executionSeconds + " seconds", true, false);
            case RunMode.PendingThenReady:
                CommitIfNeeded(state, request, executionSeconds);
                return (CommandSentinel.Pending(), false, false);
            case RunMode.PendingUntilDeadline:
                CommitIfNeeded(state, request, executionSeconds);
                _pendingUntil[request.OperationDirectory] =
                    DateTimeOffset.UtcNow + _pendingDurations.GetValueOrDefault(request.OperationDirectory, TimeSpan.Zero);
                return (CommandSentinel.Pending(), false, false);
            case RunMode.PendingNeverReady:
                state.Claimed = true;
                return (CommandSentinel.Pending(), false, false);
            case RunMode.GarbageTextAfterCommit:
                CommitIfNeeded(state, request, executionSeconds);
                return ("gateway emitted unexpected noise with no sentinel line", false, false);
            case RunMode.HangNoSideEffect:
                return (string.Empty, false, true);
            case RunMode.HangAfterCommit:
                CommitIfNeeded(state, request, executionSeconds);
                return (string.Empty, false, true);
            case RunMode.Normal:
            default:
                CommitIfNeeded(state, request, executionSeconds);
                return (ManifestText(state), false, false);
        }
    }

    private void CommitIfNeeded(OpState state, CommandScriptRequest request, long executionSeconds)
    {
        if (state.Committed)
        {
            return;
        }

        var (exit, stdout, stderr) = _programmed.GetValueOrDefault(
            request.OperationDirectory,
            (0, Array.Empty<byte>(), Array.Empty<byte>())
        );
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var lease = created + executionSeconds + CommandArtifactLayout.LeaseGraceSeconds;
        state.Claimed = true;
        state.Stdout = stdout;
        state.Stderr = stderr;
        state.Manifest = BuildManifest(request.Digest ?? string.Empty, exit, stdout, stderr, lease, created);
        state.Committed = true;
        SideEffectCount++;
    }

    private (string Text, bool IsError, bool Hang) HandleRead(CommandScriptRequest request)
    {
        if (_readFailuresRemaining.TryGetValue(request.OperationDirectory, out var remaining) && remaining > 0)
        {
            _readFailuresRemaining[request.OperationDirectory] = remaining - 1;
            return ("transient read failure", true, false);
        }

        return (ReadChunk(request), false, false);
    }

    private (string Text, bool IsError, bool Hang) HandleReclaim(CommandScriptRequest request)
    {
        if (!SuppressClean && _ops.TryGetValue(request.OperationDirectory, out var state))
        {
            // Reclaim drops only the large stream bytes; the committed manifest (the bounded completion
            // marker) is retained, so a later same-id call is still answered without re-running.
            state.Stdout = [];
            state.Stderr = [];
        }

        ReclaimedOperations.Add(request.OperationDirectory);
        CleanedOperations.Add(request.OperationDirectory);
        return (CommandSentinel.None(), false, false);
    }

    private (string Text, bool IsError, bool Hang) HandleGcPurge(CommandScriptRequest request)
    {
        // Faithfully re-validate at delete time (never trust the earlier listing snapshot): only a
        // directory whose CURRENT lease/created is still expired AND old enough is deleted.
        if (!SuppressClean && _gcState.TryGetValue(request.OperationDirectory, out var s))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var expired = now > s.Lease;
            var pastRetentionWindow = now - s.Created > CommandArtifactLayout.StaleAgeSeconds;
            if (s.Lease > 0 && s.Created > 0 && expired && pastRetentionWindow)
            {
                _ops.Remove(request.OperationDirectory);
                CleanedOperations.Add(request.OperationDirectory);
            }
        }

        return (CommandSentinel.None(), false, false);
    }

    /// <summary>Lets a test refresh a listed directory's lease/created AFTER it was listed, to prove the re-validated purge never deletes a refreshed (re-active) operation.</summary>
    public void RefreshGcEntry(string name, long leaseUnixSeconds, long createdUnixSeconds)
    {
        lock (_lock)
        {
            _gcState[name] = (leaseUnixSeconds, createdUnixSeconds);
        }
    }

    private string ReadChunk(CommandScriptRequest request)
    {
        var state = GetOrCreate(request.OperationDirectory);
        var source = string.Equals(request.Stream, "stderr", StringComparison.Ordinal) ? state.Stderr : state.Stdout;
        var start = (int)Math.Min(request.Offset, source.Length);
        var length = (int)Math.Min(request.Length, source.Length - start);
        return Convert.ToBase64String(source, start, length);
    }

    private string GcListing(int max)
    {
        var builder = new StringBuilder();
        foreach (var (name, lease, created) in _gcEntries.Take(max))
        {
            builder.Append(CommandSentinel.GcLine(name, lease, created)).Append('\n');
        }

        return builder.ToString();
    }

    private OpState GetOrCreate(string operationDirectory)
    {
        if (!_ops.TryGetValue(operationDirectory, out var state))
        {
            state = new OpState();
            _ops[operationDirectory] = state;
        }

        return state;
    }

    private string ProbeText(string operationDirectory, OpState state)
    {
        if (_rawStatusLine.TryGetValue(operationDirectory, out var rawStatus))
        {
            return rawStatus;
        }

        if (_rawManifestJson.TryGetValue(operationDirectory, out var rawJson))
        {
            return CommandSentinel.Manifest(Convert.ToBase64String(Encoding.UTF8.GetBytes(rawJson)));
        }

        if (state.Committed)
        {
            if (
                _pendingUntil.TryGetValue(operationDirectory, out var until)
                && DateTimeOffset.UtcNow < until
            )
            {
                return CommandSentinel.Pending();
            }

            return ManifestText(state);
        }

        return state.Claimed ? CommandSentinel.Pending() : CommandSentinel.None();
    }

    private static string ManifestText(OpState state)
    {
        var manifest =
            state.Manifest ?? throw new InvalidOperationException("Manifest requested before it was committed.");
        return CommandSentinel.Manifest(
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(manifest, CommandManifest.Json))
        );
    }

    private static CommandManifest BuildManifest(
        string digest,
        int exitCode,
        byte[] stdout,
        byte[] stderr,
        long lease,
        long created
    ) =>
        new()
        {
            Version = CommandManifest.CurrentVersion,
            Digest = digest,
            ExitCode = exitCode,
            Stdout = BuildStreamManifest(stdout),
            Stderr = BuildStreamManifest(stderr),
            LeaseUnixSeconds = lease,
            CreatedUnixSeconds = created,
        };

    private static CommandStreamManifest BuildStreamManifest(byte[] bytes) =>
        new()
        {
            Length = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Inline = bytes.Length <= CommandArtifactLayout.InlineThresholdBytes ? Convert.ToBase64String(bytes) : null,
        };

    private static HttpResponseMessage McpSuccess(int id, string text, bool isError)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id,
            result = new { content = new[] { new { type = "text", text } }, isError },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
    }
}
