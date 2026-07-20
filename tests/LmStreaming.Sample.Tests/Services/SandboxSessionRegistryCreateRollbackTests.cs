using System.Net;
using System.Text;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Regression coverage for the create-then-persist rollback contract: the gateway create can succeed
/// (the remote session now exists) yet the very next step — persisting the session's webhook secret —
/// can still fail or be cancelled. If the registry left the session-id → session / credential maps
/// published and the remote session alive, the faulting <see cref="System.Lazy{T}"/> single-flight
/// would retry into a SECOND gateway session while discovery/liveness saw stale state. This proves the
/// failure path rolls back exactly the entries it added and best-effort destroys the remote session.
/// </summary>
public class SandboxSessionRegistryCreateRollbackTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    [Fact]
    public async Task Create_rolls_back_maps_and_destroys_remote_session_when_secret_persist_fails()
    {
        var calls = new CallLog();
        var registryHandler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;

            if (req.Method == HttpMethod.Post && path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                var ordinal = calls.RecordPost();
                var sessionId = $"sess-{ordinal}";
                var responseBody = $$"""
                    { "session_id": "{{sessionId}}", "container_id": "c-{{ordinal}}",
                      "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Delete && path.Contains("/sandboxes/", StringComparison.Ordinal))
            {
                calls.RecordDelete(path[(path.LastIndexOf('/') + 1)..]);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = null };
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));

        // Sabotage the secret store so SaveAsync — which runs AFTER the create succeeds and the maps are
        // published — fails deterministically: replace its base directory with a FILE, so SaveAsync's
        // Directory.CreateDirectory throws. This is scheme-independent (no dependency on the file-name
        // hashing) failure injection at exactly the post-create/persist window under test.
        var secretDir = Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N"));
        var secretStore = new SessionSecretStore(secretDir, NullLogger<SessionSecretStore>.Instance);
        Directory.Delete(secretDir, recursive: true);
        await File.WriteAllTextAsync(secretDir, "not-a-directory");

        await using var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(registryHandler),
            new AuthOptions(),
            secretStore);

        var act = async () => await registry.GetOrCreateSessionAsync();

        // The persist failure propagates to the caller (never a silently half-built session).
        await act.Should().ThrowAsync<Exception>();

        calls.PostCount.Should().Be(1, "the gateway session was created exactly once");
        calls.Deletes.Should().Contain("sess-1", "the just-created remote session must be best-effort destroyed");
        registry.TryGetSessionById("sess-1", out _).Should()
            .BeFalse("the reverse session-id map entry this attempt added must be rolled back");
    }

    [Fact]
    public async Task Create_retries_into_a_fresh_session_after_a_rolled_back_attempt()
    {
        var calls = new CallLog();
        var registryHandler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                var ordinal = calls.RecordPost();
                var sessionId = $"sess-{ordinal}";
                var responseBody = $$"""
                    { "session_id": "{{sessionId}}", "container_id": "c-{{ordinal}}",
                      "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Delete && path.Contains("/sandboxes/", StringComparison.Ordinal))
            {
                calls.RecordDelete(path[(path.LastIndexOf('/') + 1)..]);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = null };
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));

        var secretDir = Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N"));
        var secretStore = new SessionSecretStore(secretDir, NullLogger<SessionSecretStore>.Instance);
        // First attempt: sabotage the directory so the first SaveAsync fails; then restore it so the
        // retry's SaveAsync succeeds — proving the faulted single-flight evicted and recreated cleanly.
        Directory.Delete(secretDir, recursive: true);
        await File.WriteAllTextAsync(secretDir, "not-a-directory");

        await using var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(registryHandler),
            new AuthOptions(),
            secretStore);

        var first = async () => await registry.GetOrCreateSessionAsync();
        await first.Should().ThrowAsync<Exception>();

        // Repair the store for the retry.
        File.Delete(secretDir);
        _ = Directory.CreateDirectory(secretDir);

        var session = await registry.GetOrCreateSessionAsync();

        session.SessionId.Should().Be("sess-2", "the retry must create a fresh session, not reuse the rolled-back one");
        calls.PostCount.Should().Be(2);
        registry.TryGetSessionById("sess-2", out _).Should().BeTrue();
        registry.TryGetSessionById("sess-1", out _).Should().BeFalse();
    }

    private sealed class CallLog
    {
        private readonly object _gate = new();
        private int _postCount;
        private readonly List<string> _deletes = [];

        public int PostCount
        {
            get { lock (_gate) { return _postCount; } }
        }

        public IReadOnlyList<string> Deletes
        {
            get { lock (_gate) { return [.. _deletes]; } }
        }

        public int RecordPost()
        {
            lock (_gate) { return ++_postCount; }
        }

        public void RecordDelete(string sessionId)
        {
            lock (_gate) { _deletes.Add(sessionId); }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
