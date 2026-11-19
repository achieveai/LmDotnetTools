using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// In-memory sandbox gateway for the dedicated-sandbox tests. Records each create's session id and request body,
/// acknowledges a requested working directory as the real gateway does, and lets a test hold DELETEs (to act while
/// one is in flight) and refuse them (500, nothing deleted). Every other call answers 200.
/// </summary>
internal sealed class OwnedSandboxGateway : HttpMessageHandler
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<(string SessionId, string Body)> _creates = [];
    private readonly List<string> _deletes = [];
    private readonly SemaphoreSlim _deleted = new(0);
    private readonly SemaphoreSlim _deletesStarted = new(0);
    private TaskCompletionSource? _deleteGate;
    private int _deleteAttempts;

    /// <summary>When set, every DELETE answers 500 and deletes nothing.</summary>
    public volatile bool FailDeletes;

    public IReadOnlyList<(string SessionId, string Body)> Creates
    {
        get
        {
            lock (_gate)
            {
                return [.. _creates];
            }
        }
    }

    /// <summary>The sessions the gateway confirmed deleted, in order.</summary>
    public IReadOnlyList<string> Deletes
    {
        get
        {
            lock (_gate)
            {
                return [.. _deletes];
            }
        }
    }

    /// <summary>Every DELETE that reached the gateway, answered, refused or still held.</summary>
    public int DeleteAttempts => Volatile.Read(ref _deleteAttempts);

    /// <summary>Holds every DELETE until the returned gate is completed.</summary>
    public TaskCompletionSource HoldDeletes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _deleteGate, gate);
        return gate;
    }

    /// <summary>Waits until one more DELETE has reached the gateway (held or not).</summary>
    public async Task WaitForDeleteStartedAsync() =>
        (await _deletesStarted.WaitAsync(Patience)).Should().BeTrue("a delete was expected to reach the gateway");

    /// <summary>Waits for the next confirmed delete (a removed agent releases its sandbox in the background).</summary>
    public async Task<string> NextDeleteAsync()
    {
        (await _deleted.WaitAsync(Patience)).Should().BeTrue("a delete was expected");
        lock (_gate)
        {
            return _deletes[^1];
        }
    }

    /// <summary>
    /// A registry over this gateway, built as the host builds one, for tests that drive a controller directly.
    /// </summary>
    public SandboxSessionRegistry CreateRegistry(ILoggerFactory loggerFactory, string appId)
    {
        var options = new SandboxGatewayOptions
        {
            BaseUrl = "http://127.0.0.1:1",
            AppId = appId,
            Marketplaces = null,
        };
        return new SandboxSessionRegistry(
            new SandboxGatewayLifetime(
                options,
                loggerFactory.CreateLogger<SandboxGatewayLifetime>(),
                new HttpClient(this, disposeHandler: false)
            ),
            options,
            loggerFactory.CreateLogger<SandboxSessionRegistry>(),
            new HttpClient(this, disposeHandler: false),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                loggerFactory.CreateLogger<SessionSecretStore>()
            )
        );
    }

    /// <summary>The working directory a create asked for, or null.</summary>
    public static string? WorkingDir(string createBody)
    {
        using var document = JsonDocument.Parse(createBody);
        return document.RootElement.TryGetProperty("working_dir", out var dir) ? dir.GetString() : null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        if (request.Method == HttpMethod.Delete && path.Contains("/sandboxes/", StringComparison.Ordinal))
        {
            _ = Interlocked.Increment(ref _deleteAttempts);
            _ = _deletesStarted.Release();
            if (Volatile.Read(ref _deleteGate) is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            if (FailDeletes)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            lock (_gate)
            {
                _deletes.Add(path[(path.LastIndexOf('/') + 1)..]);
            }

            _ = _deleted.Release();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        if (request.Method != HttpMethod.Post || !path.EndsWith("/sandboxes", StringComparison.Ordinal))
        {
            // Health probes, liveness GETs, operations and the boot-time context-file reads.
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        string sessionId;
        lock (_gate)
        {
            sessionId = $"sess-{_creates.Count + 1}";
            _creates.Add((sessionId, body));
        }

        var workingDir = WorkingDir(body) is { } dir ? $", \"working_dir\": \"{dir}\"" : string.Empty;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                { "session_id": "{{sessionId}}", "container_id": "c-{{sessionId}}",
                  "volumes": { "workspace": { "container_path": "/workspace", "read_only": false{{workingDir}} } } }
                """,
                Encoding.UTF8,
                "application/json"
            ),
        };
    }
}
