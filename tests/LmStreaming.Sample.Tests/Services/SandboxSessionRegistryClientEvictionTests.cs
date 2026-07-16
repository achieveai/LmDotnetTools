using System.Net;
using System.Text;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the per-credential client ref-counting (task 37): the registry caches one
/// <c>SandboxClient</c>+transport per distinct (AppId, AppKey), ref-counted by live sessions, and
/// evicts+disposes it when the last session under that credential is destroyed — so credential rotation
/// / high-cardinality credentials cannot grow the client cache monotonically. A later create under an
/// evicted credential re-adds a fresh entry.
/// </summary>
public sealed class SandboxSessionRegistryClientEvictionTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    [Fact]
    public async Task PerCredentialClient_IsEvictedWhenItsLastSessionIsDestroyed_AndReAddedOnRecreate()
    {
        var (registry, _) = CreateRegistry();
        await using var _reg = registry;

        var credA = new SandboxCredential("app-a", string.Empty);
        var credB = new SandboxCredential("app-b", string.Empty);

        // Two sessions under two distinct credentials → two per-credential client entries.
        _ = await registry.GetOrCreateSessionAsync("wsA", CancellationToken.None, credA);
        _ = await registry.GetOrCreateSessionAsync("wsB", CancellationToken.None, credB);
        registry.PerCredentialClientCount.Should().Be(2);

        // Destroying every session for workspace wsA releases credA's last session → its client is evicted.
        await registry.DestroyWorkspaceSessionAsync("wsA");
        registry.PerCredentialClientCount.Should().Be(1);

        // A later create under the evicted credential re-adds a fresh entry.
        _ = await registry.GetOrCreateSessionAsync("wsA", CancellationToken.None, credA);
        registry.PerCredentialClientCount.Should().Be(2);
    }

    [Fact]
    public async Task PerCredentialClient_SharedByTwoSessions_SurvivesTheFirstDestroy_AndIsEvictedOnTheSecond()
    {
        var (registry, _) = CreateRegistry();
        await using var _reg = registry;

        // TWO workspaces under the SAME credential share ONE ref-counted client entry (refcount 2).
        var cred = new SandboxCredential("app-shared", string.Empty);
        _ = await registry.GetOrCreateSessionAsync("wsA", CancellationToken.None, cred);
        var sessionB = await registry.GetOrCreateSessionAsync("wsB", CancellationToken.None, cred);
        registry.PerCredentialClientCount.Should().Be(1);

        // Destroy ONE session (refcount 2 → 1): the shared client entry must REMAIN.
        await registry.DestroyWorkspaceSessionAsync("wsA");
        registry.PerCredentialClientCount.Should().Be(1);

        // The still-live second session can make a gateway call — proving the shared client was NOT disposed.
        var stillUsable = () => registry.ListDiscoveredAsync(sessionB.SessionId);
        await stillUsable.Should().NotThrowAsync();

        // Destroy the SECOND (last) session under the credential (refcount 1 → 0): entry evicted+disposed.
        await registry.DestroyWorkspaceSessionAsync("wsB");
        registry.PerCredentialClientCount.Should().Be(0);
    }

    [Fact]
    public async Task Create_UnderCredentialWhoseOtherSessionIsDestroyedMidCreate_DoesNotDisposeTheClient()
    {
        var createCounter = 0;
        var secondCreateInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceedWithSecondCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Deterministic seam: hold the SECOND create (session B) open — its client is already reserved and
        // its create POST is in flight — while the test destroys the credential's OTHER session (A). With
        // the create-side reservation, that destroy must NOT decrement to zero / dispose the shared client
        // out from under B; without it, B's in-flight create would use a disposed transport.
        async Task<HttpResponseMessage> Respond(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref createCounter);
                if (n == 2)
                {
                    secondCreateInFlight.TrySetResult();
                    await proceedWithSecondCreate.Task.ConfigureAwait(false);
                }

                var body =
                    "{\"session_id\":\"sess-" + n + "\",\"container_id\":\"c-" + n + "\",\"volumes\":{\"workspace\":"
                    + "{\"container_path\":\"/workspace\",\"read_only\":false,\"id\":7}}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }

            // DELETE (destroy) and any GET (liveness) succeed.
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl };
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        );
        var auth = new AuthOptions();
        await using var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new GatedHandler(Respond)),
            auth,
            new AuthSharedSecret(auth)
        );

        // Two workspaces under the SAME credential → they share one per-credential client entry.
        var credA = new SandboxCredential("app-a", string.Empty);
        _ = await registry.GetOrCreateSessionAsync("wsA", CancellationToken.None, credA);

        var sessionBTask = registry.GetOrCreateSessionAsync("wsB", CancellationToken.None, credA);
        await secondCreateInFlight.Task; // B's client is reserved and its create is in flight

        // Destroy A while B's create is in flight — the reservation must keep the shared client alive.
        await registry.DestroyWorkspaceSessionAsync("wsA");
        proceedWithSecondCreate.SetResult();

        // B must complete cleanly on a live client — never ObjectDisposedException.
        var sessionB = await sessionBTask;

        sessionB.Should().NotBeNull();
        registry.PerCredentialClientCount.Should().Be(1); // credA's client survived (now holding only session B)
    }

    private sealed class GatedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private static (SandboxSessionRegistry Registry, StubHandler Handler) CreateRegistry()
    {
        var createCounter = 0;

        HttpResponseMessage Respond(HttpRequestMessage req)
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Post && path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref createCounter);
                var body =
                    "{\"session_id\":\"sess-" + n + "\",\"container_id\":\"c-" + n + "\",\"volumes\":{\"workspace\":"
                    + "{\"container_path\":\"/workspace\",\"read_only\":false,\"id\":7}}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Get && path.EndsWith("/discovered", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"discovered\":[]}", Encoding.UTF8, "application/json"),
                };
            }

            // DELETE (destroy) and any other GET (liveness) succeed.
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        var handler = new StubHandler(Respond);
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl };
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        );

        var auth = new AuthOptions();
        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(handler),
            auth,
            new AuthSharedSecret(auth)
        );

        return (registry, handler);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
