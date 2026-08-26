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
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance)
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
        var owner = new SandboxCredential("owner", "owner-key");
        registry.PublishEstablishedBinding(
            "thread-cred",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), owner, owner)
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
        // The binding was created by an S2S caller ("owner"), so its provenance (CallerCredential) is that
        // app id. A credential-less (header-less) interactive request has null provenance, which does NOT
        // match — regression guard for the authz gap where a request carrying neither S2S marker (allowed
        // through by [InboundS2SAuth] as the same-origin SPA) could otherwise reach another app's workspace.
        var owner = new SandboxCredential("owner", "owner-key");
        registry.PublishEstablishedBinding(
            "thread-null-cred",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), owner, owner)
        );

        var result = await registry.ResolveThreadWorkspaceSessionAsync("thread-null-cred", "default", requestCredential: null);

        result.Outcome.Should().Be(SandboxSessionResolutionOutcome.CredentialConflict);
        result.Session.Should().BeNull();
        result.ExistingAppId.Should().Be("owner");
        result.RequestedAppId.Should().BeNull();
    }

    [Fact]
    public async Task NullCallerCredential_AgainstInteractiveOwnedBinding_PassesGate_AndProceedsToResolveLiveSession()
    {
        await using var registry = CreateRegistry();
        // The interactive path: a binding created by the interactive UI has NULL provenance (its effective
        // credential is the process default). A null/interactive caller matches that null provenance, so it
        // passes the gate and proceeds to resolve a live session (tripping the throwing stub). Proves the
        // security fix does NOT break the ordinary same-origin file browser.
        registry.PublishEstablishedBinding(
            "thread-default-owned",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), registry.DefaultCredential, CallerCredential: null)
        );

        var act = () => registry.ResolveThreadWorkspaceSessionAsync("thread-default-owned", "default", requestCredential: null);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ExplicitCallerUsingDefaultAppId_AgainstInteractiveOwnedBinding_ReturnsCredentialConflict()
    {
        await using var registry = CreateRegistry();
        // Provenance, not just app id: an interactive-owned binding (null provenance) must NOT be accessible
        // by an EXPLICIT caller that happens to present the configured default app id — the two are distinct
        // origins even though the effective app ids match.
        registry.PublishEstablishedBinding(
            "thread-interactive",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), registry.DefaultCredential, CallerCredential: null)
        );

        var result = await registry.ResolveThreadWorkspaceSessionAsync(
            "thread-interactive",
            "default",
            new SandboxCredential(registry.DefaultCredential.AppId, "explicit-key")
        );

        result.Outcome.Should().Be(SandboxSessionResolutionOutcome.CredentialConflict);
        result.ExistingAppId.Should().BeNull();
        result.RequestedAppId.Should().Be(registry.DefaultCredential.AppId);
    }

    [Fact]
    public async Task NullCaller_AgainstExplicitDefaultAppIdOwnedBinding_ReturnsCredentialConflict()
    {
        await using var registry = CreateRegistry();
        // The mirror case: a binding created by an EXPLICIT caller using the default app id has NON-null
        // provenance, so a null/interactive request must conflict — a headerless caller cannot inherit an
        // explicitly-authenticated session just because the app ids coincide.
        var explicitOwner = new SandboxCredential(registry.DefaultCredential.AppId, "owner-key");
        registry.PublishEstablishedBinding(
            "thread-explicit-default",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), explicitOwner, explicitOwner)
        );

        var result = await registry.ResolveThreadWorkspaceSessionAsync("thread-explicit-default", "default", requestCredential: null);

        result.Outcome.Should().Be(SandboxSessionResolutionOutcome.CredentialConflict);
        result.ExistingAppId.Should().Be(registry.DefaultCredential.AppId);
        result.RequestedAppId.Should().BeNull();
    }

    [Fact]
    public async Task MatchingCallerAppId_PassesGate_AndProceedsToResolveLiveSession()
    {
        await using var registry = CreateRegistry();
        var owner = new SandboxCredential("owner", "owner-key");
        registry.PublishEstablishedBinding(
            "thread-match-cred",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), owner, owner)
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

    // ------------------------------------------------ #253: the background resolver, on the REAL registry

    /// <summary>
    /// The distinction #253 turns on, pinned where it is actually made rather than in a double. The
    /// background seam skips the provenance comparison because a drain loop has no caller to compare — so
    /// an S2S-OWNED binding, which a null-provenance request is refused against
    /// (<see cref="NullCallerCredential_AgainstNonDefaultOwner_ReturnsCredentialConflict"/>), passes the
    /// gate here and proceeds to the gateway.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two calls are made against the SAME binding on purpose. That is what makes this a test of the
    /// seam rather than of a fixture: one and the same state yields <c>CredentialConflict</c> through the
    /// request path and a passed gate through the background path, so nothing but the method under test
    /// can account for the difference.
    /// </para>
    /// <para>
    /// Every test double in the suite models this distinction by simply answering differently, which
    /// cannot detect the production argument being flipped to <c>compareProvenance: true</c> — the flip
    /// would restore silent, permanent transcript loss for every S2S-created conversation while the whole
    /// suite stayed green. This test is the one that goes red.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task BackgroundResolver_AgainstAnS2SOwnedBinding_SkipsProvenance_AndProceedsToResolveLiveSession()
    {
        await using var registry = CreateRegistry();
        var owner = new SandboxCredential("review-daemon", "owner-key");
        registry.PublishEstablishedBinding(
            "thread-background",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), owner, owner)
        );

        // The request path refuses this exact binding for want of a matching caller...
        var viaRequest = await registry.ResolveThreadWorkspaceSessionAsync(
            "thread-background",
            "default",
            requestCredential: null
        );
        viaRequest.Outcome.Should().Be(SandboxSessionResolutionOutcome.CredentialConflict);

        // ...and the background path passes the gate on the same binding, tripping the throwing stub.
        var act = () => registry.ResolveThreadWorkspaceSessionForBackgroundAsync("thread-background", "default");

        _ = await act.Should().ThrowAsync<InvalidOperationException>(
            "a drain loop has no caller, so there is no provenance to compare - and comparing anyway "
                + "denies every flush of an S2S-created conversation, forever and silently");
    }

    /// <summary>
    /// Skipping provenance is the ONLY thing the background seam skips. A thread with no binding resolves
    /// to <see cref="SandboxSessionResolutionOutcome.NoSession"/> here exactly as it does on the request
    /// path, without touching the gateway.
    /// </summary>
    [Fact]
    public async Task BackgroundResolver_WithNoBinding_StillReturnsNoSession_WithoutCallingGateway()
    {
        await using var registry = CreateRegistry();

        var result = await registry.ResolveThreadWorkspaceSessionForBackgroundAsync("thread-none", "default");

        result.Outcome.Should().Be(SandboxSessionResolutionOutcome.NoSession);
        result.Session.Should().BeNull();
    }

    /// <summary>
    /// The other gate the background seam keeps: the persisted conversation workspace stays authoritative,
    /// so a binding left over from before a workspace switch is still refused. Without this the background
    /// path would write a thread's transcript into a workspace the conversation no longer belongs to.
    /// </summary>
    [Fact]
    public async Task BackgroundResolver_WithABindingForAnotherWorkspace_StillReturnsNoSession()
    {
        await using var registry = CreateRegistry();
        var owner = new SandboxCredential("review-daemon", "owner-key");
        registry.PublishEstablishedBinding(
            "thread-background-ws",
            new SandboxEstablishedBinding(new WorkspaceRef("default"), owner, owner)
        );

        var result = await registry.ResolveThreadWorkspaceSessionForBackgroundAsync(
            "thread-background-ws",
            "some-other-workspace"
        );

        result.Outcome.Should().Be(SandboxSessionResolutionOutcome.NoSession);
        result.Session.Should().BeNull();
    }
}
