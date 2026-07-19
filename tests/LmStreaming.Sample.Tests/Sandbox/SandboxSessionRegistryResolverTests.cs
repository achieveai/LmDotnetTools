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
    public async Task NullCallerCredential_AgainstNonDefaultOwner_ReturnsCredentialConflict()
    {
        await using var registry = CreateRegistry();
        // The binding is owned by an S2S caller ("owner"), NOT the process default. A credential-less
        // (header-less) caller resolves to the process default identity, which is NOT that owner — so it
        // must conflict, symmetric with MultiTurnAgentPool. Regression guard for the authz gap where a
        // request carrying neither S2S marker (allowed through by [InboundS2SAuth] as the same-origin SPA)
        // could otherwise reach another app identity's workspace.
        registry.PublishEstablishedBinding(
            "thread-null-cred",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), new SandboxCredential("owner", "owner-key"))
        );

        var result = await registry.ResolveThreadWorkspaceSessionAsync("thread-null-cred", "default", requestCredential: null);

        result.Outcome.Should().Be(SandboxSessionResolutionOutcome.CredentialConflict);
        result.Session.Should().BeNull();
        result.ExistingAppId.Should().Be("owner");
        result.RequestedAppId.Should().Be(registry.DefaultCredential.AppId);
    }

    [Fact]
    public async Task NullCallerCredential_AgainstDefaultOwnedBinding_PassesGate_AndProceedsToResolveLiveSession()
    {
        await using var registry = CreateRegistry();
        // The interactive path: a binding created by the interactive UI is owned by the process default
        // credential. A null/interactive caller resolves to that same default identity, so it passes the
        // gate and proceeds to resolve a live session (tripping the throwing stub). Proves the security
        // fix does NOT break the ordinary same-origin file browser.
        registry.PublishEstablishedBinding(
            "thread-default-owned",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), registry.DefaultCredential)
        );

        var act = () => registry.ResolveThreadWorkspaceSessionAsync("thread-default-owned", "default", requestCredential: null);

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
        // Default-owned bindings so a null (interactive) caller passes the credential gate — this test is
        // about binding ISOLATION on clear, not the cross-actor gate (covered separately above).
        var binding = new SandboxEstablishedBinding(new WorkspaceRef("default"), registry.DefaultCredential);
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
