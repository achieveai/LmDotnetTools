using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>Real registry creation and controller decisions; HTTP transport alone is replaced at the gateway boundary.</summary>
public sealed class SandboxMcpAuthTests
{
    [Fact]
    public async Task AuthRequest_UsesTheCreatingAppsKey_AndReturnsFreshDecisionsBeforeDedup()
    {
        await using var fixture = await Fixture.CreateAsync();
        var appA = await fixture.CreateSessionAsync("app-a");
        var appB = await fixture.CreateSessionAsync("app-b");
        var unbound = await fixture.CreateSessionAsync("unbound");
        var request = Request();

        (await fixture.PostAsync(appA, "app-a", request, wrongSecret: true)).Should().BeOfType<UnauthorizedResult>();
        var first = Decision(await fixture.PostAsync(appA, "app-a", request));
        var repeated = Decision(await fixture.PostAsync(appA, "app-a", request));
        var other = Decision(await fixture.PostAsync(appB, "app-b", request));
        var denied = Decision(await fixture.PostAsync(unbound, "unbound", request));

        first.Decision.Should().Be("allow");
        first.Headers.Should().ContainSingle().Which.Should().Equal("Authorization", "Bearer app-a-canary");
        repeated.Headers.Should().BeEquivalentTo(first.Headers);
        other.Headers.Should().ContainSingle().Which.Should().Equal("Authorization", "Bearer app-b-canary");
        first.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow).And.BeBefore(DateTimeOffset.UtcNow.AddSeconds(61));
        denied.Decision.Should().Be("deny");
        denied.Headers.Should().BeNull();
        using var sent = JsonDocument.Parse(fixture.Creates[0]);
        sent.RootElement.GetProperty("mcp").GetProperty("egress").GetString().Should().Be("auto");
        sent.RootElement.GetProperty("webhook")
            .GetProperty("events")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should()
            .Contain("auth_request", "auth_post_response_failure");
        sent.RootElement.TryGetProperty("network", out _)
            .Should()
            .BeFalse("both legacy same-host credential routes must be suppressed");
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("path")]
    [InlineData("url")]
    [InlineData("case")]
    [InlineData("source")]
    public async Task AuthRequest_UnownedIdentityOrDestination_DeniesWithoutHeaders(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = await fixture.CreateSessionAsync("app-a");
        var request = Request();
        request = change switch
        {
            "host" => request with { DestinationHost = "other.example" },
            "port" => request with { DestinationPort = 8443 },
            "path" => request with { Path = "/other" },
            "url" => request with { McpServer = request.McpServer! with { Url = "https://other.example/mcp" } },
            "case" => request with { McpServer = request.McpServer! with { Name = "Stub" } },
            "source" => request with { ProviderId = "predefined-b" },
            _ => request,
        };

        var result = Decision(await fixture.PostAsync(session, change == "owner" ? "app-b" : "app-a", request));

        result.Decision.Should().Be("deny");
        result.Headers.Should().BeNull();
    }

    [Fact]
    public async Task AuthRequest_InnerOwnerSpoof_UsesAuthenticatedOuterEnvelope()
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = await fixture.CreateSessionAsync("app-a");

        var decision = Decision(
            await fixture.PostAsync(session, "app-a", Request() with { AppId = "app-b", SessionId = "foreign" })
        );

        decision.Headers.Should().ContainSingle().Which.Should().Equal("Authorization", "Bearer app-a-canary");
    }

    [Fact]
    public async Task AuthRequest_MissingCredential_ReturnsPending_AndSameSessionRecoversAfterKeyEdit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = await fixture.CreateSessionAsync("app-a");
        await fixture.Keys.UpsertAsync(
            new PredefinedKeyEntry
            {
                Id = "a",
                Host = "mcp.example",
                Kind = PredefinedKeyKind.CustomHeaders,
                Headers = [],
            }
        );

        var pending = Decision(await fixture.PostAsync(session, "app-a", Request()));
        var json = JsonSerializer.Serialize(pending);
        pending.Decision.Should().Be("pending");
        pending.Headers.Should().BeNull();
        pending.RetryAfterSeconds.Should().Be(5);
        json.Should().Contain("\"retry_after_seconds\":5").And.Contain("\"message\"").And.NotContain("\"headers\"");

        await fixture.Keys.UpsertAsync(
            new PredefinedKeyEntry
            {
                Id = "a",
                Host = "mcp.example",
                Kind = PredefinedKeyKind.CustomHeaders,
                Headers = [new("Authorization", "Bearer replacement-canary")],
            }
        );
        var recovered = Decision(await fixture.PostAsync(session, "app-a", Request()));

        recovered.Decision.Should().Be("allow");
        recovered.Headers.Should().ContainSingle().Which.Should().Equal("Authorization", "Bearer replacement-canary");
    }

    [Fact]
    public async Task AuthRequest_NarrowerPolicyHeaders_DoesNotInjectOtherHeadersPermittedByTheBinding()
    {
        await using var fixture = await Fixture.CreateAsync(["Authorization", "Cookie"]);
        await fixture.Keys.UpsertAsync(
            new PredefinedKeyEntry
            {
                Id = "a",
                Host = "mcp.example",
                Kind = PredefinedKeyKind.CustomHeaders,
                Headers = [new("Authorization", "Bearer app-a-canary"), new("Cookie", "unexpected-cookie-canary")],
            }
        );
        var session = await fixture.CreateSessionAsync("app-a");

        var decision = Decision(await fixture.PostAsync(session, "app-a", Request()));

        decision.Decision.Should().Be("allow");
        decision.Headers.Should().ContainSingle().Which.Should().Equal("Authorization", "Bearer app-a-canary");
    }

    [Fact]
    public async Task AuthRequest_SessionReleasedDuringAcquisition_DoesNotPublishTheAcquiredToken()
    {
        var provider = new GatedProvider();
        await using var fixture = await Fixture.CreateAsync(provider: provider);
        var session = await fixture.CreateSessionAsync("app-a");
        var response = fixture.PostAsync(session, "app-a", ManagedRequest());
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.Registry.ReleaseWorkspaceSessionsAsync("ws", "app-a");
        provider.Token.SetResult(new("never-publish-canary", DateTimeOffset.UtcNow.AddMinutes(5)));
        var decision = Decision(await response);

        decision.Decision.Should().Be("deny");
        decision.Headers.Should().BeNull();
        provider.SignOutCalls.Should().Be(0);
    }

    [Theory]
    [InlineData(false, "pending")]
    [InlineData(true, "deny")]
    public async Task AuthRequest_UnusableProviderResult_ReturnsNoHeadersOrSecretDetail(bool error, string expected)
    {
        var provider = new GatedProvider();
        if (error)
        {
            provider.Token.SetException(new IOException("provider-secret-canary"));
        }
        else
        {
            provider.Token.SetResult(new("provider-secret-canary", DateTimeOffset.UtcNow.AddSeconds(15)));
        }
        await using var fixture = await Fixture.CreateAsync(provider: provider);
        var session = await fixture.CreateSessionAsync("app-a");

        var decision = Decision(await fixture.PostAsync(session, "app-a", ManagedRequest()));

        decision.Decision.Should().Be(expected);
        decision.Headers.Should().BeNull();
        JsonSerializer.Serialize(decision).Should().NotContain("provider-secret-canary");
        provider.SignOutCalls.Should().Be(0);
    }

    [Fact]
    public async Task AuthFailure_MinimalNotification_DoesNotAcquireTokensOrSignOutSharedProviders()
    {
        var provider = new GatedProvider();
        await using var fixture = await Fixture.CreateAsync(provider: provider);
        var session = await fixture.CreateSessionAsync("app-a");

        var result = Decision(
            await fixture.PostAsync(
                session,
                "app-a",
                ManagedRequest() with
                {
                    McpServer = null,
                    Method = null,
                    Path = null,
                },
                eventName: "auth_post_response_failure"
            )
        );

        result.Decision.Should().Be("pending");
        result.Headers.Should().BeNull();
        provider.Started.Task.IsCompleted.Should().BeFalse();
        provider.SignOutCalls.Should().Be(0);
    }

    [Fact]
    public async Task AuthRequest_NoOwnershipContext_DeniesAnExistingSessionWithoutHeaders()
    {
        await using var original = await Fixture.CreateAsync();
        var session = await original.CreateSessionAsync("app-a");
        await using var fresh = await Fixture.CreateAsync();
        var service = new SandboxMcpAuthService(fresh.Registry, [], original.Keys);

        var decision = await service.EvaluateAsync(Request() with { SessionId = session, AppId = "app-a" });

        decision.Decision.Should().Be("deny");
        decision.Headers.Should().BeNull();
    }

    private static AuthWebhookResponse Decision(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<AuthWebhookResponse>().Subject;

    private static AuthWebhookRequest Request() =>
        new()
        {
            ProviderId = "mcp:stub",
            RuleId = "mcp:stub",
            DestinationHost = "mcp.example",
            DestinationPort = 443,
            Method = "POST",
            Path = "/mcp",
            McpServer = new() { Name = "stub", Url = "https://mcp.example/mcp" },
        };

    private static AuthWebhookRequest ManagedRequest() =>
        Request() with
        {
            DestinationHost = "api.github.com",
            McpServer = new() { Name = "stub", Url = "https://api.github.com/mcp" },
        };

    private sealed class GatedProvider : IOAuthTokenProvider
    {
        public string ProviderId => "github";
        public OAuthStatus Status => new(OAuthSignInState.SignedIn, null, [], null, null);
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<OAuthAccessToken> Token { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SignOutCalls { get; private set; }

        public Task HydrateFromStoreAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<SignInChallenge> BeginSignInAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SignOutAsync(CancellationToken ct = default)
        {
            SignOutCalls++;
            return Task.CompletedTask;
        }

        public Task<OAuthAccessToken> GetAccessTokenAsync(
            IReadOnlyList<string>? scopes = null,
            CancellationToken ct = default
        )
        {
            Started.SetResult(true);
            return Token.Task.WaitAsync(ct);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("lm-mcp-contract-");
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
        private SandboxSessionRegistry _registry = null!;
        private SandboxGatewayLifetime _lifetime = null!;
        private SessionSecretStore _secretStore = null!;
        private SandboxMcpAuthService _service = null!;
        public PredefinedKeyRegistry Keys { get; private set; } = null!;
        public SandboxSessionRegistry Registry => _registry;
        public List<string> Creates { get; } = [];

        public static async Task<Fixture> CreateAsync(
            string[]? bindingHeaders = null,
            IOAuthTokenProvider? provider = null
        )
        {
            var fixture = new Fixture();
            var endpoint = provider is null ? "https://mcp.example/mcp" : "https://api.github.com/mcp";
            var keys = new PredefinedKeyRegistry(
                fixture._directory.FullName,
                new FileOAuthTokenStore(fixture._directory.FullName, NullLogger<FileOAuthTokenStore>.Instance),
                new HttpClient(new Gateway(fixture)),
                NullLoggerFactory.Instance
            );
            fixture.Keys = keys;
            foreach (var app in new[] { "a", "b" })
            {
                await keys.UpsertAsync(
                    new PredefinedKeyEntry
                    {
                        Id = app,
                        Host = "mcp.example",
                        Kind = PredefinedKeyKind.CustomHeaders,
                        Headers = [new("Authorization", $"Bearer app-{app}-canary")],
                    }
                );
            }

            var options = new SandboxGatewayOptions
            {
                BaseUrl = "http://localhost:3000",
                Mcp = new()
                {
                    Egress = "auto",
                    Servers = new()
                    {
                        ["stub"] = new()
                        {
                            Auth = "injected",
                            Url = endpoint,
                            Headers = ["Authorization"],
                        },
                    },
                    CredentialBindings =
                    [
                        Binding("a", bindingHeaders, endpoint, provider),
                        Binding("b", bindingHeaders, endpoint, provider),
                    ],
                },
            };
            fixture._lifetime = new(
                options,
                NullLogger<SandboxGatewayLifetime>.Instance,
                new HttpClient(new Gateway(fixture))
            );
            fixture._secretStore = new(
                Path.Combine(fixture._directory.FullName, "secrets"),
                NullLogger<SessionSecretStore>.Instance
            );
            var auth = new AuthOptions
            {
                Webhook = new() { UseSandboxWebhook = true, PublicBaseUrl = "https://callback.example" },
            };
            fixture._registry = new(
                fixture._lifetime,
                options,
                NullLogger<SandboxSessionRegistry>.Instance,
                new HttpClient(new Gateway(fixture)),
                auth,
                fixture._secretStore,
                keys
            );
            fixture._service = new(fixture._registry, provider is null ? [] : [provider], keys);
            return fixture;
        }

        public async Task<string> CreateSessionAsync(string app) =>
            (await _registry.GetOrCreateSessionAsync(new WorkspaceRef("ws"), credential: new(app, ""))).SessionId;

        public Task<IActionResult> PostAsync(
            string session,
            string app,
            AuthWebhookRequest request,
            bool wrongSecret = false,
            string eventName = "auth_request"
        )
        {
            var controller = new SandboxWebhookController(
                _secretStore,
                _registry,
                null!,
                null!,
                NullLogger<SandboxWebhookController>.Instance,
                _service
            );
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = wrongSecret ? "wrong-secret" : _secrets[session];
            controller.ControllerContext = new() { HttpContext = context };
            return controller.ReceiveAsync(
                new SandboxWebhookEnvelope
                {
                    Event = eventName,
                    EventId = "same-event",
                    SessionId = session,
                    AppId = app,
                    Data = JsonSerializer.SerializeToElement(request),
                },
                CancellationToken.None
            );
        }

        private static SandboxMcpCredentialBindingOptions Binding(
            string app,
            string[]? headers,
            string endpoint,
            IOAuthTokenProvider? provider
        ) =>
            new()
            {
                AppId = $"app-{app}",
                Server = "stub",
                Url = endpoint,
                ProviderId = provider?.ProviderId ?? $"predefined-{app}",
                Headers = headers ?? ["Authorization"],
            };

        public async ValueTask DisposeAsync()
        {
            await _registry.DisposeAsync();
            await _lifetime.DisposeAsync();
            _directory.Delete(recursive: true);
        }

        private sealed class Gateway(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken ct
            )
            {
                if (request.Method == HttpMethod.Delete)
                {
                    return new(HttpStatusCode.NoContent);
                }

                var app = request.Headers.TryGetValues("X-Sbx-App-Id", out var values) ? values.Single() : "health";
                var session = $"session-{app}";
                if (request.Method == HttpMethod.Post)
                {
                    var body = await request.Content!.ReadAsStringAsync(ct);
                    fixture.Creates.Add(body);
                    using var sent = JsonDocument.Parse(body);
                    fixture._secrets[session] = sent
                        .RootElement.GetProperty("webhook")
                        .GetProperty("auth_header")
                        .GetString()!;
                }

                var json = JsonSerializer.Serialize(
                    new
                    {
                        session_id = session,
                        container_id = "container",
                        status = "running",
                        volumes = new { workspace = new { container_path = "/workspace", read_only = false } },
                        webhook = new
                        {
                            url = "https://callback.example/api/sandbox/webhook",
                            events = new[] { "auth_request" },
                            auth_header_set = true,
                        },
                    }
                );
                return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
        }
    }
}
