using System.Security.Claims;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.Tests.Identity;

/// <summary>
/// Pins the contract between our <c>OnTokenValidated</c> handler and the one
/// <c>Microsoft.Identity.Web</c> installs ahead of it.
/// </summary>
/// <remarks>
/// The two handlers are chained rather than replaced, which means ours runs even when the inner one
/// has already decided to reject the token. Since <see cref="IdentityMiddleware"/> admits a request
/// on the stashed resolution alone, "ours runs anyway" and "ours stashes anyway" would be an
/// authentication bypass.
/// </remarks>
public sealed class TokenValidatedChainingTests
{
    private static TokenValidatedContext CreateContext()
    {
        var services = new ServiceCollection();
        _ = services.AddLogging();
        _ = services.AddSingleton(TimeProvider.System);
        _ = services.AddSingleton<ITenantStore, StubTenantStore>();
        _ = services.AddSingleton<IAuditSink, RecordingAuditSink>();
        _ = services.Configure<IdentityOptions>(_ => { });
        _ = services.AddSingleton<PrincipalFactory>();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };

        var scheme = new AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(JwtBearerHandler));

        return new TokenValidatedContext(httpContext, scheme, new JwtBearerOptions())
        {
            Principal = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim("tid", "tenant-1"), new Claim("oid", "user-1")],
                    authenticationType: "Bearer")),
        };
    }

    [Fact]
    public async Task WhenTheInnerHandlerRejectsTheToken_NoPrincipalIsStashedForTheMiddleware()
    {
        var context = CreateContext();

        // Exactly what Microsoft.Identity.Web does when one of ITS checks fails: set a failed
        // Result and return. It does not throw, and it leaves Principal populated - so our handler
        // sees a perfectly readable set of claims belonging to a token that has been refused.
        await LmStreaming.Sample.Identity.IdentityServiceCollectionExtensions.OnTokenValidatedAsync(
            context,
            ctx =>
            {
                ctx.Fail("the inner handler refused this token");
                return Task.CompletedTask;
            });

        Assert.False(
            context.HttpContext.Items.ContainsKey(IdentityHttpItems.ResolutionKey),
            "A rejected token must not leave a resolution behind - the middleware admits a request "
                + "on the presence of one.");
    }

    [Fact]
    public async Task WhenTheInnerHandlerAcceptsTheToken_ThePrincipalIsStashedAsUsual()
    {
        // The guard must not be so eager that it also suppresses the ordinary path.
        var context = CreateContext();

        await LmStreaming.Sample.Identity.IdentityServiceCollectionExtensions.OnTokenValidatedAsync(
            context,
            _ => Task.CompletedTask);

        Assert.True(context.HttpContext.Items.ContainsKey(IdentityHttpItems.ResolutionKey));
    }

    [Fact]
    public async Task WithNoInnerHandlerAtAll_ThePrincipalIsStillStashed()
    {
        var context = CreateContext();

        await LmStreaming.Sample.Identity.IdentityServiceCollectionExtensions.OnTokenValidatedAsync(context, inner: null);

        Assert.True(context.HttpContext.Items.ContainsKey(IdentityHttpItems.ResolutionKey));
    }
}
