using System.Net;
using System.Net.Http.Headers;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Configuration;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Extensions;
using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Tests.Identity;

/// <summary>
/// Pins the service-to-service front door (#345) and the CORS ordering it shares a pipeline with
/// (#346).
/// </summary>
/// <remarks>
/// <para>
/// Both issues are about ORDER, so every test here runs a real pipeline in a
/// <see cref="TestServer"/>: CORS, then authentication's stash, then
/// <see cref="IdentityMiddleware"/>, then a terminal endpoint that echoes whatever principal it was
/// handed. A hand-rolled <c>DefaultHttpContext</c> cannot show that a component earlier in the
/// pipeline stopped a later one from ever running, which is the entire defect in both cases.
/// </para>
/// <para>
/// The endpoint filter that also guards these routes is deliberately NOT in this pipeline. Its own
/// behaviour is pinned in <c>ConversationsControllerS2SPipelineTests</c>; what is under test here is
/// the middleware layer that used to refuse the caller before that filter could run at all.
/// </para>
/// </remarks>
public sealed class ServiceCallerPrincipalTests
{
    private const string Secret = "s3cr3t-inbound-value-for-the-middleware";
    private const string DaemonAppId = "review-daemon";
    private const string DaemonTenant = "tnt_daemon";
    private const string GuardedRoute = "/api/conversations";
    private const string ClientOrigin = "https://client.example";

    /// <summary>Marks that the request reached the end of the pipeline rather than being refused.</summary>
    private const string ReachedBody = "reached";

    private static async Task<TestServer> StartAsync(
        bool enforce,
        string? s2sSecret = null,
        IReadOnlyDictionary<string, string>? apps = null,
        string? allowedOrigin = null,
        string legacyTenantId = "legacy",
        bool corsFirst = true)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (s2sSecret is not null)
        {
            settings[InboundS2SAuthAttribute.SecretConfigKey] = s2sSecret;
        }

