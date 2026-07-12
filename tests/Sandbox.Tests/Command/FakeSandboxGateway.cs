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
/// atomic claim/manifest, chunked base64 reads, cleanup, and a bounded GC listing — so command tests
/// assert on genuine behavior (exact reassembled bytes, recovery, election) rather than merely on how
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
    private readonly List<(string Name, long Lease, long Created)> _gcEntries = [];

    /// <summary>Every parsed submission, in order — lets a test assert exactly which roles were played.</summary>
    public List<CommandScriptRequest> Requests { get; } = [];

    /// <summary>Raw MCP request bodies, for asserting the credential never appears in a submitted script.</summary>
    public List<string> RequestBodies { get; } = [];

    /// <summary>Operation directories a CLEAN deleted.</summary>
    public List<string> CleanedOperations { get; } = [];

    public int RunSubmissionCount { get; private set; }

    /// <summary>Number of times the command actually ran (a RUN that won the claim and committed a fresh manifest).</summary>
    public int SideEffectCount { get; private set; }

    /// <summary>When set, a CLEAN is recorded but does not erase state — lets a concurrency test keep a committed manifest visible to a peer.</summary>
    public bool SuppressClean { get; set; }

    /// <summary>Programs the exit code and exact output bytes a RUN for <paramref name="operationDirectory"/> will produce.</summary>
    public void Program(string operationDirectory, int exitCode, byte[] stdout, byte[] stderr) =>
        _programmed[operationDirectory] = (exitCode, stdout, stderr);

    public void SetRunMode(string operationDirectory, RunMode mode) => _modes[operationDirectory] = mode;

    /// <summary>Pre-populates a committed operation, as if a prior interrupted attempt left retained artifacts.</summary>
    public void SeedCompleted(
        string operationDirectory,
        string digest,
        int exitCode,
        byte[] stdout,
        byte[] stderr,
        long lease = long.MaxValue,
        long created = 0
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

    /// <summary>Adds an artifact directory to the GC listing with the given lease/created timestamps.</summary>
    public void AddGcEntry(string name, long leaseUnixSeconds, long createdUnixSeconds) =>
        _gcEntries.Add((name, leaseUnixSeconds, createdUnixSeconds));

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

        var (text, isError, hang) = Handle(script, execSeconds);
        if (hang)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        return McpSuccess(id, text, isError);
    }

    private (string Text, bool IsError, bool Hang) Handle(CommandScriptRequest request, long executionSeconds)
    {
        lock (_lock)
        {
            return request.Kind switch
            {
                CommandScriptKind.Run => HandleRun(request, executionSeconds),
                CommandScriptKind.Probe => (ProbeText(GetOrCreate(request.OperationDirectory)), false, false),
                CommandScriptKind.Read => (ReadChunk(request), false, false),
                CommandScriptKind.Clean => HandleClean(request),
                CommandScriptKind.Gc => (GcListing(request.Max), false, false),
                _ => (CommandSentinel.None(), false, false),
            };
        }
    }

    private (string Text, bool IsError, bool Hang) HandleRun(CommandScriptRequest request, long executionSeconds)
    {
        RunSubmissionCount++;
        var mode = _modes.GetValueOrDefault(request.OperationDirectory, RunMode.Normal);
        var state = GetOrCreate(request.OperationDirectory);

        switch (mode)
        {
            case RunMode.ExecutionTimeout:
                return ("Error: Command timed out after " + executionSeconds + " seconds", true, false);
            case RunMode.PendingThenReady:
                CommitIfNeeded(state, request, executionSeconds);
                return (CommandSentinel.Pending(), false, false);
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

    private (string Text, bool IsError, bool Hang) HandleClean(CommandScriptRequest request)
    {
        if (!SuppressClean)
        {
            _ops.Remove(request.OperationDirectory);
        }

        CleanedOperations.Add(request.OperationDirectory);
        return (CommandSentinel.None(), false, false);
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

    private static string ProbeText(OpState state)
    {
        if (state.Committed)
        {
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
