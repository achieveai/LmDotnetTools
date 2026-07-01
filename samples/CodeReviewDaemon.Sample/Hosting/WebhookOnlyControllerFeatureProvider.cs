using System.Reflection;
using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace CodeReviewDaemon.Sample.Hosting;

/// <summary>
/// Restricts MVC controller discovery to <see cref="AuthWebhookController"/> alone, so the daemon's
/// runtime HTTP surface is exactly <c>POST /api/auth/webhook/{provider}</c> (the sandbox gateway's
/// post-auth callback) and nothing else.
/// </summary>
/// <remarks>
/// The shared <c>LmAgentInfra</c> assembly today carries only the webhook controller, but the daemon
/// must not silently gain new routes if a future controller lands there (or in any other referenced
/// part). Filtering at the <see cref="ControllerFeatureProvider"/> level — rather than relying on
/// "we happen to reference an assembly with one controller" — keeps that guarantee explicit and is
/// what the route-exposure test (AC#4) asserts against.
/// </remarks>
internal sealed class WebhookOnlyControllerFeatureProvider : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo typeInfo) =>
        base.IsController(typeInfo) && typeInfo.AsType() == typeof(AuthWebhookController);
}
