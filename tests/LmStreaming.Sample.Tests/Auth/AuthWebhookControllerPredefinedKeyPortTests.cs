using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Auth;

/// <summary>
/// The predefined-key destination gate in <see cref="AuthWebhookController"/> is keyed to the ENTRY's
/// own port, not to 443: a custom-headers key for <c>host.docker.internal:8443</c> injects on 8443
/// and is refused on any other port — the egress proxy is TLS-only on every port, so the port gate
/// exists to keep the credential inside the rule that admitted the request, not to force 443.
/// The gate and the injected headers must also come from ONE revision of the entry: an edit that
/// lands while the decision is in flight is denied, never spliced.
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

    /// <summary>
    /// Stands in for the host's auth-resolution policy and, while the webhook is parked on it, edits
    /// the key through the registry — the deterministic interleaving of "destination validated on
    /// revision A, token in hand under revision B" that a concurrent Egress Auth save produces.
    /// </summary>
    private sealed class EditingPolicy(PredefinedKeyRegistry keys, PredefinedKeyEntry replacement)
        : IAuthResolutionPolicy
    {
        public async Task<OAuthAccessToken?> ResolveAsync(
            IOAuthTokenProvider provider,
            IReadOnlyList<string>? scopes,
            CancellationToken cancellationToken
        )
        {
            await keys.UpsertAsync(replacement, cancellationToken);
            return new OAuthAccessToken(string.Empty, DateTimeOffset.MaxValue);
        }
    }

    private static PredefinedKeyEntry Entry(string host, int port, params PredefinedHeader[] headers) =>
        new()
        {
            Id = "e1",
            Host = host,
            Port = port,
            Kind = PredefinedKeyKind.CustomHeaders,
            Headers = [.. headers],
        };

    private static Task<(AuthWebhookController controller, DirectoryInfo dir)> CreateAsync(int entryPort) =>
        CreateAsync(
            Entry("host.docker.internal", entryPort, new PredefinedHeader("Authorization", "Bearer CANARY")),
            policy: null
        );

    private static async Task<(AuthWebhookController controller, DirectoryInfo dir)> CreateAsync(
        PredefinedKeyEntry entry,
        Func<PredefinedKeyRegistry, IAuthResolutionPolicy>? policy
    )
    {
        var dir = Directory.CreateTempSubdirectory("egr-port-gate");
        var keys = new PredefinedKeyRegistry(
            dir.FullName,
            new NoopStore(),
            new HttpClient(),
            NullLoggerFactory.Instance
        );
        await keys.UpsertAsync(entry);

        var sessionSecretStore = new SessionSecretStore(
            Path.Combine(dir.FullName, "secrets"),
            NullLogger<SessionSecretStore>.Instance
        );
        await sessionSecretStore.SaveAsync(SessionId, Secret);

        var controller = new AuthWebhookController(
            [],
            sessionSecretStore,
            policy?.Invoke(keys) ?? new DenyingPolicy(),
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

    /// <summary>
    /// Revision A (host.docker.internal:8443, no headers yet) admits the destination, then the
    /// webhook parks on the auth policy — where an edit publishes revision B (a different host with
    /// a real credential). Before the fix the allow response spliced B's header onto A's admission,
    /// leaking B's credential to a host B never listed. Now the decision is a deny with no headers.
    /// </summary>
    [Fact]
    public async Task Denies_when_the_key_is_edited_between_destination_check_and_header_build()
    {
        var (controller, dir) = await CreateAsync(
            Entry("host.docker.internal", 8443),
            keys => new EditingPolicy(
                keys,
                Entry("api.other.example", 443, new PredefinedHeader("Authorization", "Bearer LEAKED"))
            )
        );
        try
        {
            var decision = Decision(await controller.Evaluate("predefined-e1", Request(8443), CancellationToken.None));

            decision.Decision.Should().Be("deny");
            decision.Headers.Should().BeNull();
            decision.Reason.Should().Contain("edited").And.NotContain("LEAKED");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
