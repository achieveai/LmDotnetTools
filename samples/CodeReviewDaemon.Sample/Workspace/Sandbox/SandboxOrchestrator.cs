using System.Globalization;
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
    private readonly ILogger<SandboxOrchestrator> _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private McpClient? _client;
    private bool _disposed;

    public SandboxOrchestrator(
        string gatewayBaseUrl,
        string sessionId,
        ILogger<SandboxOrchestrator> logger
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _endpoint = new Uri($"{gatewayBaseUrl.TrimEnd('/')}/mcp");
        _sessionId = sessionId;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        var response = await client
            .CallToolAsync(
                ShellToolName,
                new Dictionary<string, object?> { ["command"] = wrapped },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var text = ExtractText(response);
        return ParseResult(text, response.IsError ?? false);
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
                        AdditionalHeaders = new Dictionary<string, string>
                        {
                            ["X-Session-ID"] = _sessionId,
                        },
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

    private static SandboxCommandResult ParseResult(string text, bool toolFlaggedError)
    {
        var markerIndex = text.LastIndexOf(ExitMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            // No sentinel: the shell never reached the trailer (e.g. the gateway itself errored).
            return new SandboxCommandResult(toolFlaggedError ? 1 : 0, text, string.Empty);
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

        return new SandboxCommandResult(exitCode, output, string.Empty);
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
