namespace LmStreaming.Sample.Tests.Sandbox;

/// <summary>
/// Covers WI #195's NON-CREATING session resolver on <see cref="SandboxSessionRegistry"/>
/// (<see cref="SandboxSessionRegistry.ResolveThreadWorkspaceSessionAsync"/>): the safety-critical paths
/// that must return WITHOUT ever calling the gateway. A stub transport that throws on any HTTP use proves
/// "zero provisioning": if a path returns <see cref="SandboxSessionResolutionOutcome.NoSession"/> or
/// <see cref="SandboxSessionResolutionOutcome.CredentialConflict"/> it never touched the network; a path
/// that DOES proceed to resolve a live session trips the stub (proving it passed the pre-gateway gates).
/// The happy Resolved path itself is covered by the controller/E2E tests against a scripted gateway.
/// </summary>
public class SandboxSessionRegistryResolverTests
{
    private const string GatewayBaseUrl = "http://localhost:65535";

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP not expected in resolver unit tests");
    }

    private static SandboxSessionRegistry CreateRegistry()
    {
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl };
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new ThrowingHandler())
        );
        return new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new ThrowingHandler()),
            new AuthOptions(),
            new AuthSharedSecret(new AuthOptions())
        );
    }

    [Fact]
    public async Task NoBinding_ReturnsNoSession_WithoutCallingGateway()
    {
        await using var registry = CreateRegistry();

        var result = await registry.ResolveThreadWorkspaceSessionAsync("thread-none", "default", requestCredential: null);

        result.Outcome.Should().Be(SandboxSessionResolutionOutcome.NoSession);
        result.Session.Should().BeNull();
    }

    [Fact]
    public async Task BindingForDifferentWorkspace_ReturnsNoSession()
    {
        await using var registry = CreateRegistry();
        registry.PublishEstablishedBinding(
            "thread-ws",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), new SandboxCredential("owner", "key"))
        );

        // The persisted conversation workspace is authoritative — a binding for another workspace id is not a match.
        var result = await registry.ResolveThreadWorkspaceSessionAsync("thread-ws", "some-other-workspace", requestCredential: null);

        result.Outcome.Should().Be(SandboxSessionResolutionOutcome.NoSession);
    }

    [Fact]
    public async Task DifferentCallerAppId_ReturnsCredentialConflict()
    {
        await using var registry = CreateRegistry();
        registry.PublishEstablishedBinding(
            "thread-cred",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), new SandboxCredential("owner", "owner-key"))
        );

        var result = await registry.ResolveThreadWorkspaceSessionAsync(
            "thread-cred",
            "default",
            new SandboxCredential("intruder", "intruder-key")
        );

        result.Outcome.Should().Be(SandboxSessionResolutionOutcome.CredentialConflict);
        result.ExistingAppId.Should().Be("owner");
        result.RequestedAppId.Should().Be("intruder");
    }

    [Fact]
    public async Task NullCallerCredential_PassesGate_AndProceedsToResolveLiveSession()
    {
        await using var registry = CreateRegistry();
        registry.PublishEstablishedBinding(
            "thread-null-cred",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), new SandboxCredential("owner", "owner-key"))
        );

        // A null/interactive caller must NOT conflict — it resolves to the process default, i.e. the
        // binding's own identity. The resolver therefore proceeds to resolve a live session, which trips the
        // throwing stub. Reaching the gateway is proof the credential gate passed (it did not short-circuit
        // to NoSession/CredentialConflict).
        var act = () => registry.ResolveThreadWorkspaceSessionAsync("thread-null-cred", "default", requestCredential: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MatchingCallerAppId_PassesGate_AndProceedsToResolveLiveSession()
    {
        await using var registry = CreateRegistry();
        registry.PublishEstablishedBinding(
            "thread-match-cred",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), new SandboxCredential("owner", "owner-key"))
        );

        // Same AppId (key need not match) passes the gate and proceeds to the gateway (stub throws).
        var act = () =>
            registry.ResolveThreadWorkspaceSessionAsync(
                "thread-match-cred",
                "default",
                new SandboxCredential("owner", "a-different-key")
            );

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ClearingOneThreadBinding_LeavesOtherThreadsIntact()
    {
        await using var registry = CreateRegistry();
        var binding = new SandboxEstablishedBinding(new WorkspaceRef("default"), new SandboxCredential("owner", "key"));
        registry.PublishEstablishedBinding("thread-a", binding);
        registry.PublishEstablishedBinding("thread-b", binding);

        registry.ClearEstablishedBinding("thread-a");

        // thread-a is gone (NoSession, no gateway call); thread-b's binding survives and proceeds to the
        // gateway (stub throws) — proving clearing one binding never affects another sharing the session.
        var a = await registry.ResolveThreadWorkspaceSessionAsync("thread-a", "default", requestCredential: null);
        a.Outcome.Should().Be(SandboxSessionResolutionOutcome.NoSession);

        var actB = () => registry.ResolveThreadWorkspaceSessionAsync("thread-b", "default", requestCredential: null);
        await actB.Should().ThrowAsync<InvalidOperationException>();
    }
}
