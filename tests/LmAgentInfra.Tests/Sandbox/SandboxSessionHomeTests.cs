using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

public sealed class SandboxSessionHomeTests
{
    [Fact]
    public async Task Home_policy_and_app_partition_sessions_without_changing_workspace_mount()
    {
        await using var fixture = new Fixture();
        var home0 = Ref(".worktrees/Nova-0");
        var first = await fixture.Registry.GetOrCreateLiveSessionAsync(home0);
        (await fixture.Registry.GetOrCreateLiveSessionAsync(home0 with { HomeRelativePath = "./.worktrees//Nova-0/" }))
            .Should()
            .BeSameAs(first);
        var second = await fixture.Registry.GetOrCreateLiveSessionAsync(Ref(".worktrees/Nova-1"));
        var root = await fixture.Registry.GetOrCreateLiveSessionAsync(Ref(null));
        var blocked = await fixture.Registry.GetOrCreateLiveSessionAsync(home0 with { BlockProviderEgress = true });
        var otherApp = await fixture.Registry.GetOrCreateLiveSessionAsync(
            home0,
            credential: new SandboxCredential("other", "other-key")
        );
        new[] { first, second, root, blocked, otherApp }.Select(s => s.SessionId).Distinct().Should().HaveCount(5);
        fixture
            .Gateway.Creates.Should()
            .HaveCount(5)
            .And.OnlyContain(body => body.GetProperty("workspace").GetString() == "nova-reviews");
        first.HomeRelativePath.Should().Be(".worktrees/Nova-0");
        first.HostPath.Should().Be("/exists-only-on-remote-host/workspace");
    }

    [Fact]
    public async Task Eviction_reload_preserves_home_and_policy_and_replaces_only_that_partition()
    {
        await using var fixture = new Fixture();
        var requested = Ref(".worktrees/Nova-0") with { BlockProviderEgress = true };
        var first = await fixture.Registry.GetOrCreateLiveSessionAsync(requested);
        var sibling = await fixture.Registry.GetOrCreateLiveSessionAsync(Ref(".worktrees/Nova-1"));
        fixture.Gateway.Evicted.Add(first.SessionId);
        fixture.Clock.Now += TimeSpan.FromMinutes(1);
        var replacement = await fixture.Registry.GetOrCreateLiveSessionAsync(requested);
        replacement.SessionId.Should().NotBe(first.SessionId);
        replacement.HomeRelativePath.Should().Be(requested.HomeRelativePath);
        replacement.BlockProviderEgress.Should().BeTrue();
        replacement.Marketplaces.Should().Equal("reloaded");
        (await fixture.Registry.GetOrCreateLiveSessionAsync(Ref(".worktrees/Nova-1"))).Should().BeSameAs(sibling);
    }

