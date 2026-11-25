using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// In-memory sandbox gateway for the dedicated-sandbox tests. Records each create's session id and request body,
/// acknowledges a requested working directory as the real gateway does, and lets a test hold DELETEs (to act while
/// one is in flight) and refuse them (500, nothing deleted). A liveness probe of a deleted session answers 404, as the
/// real gateway does, and a test can hold probes or fail them. Every other call answers 200. Each DELETE's sandbox app
/// is noted, and whether it carried a key, never the key itself.
/// </summary>
/// <remarks>
/// One gateway can serve two hosts in turn, a restart, and keeps every session across them as the real one does.
/// </remarks>
internal sealed class OwnedSandboxGateway : HttpMessageHandler
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<(string SessionId, string Body)> _creates = [];
    private readonly List<string> _deletes = [];
    private readonly HashSet<string> _forgotten = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _deleted = new(0);
    private readonly SemaphoreSlim _deletesStarted = new(0);
    private TaskCompletionSource? _deleteGate;
    private int _deleteAttempts;
    private readonly Dictionary<string, Dictionary<string, string>> _env = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _probesStarted = new(0);
    private TaskCompletionSource? _probeGate;
    private readonly List<(string SessionId, string? AppId, bool HasKey)> _deleteApps = [];

    /// <summary>When set, every DELETE answers 500 and deletes nothing.</summary>
    public volatile bool FailDeletes;

    /// <summary>
    /// When set, a GET of one sandbox answers as the real gateway does: the session it created and still has, or
    /// 404. Otherwise every such GET of a session not deleted answers an empty 200, which the registry's liveness
    /// probe takes as alive without stamping it fresh, so each later acquisition probes again. Its env route answers
    /// too, as a host that did not create the session (a restarted one) reads it before patching.
    /// </summary>
    public volatile bool DescribeSessions;

    /// <summary>When not 0, every GET of one sandbox answers this status (a gateway that cannot say).</summary>
    public volatile int FailProbesWithStatus;

    /// <summary>When set, every GET of one sandbox times out on the way, before the gateway answers.</summary>
    public volatile bool TimeOutProbes;

    /// <summary>
    /// The sandbox app of every DELETE that reached the gateway, in order, and whether it carried an app key.
    /// </summary>
    public IReadOnlyList<(string SessionId, string? AppId, bool HasKey)> DeleteApps
    {
        get
        {
            lock (_gate)
            {
                return [.. _deleteApps];
            }
        }
    }

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

    /// <summary>The gateway forgets <paramref name="sessionId"/> (an idle eviction): its probes answer 404.</summary>
    public void Forget(string sessionId)
    {
        lock (_gate)
        {
            _ = _forgotten.Add(sessionId);
        }
    }

    /// <summary>Holds every liveness probe (GET of one sandbox) until the returned gate is completed.</summary>
    public TaskCompletionSource HoldProbes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only probes from now on count: earlier ones (an agent build's own) must not satisfy the next wait.
        while (_probesStarted.Wait(0)) { }

        Volatile.Write(ref _probeGate, gate);
        return gate;
    }

    /// <summary>Waits until one more liveness probe has reached the gateway (held or not).</summary>
    public async Task WaitForProbeStartedAsync() =>
        (await _probesStarted.WaitAsync(Patience))
            .Should()
            .BeTrue("a liveness probe was expected to reach the gateway");

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
            lock (_gate)
            {
                _deleteApps.Add(
                    (
                        path[(path.LastIndexOf('/') + 1)..],
                        request.Headers.TryGetValues(SandboxCredential.AppIdHeader, out var apps)
                            ? apps.Single()
                            : null,
                        request.Headers.TryGetValues(SandboxCredential.AppKeyHeader, out var keys)
                            && !string.IsNullOrEmpty(keys.Single())
                    )
                );
            }

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

        var sandboxes = path.LastIndexOf("/sandboxes/", StringComparison.Ordinal);
        if (request.Method == HttpMethod.Get && sandboxes >= 0 && path.IndexOf('/', sandboxes + 11) < 0)
        {
            _ = _probesStarted.Release();
            if (Volatile.Read(ref _probeGate) is { } probeGate)
            {
                await probeGate.Task.WaitAsync(cancellationToken);
            }

            if (TimeOutProbes)
            {
                // What HttpClient throws when its own timeout fires, not the caller's token.
                throw new TaskCanceledException("The probe timed out.", new TimeoutException());
            }

            if (FailProbesWithStatus != 0)
            {
                return new HttpResponseMessage((HttpStatusCode)FailProbesWithStatus);
            }

            var id = path[(sandboxes + 11)..];
            bool deleted;
            string? createBody;
            lock (_gate)
            {
                deleted = _deletes.Contains(id) || _forgotten.Contains(id);
                createBody = _creates.FirstOrDefault(create => create.SessionId == id).Body;
            }

            if (DescribeSessions)
            {
                deleted |= createBody is null;
                if (!deleted)
                {
                    return Created(id, createBody!);
                }
            }

            // The body is what tells "this session is gone" from "no such route" to the SDK.
            return deleted
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        $$"""{ "code": 404, "error": "Session not found: {{id}}" }""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                }
                : new HttpResponseMessage(HttpStatusCode.OK);
        }

        if (DescribeSessions && path.EndsWith("/env", StringComparison.Ordinal) && sandboxes >= 0)
        {
            return await EnvAsync(request, path[(sandboxes + 11)..^4], cancellationToken);
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

        return Created(sessionId, body);
    }

    /// <summary>Reads or patches a session's env: a null value unsets its key.</summary>
    private async Task<HttpResponseMessage> EnvAsync(
        HttpRequestMessage request,
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        var changes =
            request.Method == HttpMethod.Patch
                ? JsonSerializer.Deserialize<Dictionary<string, string?>>(
                    await request.Content!.ReadAsStringAsync(cancellationToken)
                )!
                : [];
        string body;
        lock (_gate)
        {
            if (!_env.TryGetValue(sessionId, out var env))
            {
                _env[sessionId] = env = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            foreach (var (key, value) in changes)
            {
                if (value is null)
                {
                    _ = env.Remove(key);
                }
                else
                {
                    env[key] = value;
                }
            }

            body = JsonSerializer.Serialize(new { env });
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>The gateway's description of a session it created from <paramref name="createBody"/>.</summary>
    private static HttpResponseMessage Created(string sessionId, string createBody)
    {
        var workingDir = WorkingDir(createBody) is { } dir ? $", \"working_dir\": \"{dir}\"" : string.Empty;
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
