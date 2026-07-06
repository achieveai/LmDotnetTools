using System.Globalization;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Drives the sandbox gateway as a DIRECT MCP client (plan D12). It connects an <see cref="McpClient"/>
/// over <see cref="HttpClientTransport"/> to <c>{gatewayBaseUrl}/mcp</c>, binding every call to one
/// gateway session via the <c>X-Session-ID</c> header, and runs each <see cref="SandboxCommand"/> by
/// invoking the gateway's <c>Bash</c> tool.
/// </summary>
/// <remarks>
/// <para>
/// This is the thin, integration-only glue between the daemon's deterministic git orchestration and
/// the live gateway; all decision logic lives in unit-tested collaborators
/// (<see cref="OperationPolicy"/>, <c>GitRunner</c>, <c>SubmoduleInitializer</c>) that depend only on
/// <see cref="ISandboxCommandRunner"/>, so they are verifiable against a fake without a running
/// gateway.
/// </para>
/// <para>
/// The gateway's <c>Bash</c> tool reports results as free text, so a deterministic exit code cannot be
/// read from its content reliably. The command is therefore wrapped to append a sentinel
/// (<c>__CRD_EXIT__:N:</c>) carrying <c>$?</c>; the orchestrator parses and strips it, yielding a
/// real <see cref="SandboxCommandResult.ExitCode"/> regardless of how the gateway flags errors.
/// </para>
/// </remarks>
internal sealed class SandboxOrchestrator : ISandboxCommandRunner, IAsyncDisposable
{
    private const string ExitMarker = "__CRD_EXIT__:";
    private const string ShellToolName = "Bash";

    private readonly Uri _endpoint;
    private readonly string _sessionId;
    private readonly SandboxCredential _credential;
    private readonly SandboxLimits _limits;
    private readonly ILogger<SandboxOrchestrator> _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private McpClient? _client;
    private bool _disposed;

    public SandboxOrchestrator(
        string gatewayBaseUrl,
        string sessionId,
        ILogger<SandboxOrchestrator> logger,
        SandboxCredential credential,
        SandboxLimits? limits = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _endpoint = new Uri($"{gatewayBaseUrl.TrimEnd('/')}/mcp");
        _sessionId = sessionId;
        _credential = credential;
        _limits = limits ?? new SandboxLimits();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Builds the transport header set every daemon MCP client stamps on its gateway connection: the
    /// session binding (<c>X-Session-ID</c>) plus the per-app credential the gateway requires on every
    /// app-facing call (ADR 0029). <c>X-Sbx-App-Key</c> is omitted entirely when the credential carries no
    /// key — the keyless <c>AUTH_ENFORCE=off</c> dev path — rather than sent as an empty header value.
    /// Shared by <see cref="EnsureConnectedAsync"/> and <c>LiveReviewAgentLoopFactory</c>'s own <c>/mcp</c>
    /// transport, the daemon's other direct (registry-bypassing) MCP client.
    /// </summary>
    internal static Dictionary<string, string> BuildTransportHeaders(string sessionId, SandboxCredential credential)
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Session-ID"] = sessionId,
        };

        credential.StampHeaders(headers);

        return headers;
    }

    public async Task<SandboxCommandResult> RunAsync(
        SandboxCommand command,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var inner = PosixShell.BuildCommandLine(command);
        // Group the command so the exit code is the command's own, then stamp $? via the sentinel.
        var wrapped = $"{{ {inner} ; }} ; printf '\\n{ExitMarker}%d:' \"$?\"";

        // Bound every command with a per-command timeout (PR #121 H4): a command that runs longer than
        // the configured limit is cancelled, so untrusted PR code cannot hang the poller indefinitely.
        // The linked source cancels on either the caller's token or the timeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_limits.CommandTimeout);

        CallToolResult response;
        try
        {
            response = await client
                .CallToolAsync(
                    ShellToolName,
                    new Dictionary<string, object?> { ["command"] = wrapped },
                    cancellationToken: timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Sandbox command exceeded the {Timeout} per-command timeout and was cancelled.",
                _limits.CommandTimeout);
            throw new TimeoutException(
                $"Sandbox command exceeded the configured {_limits.CommandTimeout} timeout.");
        }

        var text = ExtractText(response);
        return ParseResult(text, response.IsError ?? false, _limits);
    }

    private async Task<McpClient> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                var transport = new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Name = "sandbox",
                        Endpoint = _endpoint,
                        AdditionalHeaders = BuildTransportHeaders(_sessionId, _credential),
                    });

                _client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogInformation(
                    "Connected sandbox MCP client to {Endpoint} for session {SessionId}",
                    _endpoint,
                    _sessionId);
            }
        }
        finally
        {
            _ = _connectGate.Release();
        }

        return _client;
    }

    private static string ExtractText(CallToolResult response) =>
        response.Content is null
            ? string.Empty
            : string.Join(
                '\n',
                response
                    .Content.OfType<TextContentBlock>()
                    .Select(block => block.Text));

    private static SandboxCommandResult ParseResult(string text, bool toolFlaggedError, SandboxLimits limits)
    {
        var markerIndex = text.LastIndexOf(ExitMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            // No sentinel: the shell never reached the trailer (e.g. the gateway itself errored). Cap the
            // captured text so an unbounded gateway error response cannot flood logs/persistence (H4).
            return new SandboxCommandResult(toolFlaggedError ? 1 : 0, limits.CapOutput(text), string.Empty);
        }

        var output = text[..markerIndex].TrimEnd('\n', '\r');
        var rest = text[(markerIndex + ExitMarker.Length)..];
        var end = rest.IndexOf(':', StringComparison.Ordinal);
        var exitCode = 1;
        if (end > 0
            && int.TryParse(rest[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            exitCode = parsed;
        }

        // Cap stdout before it is materialized into the result, so a command that emits megabytes of
        // output cannot be persisted to SQLite or fed wholesale to the agent (PR #121 H4).
        return new SandboxCommandResult(exitCode, limits.CapOutput(output), string.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _connectGate.Dispose();
    }
}
