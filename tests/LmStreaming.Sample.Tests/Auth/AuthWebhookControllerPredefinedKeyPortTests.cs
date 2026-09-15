using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Auth;

/// <summary>
/// The predefined-key destination gate in <see cref="AuthWebhookController"/> is keyed to the ENTRY's
/// own port, not to 443: a custom-headers key for <c>host.docker.internal:8443</c> injects on 8443
/// and is refused on any other port — the egress proxy is TLS-only on every port, so the port gate
/// exists to keep the credential inside the rule that admitted the request, not to force 443.
/// </summary>
public sealed class AuthWebhookControllerPredefinedKeyPortTests
{
    private const string Secret = "test-shared-secret";
    private const string SessionId = "session-1";

    private sealed class NoopStore : IOAuthTokenStore
    {
        public Task<OAuthTokenRecord?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<OAuthTokenRecord?>(null);

        public Task SaveAsync(OAuthTokenRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string provider, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DenyingPolicy : IAuthResolutionPolicy
    {
        public Task<OAuthAccessToken?> ResolveAsync(
            IOAuthTokenProvider provider,
            IReadOnlyList<string>? scopes,
            CancellationToken cancellationToken
        ) => Task.FromResult<OAuthAccessToken?>(null);
    }

    private sealed class NoopForwarder : IAuthWebhookForwarder
    {
        public Task<AuthWebhookTarget?> NotifyAuthRequiredAsync(
            string sessionId,
            string providerId,
            string signinUrl,
            string reason,
            CancellationToken ct
        ) => Task.FromResult<AuthWebhookTarget?>(null);

        public Task NotifyAuthCompletedAsync(
            AuthWebhookTarget? target,
            string sessionId,
            string providerId,
            CancellationToken ct
        ) => Task.CompletedTask;

        public Task NotifyAuthDeniedAsync(
            AuthWebhookTarget? target,
            string sessionId,
            string providerId,
            string reason,
            CancellationToken ct
        ) => Task.CompletedTask;
    }

    private static async Task<(AuthWebhookController controller, DirectoryInfo dir)> CreateAsync(int entryPort)
    {
        var dir = Directory.CreateTempSubdirectory("egr-port-gate");
        var keys = new PredefinedKeyRegistry(
            dir.FullName,
            new NoopStore(),
            new HttpClient(),
            NullLoggerFactory.Instance
        );
        await keys.UpsertAsync(
            new PredefinedKeyEntry
            {
                Id = "e1",
                Host = "host.docker.internal",
                Port = entryPort,
                Kind = PredefinedKeyKind.CustomHeaders,
                Headers = [new PredefinedHeader("Authorization", "Bearer CANARY")],
            }
        );

        var sessionSecretStore = new SessionSecretStore(
            Path.Combine(dir.FullName, "secrets"),
            NullLogger<SessionSecretStore>.Instance
        );
        await sessionSecretStore.SaveAsync(SessionId, Secret);

        var controller = new AuthWebhookController(
            [],
            sessionSecretStore,
            new DenyingPolicy(),
            new NoopForwarder(),
            new AuthOptions(),
            NullLogger<AuthWebhookController>.Instance,
            predefinedKeys: keys
        );
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = Secret;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return (controller, dir);
    }

    private static AuthWebhookRequest Request(int port) =>
        new()
        {
            SessionId = SessionId,
            AppId = "lmstreaming-sample",
            ProviderId = "predefined-e1",
            RuleId = "predefined-e1",
            DestinationHost = "host.docker.internal",
            DestinationPort = port,
            Method = "GET",
            Path = "/api/items",
            RequiredScopes = [],
        };

    private static AuthWebhookResponse Decision(IActionResult result) =>
        result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<AuthWebhookResponse>().Subject;

    [Theory]
    [InlineData(8443, 8443, "allow")]
    [InlineData(8443, 443, "deny")]
    [InlineData(443, 443, "allow")]
    [InlineData(443, 8443, "deny")]
    public async Task Injects_only_on_the_entrys_own_port(int entryPort, int destinationPort, string expected)
    {
        var (controller, dir) = await CreateAsync(entryPort);
        try
        {
            var decision = Decision(
                await controller.Evaluate("predefined-e1", Request(destinationPort), CancellationToken.None)
            );

            decision.Decision.Should().Be(expected);
            if (expected == "allow")
            {
                decision.Headers.Should().ContainSingle().Which[0].Should().Be("Authorization");
            }
            else
            {
                decision.Headers.Should().BeNull();
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
