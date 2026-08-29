using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using LmStreaming.Sample.Controllers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace LmStreaming.Sample.Identity;

/// <summary>Registers and wires the identity pipeline into the sample host.</summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// The Entra app registration whose absence leaves no JWT bearer handler registered, and so no
    /// interactive sign-in.
    /// </summary>
    public const string ClientIdConfigKey = $"{IdentityController.AzureAdSectionName}:ClientId";

    /// <summary>
    /// Registers the identity services: options, the tenant registry, the audit sink, the principal
    /// factory and accessor, the startup seed, and - only when an Entra app registration is
    /// configured - the JWT bearer handler.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Root configuration.</param>
    public static IServiceCollection AddSampleIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        _ = services.Configure<IdentityOptions>(configuration.GetSection(IdentityOptions.SectionName));
        _ = services.AddHttpContextAccessor();

        _ = services.AddSingleton<IAuditSink>(sp => new LoggingAuditSink(
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<TimeProvider>()
        ));

        // Constructed inline for the same reason the notify-wait store is (see Program.cs): an
        // ISqliteConnectionFactory is IAsyncDisposable-only, and a container-tracked
        // IAsyncDisposable-only singleton makes the synchronous ServiceProvider.Dispose() throw -
        // which every WebApplicationFactory-based test would hit on teardown.
        var databasePath = configuration[$"{IdentityOptions.SectionName}:DatabasePath"];
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.Combine(AppContext.BaseDirectory, "identity.db");
        }

        // ONE factory for BOTH registries, so `tenants`, `tenant_admins` and `resource_grants`
        // share a database file and a migration runner. They are deliberately NOT in the
        // conversation database: the sample's conversations are a FILE store, so there is no file
        // in which a `tenants` row and a `thread_metadata` row could ever be siblings. See the PR
        // body for why the spec's single-file assumption does not survive that, and what replaced
        // it (a startup repair that reads the real registry rather than whichever `tenants` table
        // happens to sit in the same file).
        var identityConnectionFactory = new SqliteConnectionFactory(databasePath);

        _ = services.AddSingleton<ITenantStore>(_ => new SqliteTenantStore(identityConnectionFactory));
        _ = services.AddSingleton<IResourceGrantStore>(_ => new SqliteResourceGrantStore(identityConnectionFactory));

        _ = services.AddSingleton<IEnforcementGate, OptionsEnforcementGate>();
        _ = services.AddSingleton<IResourceAccessPolicy>(sp => new ResourceAccessPolicy(
            sp.GetRequiredService<IResourceGrantStore>(),
            sp.GetRequiredService<IAuditSink>(),
            sp.GetRequiredService<IEnforcementGate>(),
            sp.GetRequiredService<TimeProvider>()
        ));

        _ = services.AddSingleton<PrincipalFactory>();

        // The service-to-service front door (#345, spec 4.2 step 1). Registered as a principal
        // SOURCE rather than left to the endpoint filter, because a filter runs downstream of
        // IdentityMiddleware and cannot stop a refusal that has already been written. Order within
        // the collection is the order the middleware consults them in; this is currently the only
        // one, and the interactive door is not in the list at all because the bearer handler has
        // already stashed its outcome by the time the middleware runs.
        _ = services.AddSingleton<IRequestPrincipalSource, ServiceCallerPrincipalSource>();

        _ = services.AddSingleton<IPrincipalAccessor, HttpContextPrincipalAccessor>();
        _ = services.AddSingleton<ConversationAuthorizer>();
        _ = services.AddSingleton<WebSocketConversationGate>();
        _ = services.AddHostedService<TenantSeedHostedService>();
        _ = services.AddHostedService<ConversationOwnershipRepairHostedService>();

        AddBearerAuthentication(services, configuration);

        return services;
    }

    /// <summary>
    /// Inserts the WebSocket credential promotion, authentication, authorization and the principal
    /// middleware into the pipeline. Must run before endpoint execution and after routing - and, for
    /// the WebSocket transports to be inside the identity boundary at all, before the <c>/ws</c>
    /// endpoints are mapped.
    /// </summary>
    /// <param name="app">The application pipeline.</param>
    /// <exception cref="InvalidOperationException">
    /// <c>Identity:Enforce</c> is on and no front door on this host can ever establish a principal.
    /// </exception>
    public static IApplicationBuilder UseSampleIdentity(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        ValidateSomeFrontDoorExists(app.ApplicationServices);

        // Before authentication, and that ordering is the whole point (#342). A browser cannot put a
        // header on a WebSocket handshake, so the credential arrives in Sec-WebSocket-Protocol;
        // lifting it into Authorization HERE means the bearer handler and every
        // IRequestPrincipalSource below validate a /ws credential with the same code that validates
        // a REST one, and neither of them needs to know a WebSocket exists.
        _ = app.Use(
            static async (context, next) =>
            {
                _ = IdentityMiddleware.PromoteWebSocketCredential(context.Request);
                await next(context).ConfigureAwait(false);
            }
        );

        _ = app.UseAuthentication();
        _ = app.UseAuthorization();

        // After authentication, so the token has been validated and its resolution stashed by the
        // time this reads it.
        _ = app.UseMiddleware<IdentityMiddleware>();

        return app;
    }

    /// <summary>
    /// Refuses to build the pipeline when <c>Identity:Enforce</c> is on and nothing on this host can
    /// ever produce a <see cref="Principal"/> (#350).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure being turned into a boot refusal is otherwise entirely silent. Enforcement with
    /// no front door means <see cref="IdentityMiddleware"/> reaches its <c>principal is null</c>
    /// branch on every guarded request and answers <c>401</c> - <c>403</c> on the <c>/ws</c>
    /// transports - forever. Nothing logs a cause, and no credential a caller presents can change
    /// the outcome, so the symptom an operator sees is a uniformly dead <c>/api</c> surface and a
    /// client that cannot get past sign-in.
    /// </para>
    /// <para>
    /// <b>The condition is "no front door", not "no <c>AzureAd:ClientId</c>".</b> An enforcing host
    /// with no interactive sign-in at all is a legitimate deployment and is one the tests boot: a
    /// service-only host authenticates through <see cref="ServiceCallerPrincipalSource"/>, and a host
    /// with its own scheme registers an <see cref="IRequestPrincipalSource"/>. Refusing on the client
    /// id alone would refuse both.
    /// </para>
    /// <para>
    /// Read from the built container rather than from configuration, and here rather than in
    /// <see cref="AddSampleIdentity"/>, for two DIFFERENT reasons - one per escape, and neither one
    /// covers both.
    /// </para>
    /// <para>
    /// An <see cref="IRequestPrincipalSource"/> is the escape that is genuinely invisible until
    /// registration has finished: a host may add one AFTER <c>AddSampleIdentity</c> returns, so
    /// nothing inside that call could have seen it.
    /// </para>
    /// <para>
    /// The bearer escape is a different case, and not a deferred one. WHETHER it is configured is
    /// legible in configuration alone - <see cref="AddBearerAuthentication"/> gates on
    /// <see cref="ClientIdConfigKey"/>, which it reads straight from <see cref="IConfiguration"/>.
    /// What defers the check is the discriminator chosen for it:
    /// <see cref="BearerPrincipalStashMarker"/> is a container registration, so reading it needs a
    /// built provider.
    /// </para>
    /// <para>
    /// <b>The marker, not the registered authentication schemes.</b> Counting schemes was the
    /// obvious proxy and it is the wrong question. This pipeline can build a
    /// <see cref="Principal"/> from exactly two places: the resolution stashed by
    /// <see cref="OnTokenValidatedAsync"/>, and an <see cref="IRequestPrincipalSource"/>. Nothing
    /// anywhere reads <c>HttpContext.User</c>. So a host that registers cookies, or a scheme of its
    /// own, populates <c>AuthenticationOptions.SchemeMap</c> and still cannot ever produce a
    /// principal - it would satisfy a scheme count and boot into precisely the dead host this gate
    /// exists to refuse. <see cref="AddPrincipalResolution"/> registers the marker in the same
    /// statement block that installs the <c>OnTokenValidated</c> handler doing the stashing, so the
    /// marker's presence is the stash wiring's presence and the two cannot drift apart.
    /// </para>
    /// </remarks>
    private static void ValidateSomeFrontDoorExists(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<IdentityOptions>>().Value;

        // With enforcement off an unauthenticated request resolves to the development principal, so
        // having no front door is the ordinary development path rather than a dead host.
        if (!options.Enforce)
        {
            return;
        }

        if (services.GetService<BearerPrincipalStashMarker>() is not null)
        {
            return;
        }

        // Any source the host registered itself. ServiceCallerPrincipalSource is excluded because it
        // is registered unconditionally, so counting it would make this test always true; whether IT
        // can authenticate anyone is the configuration question asked below.
        if (services.GetServices<IRequestPrincipalSource>().Any(source => source is not ServiceCallerPrincipalSource))
        {
            return;
        }

        // Three conditions, not two, because ServiceCallerPrincipalSource needs all three: with no
        // secret it returns null rather than admitting a caller on a header anyone can type; with no
        // registration carrying a TenantId it rejects the app id presented; and it rejects with
        // service_app_tenant_invalid when the TenantId names LegacyTenantId, because no principal may
        // carry the quarantine tenant (spec 8.5.2). A registration whose TenantId is blank is refused
        // on the same branch as one that is absent, so neither counts as an onboarding.
        //
        // The quarantine conjunct sits INSIDE the Any so a host with one quarantine entry and one
        // real one still boots - it has a working front door. Trimmed before comparing, matching what
        // ServiceCallerPrincipalSource compares.
        var secretConfigured = !string.IsNullOrWhiteSpace(
            services.GetRequiredService<IConfiguration>()[InboundS2SAuthAttribute.SecretConfigKey]
        );

        if (
            secretConfigured
            && options.Apps.Any(app =>
                !string.IsNullOrWhiteSpace(app.Value?.TenantId)
                && !string.Equals(app.Value.TenantId.Trim(), options.LegacyTenantId, StringComparison.Ordinal)
            )
        )
        {
            return;
        }

        throw new InvalidOperationException(
            $"{IdentityOptions.SectionName}:Enforce is true, but no front door on this host can "
                + "establish a principal, so every route inside the identity boundary would answer "
                + "401 (403 on the /ws transports) and no credential a caller presents could change "
                + $"that. Configure one of: {ClientIdConfigKey}, for interactive sign-in; "
                + $"{InboundS2SAuthAttribute.SecretConfigKey} together with an "
                + $"{IdentityOptions.SectionName}:Apps entry naming a TenantId other than "
                + $"{IdentityOptions.SectionName}:LegacyTenantId, for service callers; "
                + "or register an IRequestPrincipalSource of your own. Set "
                + $"{IdentityOptions.SectionName}:Enforce to false to run without authentication."
        );
    }

    /// <summary>
    /// Registers the JWT bearer handler, but only when an Entra app registration is actually
    /// configured.
    /// </summary>
    /// <remarks>
    /// The authentication builder is created unconditionally so that
    /// <c>IAuthenticationSchemeProvider</c> always exists and <c>UseAuthentication</c> is always
    /// safe to call; with no handler registered the default scheme resolves to null and the
    /// middleware no-ops. That is what lets the whole pipeline ship without a single existing test
    /// needing a client id.
    /// </remarks>
    private static void AddBearerAuthentication(IServiceCollection services, IConfiguration configuration)
    {
        var authenticationBuilder = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme);

        var clientId = configuration[ClientIdConfigKey];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        _ = authenticationBuilder.AddMicrosoftIdentityWebApi(configuration, IdentityController.AzureAdSectionName);

        AddPrincipalResolution(services);
    }

    /// <summary>
    /// Hooks tenant resolution onto token validation, storing the outcome for
    /// <see cref="IdentityMiddleware"/> to act on.
    /// </summary>
    /// <remarks>
    /// A refusal is stored, never signalled through <c>context.Fail()</c>. Failing here would make
    /// the handler emit a <c>401</c> challenge, and a browser client answers a <c>401</c> by
    /// signing in again - which cannot produce a provisioned tenant, so it would loop forever.
    /// </remarks>
    private static void AddPrincipalResolution(IServiceCollection services)
    {
        _ = services.Configure<JwtBearerOptions>(
            JwtBearerDefaults.AuthenticationScheme,
            options =>
            {
                // ASP.NET Core's default is five minutes, which is generous enough that a token
                // stays usable for five minutes past its own expiry. Two minutes still absorbs
                // ordinary NTP drift between our host and the issuer (spec 5.3).
                options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(120);

                // Chained, not replaced: Microsoft.Identity.Web installs its own OnTokenValidated
                // (scope and app-role validation among others), and overwriting it would silently
                // drop those checks.
                var inner = options.Events?.OnTokenValidated;
                options.Events ??= new JwtBearerEvents();
                options.Events.OnTokenValidated = context => OnTokenValidatedAsync(context, inner);
            }
        );

        // Deliberately in the SAME statement block as the handler above, not merely on the same
        // branch. The Configure call above is the only wiring that ever writes
        // IdentityHttpItems.ResolutionKey, so a marker registered beside it reports the presence of
        // the stash itself rather than of some setting that correlates with it today.
        _ = services.AddSingleton(new BearerPrincipalStashMarker());
    }

    /// <summary>
    /// Present in the container exactly when <see cref="AddPrincipalResolution"/> has wired the
    /// bearer handler that stashes a resolution for <see cref="IdentityMiddleware"/> to read.
    /// </summary>
    /// <remarks>
    /// Carries no state and is never resolved by anything that does work; its whole purpose is to
    /// let <see cref="ValidateSomeFrontDoorExists"/> ask "is the bearer front door wired" and get an
    /// answer about THIS pipeline rather than about ASP.NET Core's scheme registry, which answers a
    /// wider question this pipeline cannot act on.
    /// </remarks>
    private sealed class BearerPrincipalStashMarker { }

    /// <summary>
    /// Runs the inner <c>OnTokenValidated</c> first, then resolves our own principal and stashes it
    /// for <see cref="IdentityMiddleware"/> to read.
    /// </summary>
    /// <remarks>
    /// Extracted from the lambda so the short-circuit below can be tested directly: the branch that
    /// matters is one that only fires when a dependency rejects a token, which is awkward to
    /// provoke through a real handler.
    /// </remarks>
    internal static async Task OnTokenValidatedAsync(
        TokenValidatedContext context,
        Func<TokenValidatedContext, Task>? inner
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        if (inner is not null)
        {
            await inner(context).ConfigureAwait(false);
        }

        // Microsoft.Identity.Web installs its own OnTokenValidated, and its checks (issuer, app
        // roles, scopes) report failure by calling context.Fail(), which sets context.Result and
        // returns - it does NOT throw and does NOT clear context.Principal.
        //
        // Continuing past that point would take the claims from a token our own dependency just
        // rejected, build a Principal from them, and stash it. IdentityMiddleware admits a request
        // on the presence of that stashed resolution alone - no controller here carries
        // [Authorize] - so the rejection would be computed and then ignored. That is an auth
        // bypass, so the failure has to end this handler.
        if (context.Result is not null)
        {
            return;
        }

        var factory = context.HttpContext.RequestServices.GetRequiredService<PrincipalFactory>();

        var resolution = await factory
            .ResolveInteractiveAsync(
                context.Principal!,
                context.HttpContext.TraceIdentifier,
                context.HttpContext.RequestAborted
            )
            .ConfigureAwait(false);

        context.HttpContext.Items[IdentityHttpItems.ResolutionKey] = resolution;
    }
}