        var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(settings))
                .ConfigureServices(services =>
                {
                    _ = services.Configure<IdentityOptions>(o =>
                    {
                        o.Enforce = enforce;
                        o.LegacyTenantId = legacyTenantId;
                        foreach (var (appId, tenantId) in apps ?? new Dictionary<string, string>())
                        {
                            o.Apps[appId] = new ServiceAppOptions { TenantId = tenantId };
                        }
                    });
                    _ = services.AddSingleton(TimeProvider.System);
                    _ = services.AddSingleton<ITenantStore, StubTenantStore>();
                    _ = services.AddSingleton<IAuditSink>(new RecordingAuditSink());
                    _ = services.AddSingleton(sp => new PrincipalFactory(
                        sp.GetRequiredService<ITenantStore>(),
                        sp.GetRequiredService<IAuditSink>(),
                        sp.GetRequiredService<IOptions<IdentityOptions>>(),
                        TimeProvider.System,
                        sp.GetRequiredService<ILogger<PrincipalFactory>>()));
                    _ = services.AddSingleton<IRequestPrincipalSource, ServiceCallerPrincipalSource>();
                    _ = services.AddLmStreaming(o =>
                    {
                        o.AllowedOrigins = allowedOrigin is null ? [] : [allowedOrigin];
                    });
                })
                .Configure(app =>
                {
                    // The order under test. `corsFirst: false` IS the #346 defect, reproduced, and
                    // the preflight and refusal-header tests below are what notice.
                    if (corsFirst)
                    {
                        _ = app.UseLmStreamingCors();
                        _ = app.UseMiddleware<IdentityMiddleware>();
                    }
                    else
                    {
                        _ = app.UseMiddleware<IdentityMiddleware>();
                        _ = app.UseLmStreamingCors();
                    }

                    app.Run(async context =>
                    {
                        var principal = context.Items[IdentityHttpItems.PrincipalKey] as Principal;
                        await context.Response.WriteAsync(
                            $"{ReachedBody}:{principal?.TenantId ?? "<none>"}"
                                + $":{principal?.Actor.Kind.ToString() ?? "<none>"}"
                                + $":{principal?.Actor.Id ?? "<none>"}"
                                + $":{principal?.Source.ToString() ?? "<none>"}");
                    });
                }))
            .StartAsync();

        return host.GetTestServer();
    }

    /// <summary>
    /// The exact header shape the Code-Review Daemon stamps on every request
    /// (<c>LmStreamingS2SClient</c>): the S2S secret plus a caller credential, and no bearer token.
    /// </summary>
    private static HttpRequestMessage DaemonRequest(
        string secret = Secret,
        string appId = DaemonAppId,
        string path = GuardedRoute)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.TryAddWithoutValidation(InboundS2SAuthAttribute.HeaderName, secret);
        request.Headers.TryAddWithoutValidation(SandboxCredential.AppIdHeader, appId);
        request.Headers.TryAddWithoutValidation(SandboxCredential.AppKeyHeader, "app-key-value");
        return request;
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    // ---- #345: a service caller gets a principal, not a 401 --------------------------------------

    [Fact]
    public async Task WithEnforcementOn_TheDaemonsExactHeaderShape_ReachesTheEndpointWithAnAppPrincipal()
    {
        // The acceptance clause of #345, stated in the daemon's own headers rather than in a
        // paraphrase of them: X-S2S-Auth plus X-Sbx-App-Id/X-Sbx-App-Key, and NO Authorization.
        using var server = await StartAsync(
            enforce: true,
            s2sSecret: Secret,
            apps: new Dictionary<string, string>(StringComparer.Ordinal) { [DaemonAppId] = DaemonTenant });

        var response = await server.CreateClient().SendAsync(DaemonRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = (await response.Content.ReadAsStringAsync()).Should().Be(
            $"{ReachedBody}:{DaemonTenant}:{nameof(PrincipalKind.App)}:{DaemonAppId}"
                + $":{nameof(PrincipalSource.AppOnly)}");
    }

    [Fact]
    public async Task WithEnforcementOn_AServiceCallerPresentingOnlyTheSecret_UsesTheDefaultRegistration()
    {
        // Not every service caller passes an app credential; some present the S2S secret alone.
        // They resolve against the reserved `default` key, which is a registration like any other -
        // not a wildcard, and absent by default.
        using var server = await StartAsync(
            enforce: true,
            s2sSecret: Secret,
            apps: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [IdentityOptions.DefaultServiceAppKey] = "tnt_infra",
            });

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(GuardedRoute, UriKind.Relative));
        request.Headers.TryAddWithoutValidation(InboundS2SAuthAttribute.HeaderName, Secret);

        var response = await server.CreateClient().SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = (await response.Content.ReadAsStringAsync()).Should().Contain(
            $"tnt_infra:{nameof(PrincipalKind.App)}:{IdentityOptions.DefaultServiceAppKey}");
    }

    [Fact]
    public async Task WithEnforcementOn_AnUnregisteredApp_IsRefusedWithForbidden_NotUnauthorized()
    {
        // 403, not 401. The caller authenticated - the secret matched - so telling it to go and get
        // another credential would send it round a loop it cannot win.
        using var server = await StartAsync(enforce: true, s2sSecret: Secret);

        var response = await server.CreateClient().SendAsync(DaemonRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ = (await ReadCodeAsync(response)).Should().Be(ServiceCallerPrincipalSource.AppNotRegisteredCode);
        _ = response.Headers.GetValues(IdentityMiddleware.RefusalCodeHeader).Should().ContainSingle()
            .Which.Should().Be(ServiceCallerPrincipalSource.AppNotRegisteredCode);
    }

    [Fact]
    public async Task WithEnforcementOn_AnAppOnboardedToTheQuarantineTenant_IsRefused()
    {
        // Spec 8.5.2: no principal may ever carry the quarantine tenant. A registration that names
        // it would hand this app every conversation on the deployment nobody has adopted yet.
        using var server = await StartAsync(
            enforce: true,
            s2sSecret: Secret,
            apps: new Dictionary<string, string>(StringComparer.Ordinal) { [DaemonAppId] = "legacy" },
            legacyTenantId: "legacy");

        var response = await server.CreateClient().SendAsync(DaemonRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ = (await ReadCodeAsync(response)).Should().Be(ServiceCallerPrincipalSource.AppTenantInvalidCode);
    }

    [Fact]
    public async Task WithEnforcementOn_AWrongSecret_IsRefused()
    {
        using var server = await StartAsync(
            enforce: true,
            s2sSecret: Secret,
            apps: new Dictionary<string, string>(StringComparer.Ordinal) { [DaemonAppId] = DaemonTenant });

        var response = await server.CreateClient().SendAsync(DaemonRequest(secret: "not-the-secret"));

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = (await ReadCodeAsync(response)).Should().Be("authentication_required");
    }

    [Fact]
    public async Task WithEnforcementOn_AndNoSecretConfigured_AServiceCallerIsStillRefused()
    {
        // The keyless dev path disables the endpoint filter's guard. If it ALSO minted an app
        // principal, anyone who typed the two header names would be admitted as an onboarded
        // service - a bypass that would arrive with the very flag meant to close the door.
        using var server = await StartAsync(
            enforce: true,
            s2sSecret: null,
            apps: new Dictionary<string, string>(StringComparer.Ordinal) { [DaemonAppId] = DaemonTenant });

        var response = await server.CreateClient().SendAsync(DaemonRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = (await ReadCodeAsync(response)).Should().Be("authentication_required");
    }

    [Fact]
    public async Task WithEnforcementOff_AServiceCallerIsUnchanged_AndStillGetsTheDevelopmentPrincipal()
    {
        // The regression gate. Identity:Enforce=false must behave EXACTLY as it did before this
        // door existed, registered apps or not.
        using var server = await StartAsync(
            enforce: false,
            s2sSecret: Secret,
            apps: new Dictionary<string, string>(StringComparer.Ordinal) { [DaemonAppId] = DaemonTenant });

        var response = await server.CreateClient().SendAsync(DaemonRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = (await response.Content.ReadAsStringAsync()).Should().Be(
            $"{ReachedBody}:legacy:{nameof(PrincipalKind.EndUser)}:dev:local:{nameof(PrincipalSource.Interactive)}");
    }

    [Fact]
    public async Task WithEnforcementOn_ABrowserRequestCarryingNoServiceMarker_IsUntouchedByThisDoor()
    {
        // The marker gate. A same-origin SPA request carries neither header and must fall through
        // to the interactive door, refused for want of a token rather than for want of a secret.
        using var server = await StartAsync(
            enforce: true,
            s2sSecret: Secret,
            apps: new Dictionary<string, string>(StringComparer.Ordinal) { [DaemonAppId] = DaemonTenant });

        var response = await server.CreateClient()
            .GetAsync(new Uri(GuardedRoute, UriKind.Relative));

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = (await ReadCodeAsync(response)).Should().Be("authentication_required");
    }

    [Theory]
    [InlineData("/api/auth/webhook/github")]
    [InlineData("/api/auth/egress-keys")]
    [InlineData("/api/lifecycle/subscriptions")]
    public async Task WithEnforcementOn_AnInfrastructureCallback_IsNotRefusedByIdentity(string path)
    {
        // These sit outside the identity boundary by decision, not by omission: they authenticate
        // with their own per-session secrets and have no user and no tenant to resolve.
        using var server = await StartAsync(enforce: true, s2sSecret: Secret);

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));

        // The webhook's Authorization header carries a SESSION SECRET, not a JWT. The bearer
        // handler cannot parse it, stashes nothing, and a guarded route would then refuse the
        // caller for presenting the credential its own endpoint requires.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sbx-session-secret-not-a-jwt");

        var response = await server.CreateClient().SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = (await response.Content.ReadAsStringAsync()).Should().StartWith(ReachedBody);
    }

    // ---- #346: CORS runs first, so preflights survive and refusals stay readable ------------------

    [Fact]
    public async Task WithEnforcementOn_ACorsPreflight_IsNotRefused_AndCarriesItsAllowOriginHeader()
    {
        // A preflight is OPTIONS with no Authorization header - browsers never attach one, by
        // specification. Refusing it kills the real request before it is ever sent.
        using var server = await StartAsync(
            enforce: true,
            s2sSecret: Secret,
            allowedOrigin: ClientOrigin);

        var request = new HttpRequestMessage(HttpMethod.Options, new Uri(GuardedRoute, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Origin", ClientOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");

        var response = await server.CreateClient().SendAsync(request);

        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        _ = response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle()
            .Which.Should().Be(ClientOrigin);
    }

    [Fact]
    public async Task WithEnforcementOn_ACorsPreflight_SurvivesEvenWhenIdentityRunsFirst()
    {
        // The preflight passthrough on its own, isolated from the ordering fix.
        //
        // With CORS registered first, the CORS middleware answers a preflight and short-circuits, so
        // IdentityMiddleware never sees one - which means the test above passes whether or not the
        // passthrough exists, and cannot be the thing that proves it. This test reproduces the
        // #346 ordering defect deliberately so identity DOES see the preflight, and pins that it
        // still lets it through. Two independent mechanisms protect a preflight; this is the second.
        using var server = await StartAsync(
            enforce: true,
            s2sSecret: Secret,
            allowedOrigin: ClientOrigin,
            corsFirst: false);

        var request = new HttpRequestMessage(HttpMethod.Options, new Uri(GuardedRoute, UriKind.Relative));
        request.Headers.TryAddWithoutValidation("Origin", ClientOrigin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");

        var response = await server.CreateClient().SendAsync(request);

        _ = response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WithEnforcementOn_ARefusal_StillCarriesItsAllowOriginHeader()
    {
        // The second half of #346, and the one that ordering alone fixes. The SPA's whole rejection
        // screen is driven by the stable code in X-Identity-Refusal; a cross-origin client that
        // cannot read the response sees an opaque network error instead of the explanation the 403
        // exists to give it.
        using var server = await StartAsync(
            enforce: true,
            s2sSecret: Secret,
            allowedOrigin: ClientOrigin);

        var request = DaemonRequest();
        request.Headers.TryAddWithoutValidation("Origin", ClientOrigin);

        var response = await server.CreateClient().SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _ = response.Headers.GetValues("Access-Control-Allow-Origin").Should().ContainSingle()
            .Which.Should().Be(ClientOrigin);
        _ = response.Headers.GetValues(IdentityMiddleware.RefusalCodeHeader).Should().ContainSingle();
    }

    [Fact]
    public async Task WithEnforcementOn_ABareOptionsRequest_IsStillGuarded()
    {
        // The non-vacuity half of the preflight passthrough. Letting every OPTIONS past would widen
        // the unguarded surface by a whole HTTP method; only a real preflight - the one carrying
        // Access-Control-Request-Method - is exempt.
        using var server = await StartAsync(enforce: true, allowedOrigin: ClientOrigin);

        var request = new HttpRequestMessage(HttpMethod.Options, new Uri(GuardedRoute, UriKind.Relative));

        var response = await server.CreateClient().SendAsync(request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void TheCorsRegistration_IsIdempotent_SoAHostMayRegisterItEarlyAndStillCallUseLmStreaming()
    {
        // Program.cs registers CORS before identity and then calls UseLmStreaming, which used to own
        // the registration. Two copies would apply the policy twice; this asserts the second call is
        // the no-op that makes the early registration safe.
        var services = new ServiceCollection();
        _ = services.AddLogging();
        _ = services.AddLmStreaming(o => o.AllowedOrigins = [ClientOrigin]);

        using var provider = services.BuildServiceProvider();
        var builder = new ApplicationBuilder(provider);

        _ = builder.UseLmStreamingCors();
        var afterFirst = builder.Properties.Count;
        _ = builder.UseLmStreamingCors();

        _ = builder.Properties.Count.Should().Be(afterFirst);
        _ = provider.GetRequiredService<IOptions<LmStreamingOptions>>().Value.AllowedOrigins
            .Should().ContainSingle().Which.Should().Be(ClientOrigin);
    }
}