    [Fact]
    public async Task Get_home_drift_fails_closed_without_creating_root_replacement()
    {
        await using var fixture = new Fixture();
        var requested = Ref(".worktrees/Nova-0");
        await fixture.Registry.GetOrCreateLiveSessionAsync(requested);
        fixture.Clock.Now += TimeSpan.FromMinutes(1);
        fixture.Gateway.OmitHomeOnGet = true;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            fixture.Registry.GetOrCreateLiveSessionAsync(requested)
        );
        fixture.Gateway.Creates.Should().ContainSingle();
        fixture.Gateway.Deletes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(".worktrees/Nova-1")]
    public async Task Create_home_drift_deletes_only_new_session_with_creating_credential(string? acknowledgedHome)
    {
        await using var fixture = new Fixture();
        var existing = await fixture.Registry.GetOrCreateLiveSessionAsync(Ref(null));
        fixture.Gateway.OmitHomeOnCreate = acknowledgedHome is null;
        fixture.Gateway.CreateHomeOverride = acknowledgedHome;
        var owner = new SandboxCredential("review-app", "synthetic-owner-key");

        var error = await Assert.ThrowsAsync<SandboxHomeAcknowledgementException>(() =>
            fixture.Registry.GetOrCreateLiveSessionAsync(Ref(".worktrees/Nova-0"), credential: owner)
        );

        error.CreatedSessionId.Should().Be("session-2");
        error.RequestedHome.Should().Be(".worktrees/Nova-0");
        error.AcknowledgedHome.Should().Be(acknowledgedHome);
        error.CleanupSucceeded.Should().BeTrue();
        fixture.Gateway.Deletes.Should().Equal(("session-2", owner.AppId, owner.AppKey));
        fixture.Gateway.Creates.Should().HaveCount(2);
        (await fixture.Registry.GetOrCreateLiveSessionAsync(Ref(null))).Should().BeSameAs(existing);
    }

    [Fact]
    public async Task Create_home_drift_cleanup_failure_retains_created_identity_and_diagnostic()
    {
        await using var fixture = new Fixture();
        fixture.Gateway.OmitHomeOnCreate = true;
        fixture.Gateway.FailDelete = true;
        var owner = new SandboxCredential("review-app", "synthetic-owner-key");

        var error = await Assert.ThrowsAsync<SandboxHomeAcknowledgementException>(() =>
            fixture.Registry.GetOrCreateLiveSessionAsync(Ref(".worktrees/Nova-0"), credential: owner)
        );

        error.CreatedSessionId.Should().Be("session-1");
        error.CleanupSucceeded.Should().BeFalse();
        error.InnerException.Should().BeOfType<SandboxException>();
        fixture.Gateway.Deletes.Should().Equal(("session-1", owner.AppId, owner.AppKey));
        fixture.Gateway.Creates.Should().ContainSingle();
    }

    [Fact]
    public async Task Established_binding_browser_resume_preserves_home_and_caller_provenance()
    {
        await using var fixture = new Fixture();
        var requested = Ref(".worktrees/Nova-0") with { BlockProviderEgress = true };
        var owner = new SandboxCredential("review-app", "owner-key");
        var original = await fixture.Registry.GetOrCreateLiveSessionAsync(requested, credential: owner);
        fixture.Registry.PublishEstablishedBinding("thread", new(requested, owner, owner, original.SessionId));
        var wrongCaller = await fixture.Registry.ResolveThreadWorkspaceSessionAsync("thread", "catalog-id", null);
        wrongCaller.Outcome.Should().Be(SandboxSessionResolutionOutcome.CredentialConflict);
        fixture.Gateway.Evicted.Add(original.SessionId);
        fixture.Clock.Now += TimeSpan.FromMinutes(1);
        var resumed = await fixture.Registry.ResolveThreadWorkspaceSessionAsync("thread", "catalog-id", owner);
        resumed.Outcome.Should().Be(SandboxSessionResolutionOutcome.Resolved);
        resumed.Session!.HomeRelativePath.Should().Be(requested.HomeRelativePath);
        resumed.Session.BlockProviderEgress.Should().BeTrue();
        resumed.Session.SessionId.Should().NotBe(original.SessionId);
    }

    [Fact]
    public async Task Plugin_candidates_keep_each_partitions_home_and_network_policy()
    {
        await using var fixture = new Fixture();
        await fixture.Registry.GetOrCreateSessionAsync(Ref(".worktrees/Nova-0"));
        await fixture.Registry.GetOrCreateSessionAsync(Ref(".worktrees/Nova-1") with { BlockProviderEgress = true });
        var partitions = fixture.Registry.SnapshotPluginSelectionPartitions("catalog-id");
        partitions.Should().HaveCount(2);
        foreach (var partition in partitions)
        {
            var candidate = await fixture.Registry.CreatePluginSelectionCandidateAsync(
                Ref(null) with
                {
                    PluginSelection = [],
                },
                partition,
                default
            );
            candidate.HomeRelativePath.Should().Be(partition.Session.HomeRelativePath);
            candidate.BlockProviderEgress.Should().Be(partition.Session.BlockProviderEgress);
        }
    }

    private static WorkspaceRef Ref(string? home) => new("catalog-id", "nova-reviews") { HomeRelativePath = home };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _secrets = Path.Combine(
            Path.GetTempPath(),
            "home-session-test-" + Guid.NewGuid().ToString("N")
        );
        public Gateway Gateway { get; } = new();
        public Clock Clock { get; } = new();
        public SandboxSessionRegistry Registry { get; }

        public Fixture()
        {
            var options = new SandboxGatewayOptions { BaseUrl = "http://gateway.invalid" };
            var lifetime = new SandboxGatewayLifetime(
                options,
                NullLogger<SandboxGatewayLifetime>.Instance,
                new HttpClient(new Gateway())
            );
            Registry = new(
                lifetime,
                options,
                NullLogger<SandboxSessionRegistry>.Instance,
                new HttpClient(Gateway),
                new AuthOptions(),
                new SessionSecretStore(_secrets, NullLogger<SessionSecretStore>.Instance),
                reloadWorkspaceRef: (_, _) =>
                    Task.FromResult<WorkspaceRef?>(new WorkspaceRef("catalog-id", "nova-reviews", ["reloaded"])),
                timeProvider: Clock
            );
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            if (Directory.Exists(_secrets))
                Directory.Delete(_secrets, true);
        }
    }

    private sealed class Gateway : HttpMessageHandler
    {
        public List<JsonElement> Creates { get; } = [];
        public HashSet<string> Evicted { get; } = [];
        public bool OmitHomeOnGet { get; set; }
        public bool OmitHomeOnCreate { get; set; }
        public string? CreateHomeOverride { get; set; }
        public bool FailDelete { get; set; }
        public List<(string SessionId, string AppId, string AppKey)> Deletes { get; } = [];
        private readonly Dictionary<string, string?> _homes = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Delete)
            {
                Deletes.Add(
                    (
                        path.Split('/')[^1],
                        request.Headers.GetValues("X-Sbx-App-Id").Single(),
                        request.Headers.GetValues("X-Sbx-App-Key").Single()
                    )
                );
                return new(FailDelete ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
            }
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Creates.Add(body.RootElement.Clone());
                var id = $"session-{Creates.Count}";
                _homes[id] = body.RootElement.TryGetProperty("home", out var home) ? home.GetString() : null;
                return Response(id, OmitHomeOnCreate ? null : CreateHomeOverride ?? _homes[id]);
            }
            if (!path.StartsWith("/api/v1/sandboxes/", StringComparison.Ordinal))
                return new(HttpStatusCode.OK);
            var session = path.Split('/')[^1];
            return Evicted.Contains(session)
                ? new(HttpStatusCode.NotFound)
                : Response(session, OmitHomeOnGet ? null : _homes[session]);
        }

        private static HttpResponseMessage Response(string id, string? home) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(
                        new
                        {
                            session_id = id,
                            volumes = new
                            {
                                workspace = new { container_path = "/exists-only-on-remote-host/workspace", home },
                            },
                        }
                    ),
                    Encoding.UTF8,
                    "application/json"
                ),
            };
    }
}
