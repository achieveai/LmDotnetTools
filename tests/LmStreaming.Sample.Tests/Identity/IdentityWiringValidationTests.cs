using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.Tests.Identity;

/// <summary>
/// Pins the startup check that refuses a host which has switched enforcement on and left itself no
/// way to authenticate anybody (#350 item 4).
/// </summary>
/// <remarks>
/// <para>
/// The failure this prevents is silent by construction. With <c>Identity:Enforce</c> on and no front
/// door configured, every route inside the identity boundary answers <c>401</c> (<c>403</c> on the
/// <c>/ws</c> transports) and no credential a caller can present changes that. There is no error,
/// no warning, and nothing in the response naming the cause - the operator sees a uniformly dead API
/// and a client stuck in sign-in.
/// </para>
/// <para>
/// <b>The check is deliberately narrower than "Enforce with no <c>AzureAd:ClientId</c>".</b> That
/// combination is a legitimate, shipped configuration: a host with no interactive sign-in at all,
/// admitting service callers through <see cref="ServiceCallerPrincipalSource"/>, or one that
/// registers an <see cref="IRequestPrincipalSource"/> of its own. Two live E2E scenarios boot exactly
/// that way. Refusing on the client id alone would have refused a working deployment, so the check
/// asks the question that actually matters - can ANY front door here ever produce a principal - and
/// the cases below pin each escape individually so that narrowing cannot quietly widen again.
/// </para>
/// <para>
/// Exercised through the real <c>IdentityServiceCollectionExtensions.UseSampleIdentity</c>
/// over a container built by the real <c>AddSampleIdentity</c>, rather than against the predicate on
/// its own: a predicate test passes just as well when nothing calls it.
/// </para>
/// </remarks>
public sealed class IdentityWiringValidationTests : IDisposable
{
    private const string ClientIdKey = "AzureAd:ClientId";
    private const string S2SSecretKey = "Auth:S2SInboundSecret";
    private const string AppTenantKey = "Identity:Apps:review-daemon:TenantId";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"identity_wiring_{Guid.NewGuid():N}.db");

    /// <summary>Runs the real pipeline wiring over a real container, returning what it threw.</summary>
    private Exception? WireWith(
        Dictionary<string, string?> settings,
        Action<IServiceCollection>? register = null)
    {
        settings["Identity:DatabasePath"] = _databasePath;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        _ = services.AddSingleton<IConfiguration>(configuration);
        _ = services.AddLogging();
        _ = services.AddSingleton(TimeProvider.System);
        _ = services.AddAuthorization();
        _ = services.AddSampleIdentity(configuration);
        register?.Invoke(services);

        using var provider = services.BuildServiceProvider();
        return Record.Exception(() => new ApplicationBuilder(provider).UseSampleIdentity());
    }

    private static Dictionary<string, string?> Enforcing() =>
        new(StringComparer.Ordinal) { ["Identity:Enforce"] = "true" };

    [Fact]
    public void AnEnforcingHostWithNoFrontDoorAtAll_RefusesToBoot()
    {
        var thrown = WireWith(Enforcing());

        // The message has to name the settings, not just the fault. An operator reading a startup
        // failure needs the keys they can change; "identity is misconfigured" sends them back to the
        // source. All three escapes are named because any one of them fixes it.
        _ = thrown.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should()
                .Contain("Identity:Enforce").And
                .Contain(ClientIdKey).And
                .Contain(S2SSecretKey).And
                .Contain("Identity:Apps");
    }

    [Fact]
    public void AHostWithEnforcementOff_BootsWithNoFrontDoorConfigured()
    {
        // The default every existing deployment and nearly every test runs on. With enforcement off
        // an unauthenticated request resolves to the development principal, so "no front door" is
        // not a dead host - it is the ordinary development path.
        var thrown = WireWith(new Dictionary<string, string?>(StringComparer.Ordinal));

        _ = thrown.Should().BeNull();
    }

    [Fact]
    public void AnEnforcingHostWithAnEntraAppRegistration_Boots()
    {
        // The interactive escape. A configured client id is what makes AddSampleIdentity register
        // the JWT bearer handler, so a browser can sign in.
        var settings = Enforcing();
        settings[ClientIdKey] = "11111111-1111-1111-1111-111111111111";
        settings["AzureAd:TenantId"] = "22222222-2222-2222-2222-222222222222";
        settings["AzureAd:Instance"] = "https://login.microsoftonline.com/";

        var thrown = WireWith(settings);

        _ = thrown.Should().BeNull();
    }

    [Fact]
    public void AnEnforcingHostThatOnboardsAServiceCaller_Boots()
    {
        // The service-to-service escape, and the reason the check is not "Enforce implies a client
        // id": this host has no interactive sign-in at all and is perfectly functional. It is the
        // shape IdentityBoundaryPipelineTests boots.
        var settings = Enforcing();
        settings[S2SSecretKey] = "inbound-secret-value";
        settings[AppTenantKey] = "tnt_daemon";

        var thrown = WireWith(settings);

        _ = thrown.Should().BeNull();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AnEnforcingHostWithHalfAServiceCallerOnboarding_RefusesToBoot(
        bool withSecret,
        bool withApp)
    {
        // Half an onboarding is not an escape, and each half fails for its own reason:
        // ServiceCallerPrincipalSource returns null when the secret is unset (it refuses to admit a
        // caller on the strength of a header anyone can type), and rejects when the presented app id
        // resolves to no registration with a tenant. Pinned as a pair because a check that asked for
        // "the secret OR an app" would pass both of these and still leave the host dead.
        var settings = Enforcing();
        if (withSecret)
        {
            settings[S2SSecretKey] = "inbound-secret-value";
        }

        if (withApp)
        {
            settings[AppTenantKey] = "tnt_daemon";
        }

        _ = WireWith(settings).Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void AnEnforcingHostWithAnAppRegistrationThatNamesNoTenant_RefusesToBoot()
    {
        // A registered key whose TenantId is blank is refused at request time by
        // ServiceCallerPrincipalSource exactly as an unregistered one is, so counting it as an
        // onboarding would let the check pass over a host that still cannot authenticate anybody.
        var settings = Enforcing();
        settings[S2SSecretKey] = "inbound-secret-value";
        settings[AppTenantKey] = "   ";

        _ = WireWith(settings).Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void AnEnforcingHostThatRegistersItsOwnPrincipalSource_Boots()
    {
        // The extensibility escape. IRequestPrincipalSource is the documented seam for a host that
        // authenticates its own way, and a source registered by the host is invisible to any check
        // that reads configuration alone - which is why this check reads the container instead. It
        // is the shape WebSocketConversationAuthorizationTests boots: enforcing, no client id, no
        // apps, and a principal source of its own.
        var thrown = WireWith(
            Enforcing(),
            services => services.AddSingleton<IRequestPrincipalSource, StubPrincipalSource>());

        _ = thrown.Should().BeNull();
    }

    public void Dispose() => File.Delete(_databasePath);

    private sealed class StubPrincipalSource : IRequestPrincipalSource
    {
        public ValueTask<PrincipalResolution?> ResolveAsync(HttpContext context, CancellationToken ct) =>
            ValueTask.FromResult<PrincipalResolution?>(null);
    }
}
