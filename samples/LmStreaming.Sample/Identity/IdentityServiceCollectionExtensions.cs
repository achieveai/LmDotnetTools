using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using LmStreaming.Sample.Controllers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

namespace LmStreaming.Sample.Identity;

/// <summary>Registers and wires the identity pipeline into the sample host.</summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the identity services: options, the tenant registry, the audit sink, the principal
    /// factory and accessor, the startup seed, and - only when an Entra app registration is
    /// configured - the JWT bearer handler.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Root configuration.</param>
    public static IServiceCollection AddSampleIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        _ = services.Configure<IdentityOptions>(configuration.GetSection(IdentityOptions.SectionName));
        _ = services.AddHttpContextAccessor();

        _ = services.AddSingleton<IAuditSink>(sp => new LoggingAuditSink(
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<TimeProvider>()));

        // Constructed inline for the same reason the notify-wait store is (see Program.cs): an
        // ISqliteConnectionFactory is IAsyncDisposable-only, and a container-tracked
        // IAsyncDisposable-only singleton makes the synchronous ServiceProvider.Dispose() throw -
        // which every WebApplicationFactory-based test would hit on teardown.
        var databasePath = configuration[$"{IdentityOptions.SectionName}:DatabasePath"];
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.Combine(AppContext.BaseDirectory, "identity.db");
        }

        _ = services.AddSingleton<ITenantStore>(
            _ => new SqliteTenantStore(new SqliteConnectionFactory(databasePath)));

        _ = services.AddSingleton<PrincipalFactory>();
        _ = services.AddSingleton<IPrincipalAccessor, HttpContextPrincipalAccessor>();
        _ = services.AddHostedService<TenantSeedHostedService>();

        AddBearerAuthentication(services, configuration);

        return services;
    }

    /// <summary>
    /// Inserts authentication, authorization and the principal middleware into the pipeline. Must
    /// run before endpoint execution and after routing.
    /// </summary>
    /// <param name="app">The application pipeline.</param>
    public static IApplicationBuilder UseSampleIdentity(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        _ = app.UseAuthentication();
        _ = app.UseAuthorization();

        // After authentication, so the token has been validated and its resolution stashed by the
        // time this reads it.
        _ = app.UseMiddleware<IdentityMiddleware>();

        return app;
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

        var clientId = configuration[$"{IdentityController.AzureAdSectionName}:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        _ = authenticationBuilder.AddMicrosoftIdentityWebApi(
            configuration,
            IdentityController.AzureAdSectionName);

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
    private static void AddPrincipalResolution(IServiceCollection services) =>
        services.Configure<JwtBearerOptions>(
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
            });
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
        Func<TokenValidatedContext, Task>? inner)
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
                context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        context.HttpContext.Items[IdentityHttpItems.ResolutionKey] = resolution;
    }

}
