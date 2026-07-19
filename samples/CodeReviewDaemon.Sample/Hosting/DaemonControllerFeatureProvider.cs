using System.Reflection;
using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using CodeReviewDaemon.Sample.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace CodeReviewDaemon.Sample.Hosting;

/// <summary>
/// Restricts MVC controller discovery to the daemon's two intentional endpoints — the sandbox gateway's
/// post-auth callback <see cref="AuthWebhookController"/> (<c>POST /api/auth/webhook/{provider}</c>) and
/// its context-discovery callback <see cref="DiscoveryController"/> (<c>POST /api/discovery/context_discovery</c>)
/// — and nothing else.
/// </summary>
/// <remarks>
/// Both callbacks come from the same gateway and are authenticated by the same shared secret; the
/// discovery route exists solely so a non-2xx response no longer tears down the sandbox session. The
/// daemon still exposes no other surface: filtering at the <see cref="ControllerFeatureProvider"/> level
/// — rather than relying on "we happen to reference an assembly with these controllers" — keeps that
/// guarantee explicit, and is what the route-exposure test (AC#4) asserts against.
/// </remarks>
internal sealed class DaemonControllerFeatureProvider : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo typeInfo) =>
        base.IsController(typeInfo)
        && (typeInfo.AsType() == typeof(AuthWebhookController)
            || typeInfo.AsType() == typeof(DiscoveryController));
}
