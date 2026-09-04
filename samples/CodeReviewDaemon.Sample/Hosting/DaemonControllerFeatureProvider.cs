using System.Reflection;
using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using CodeReviewDaemon.Sample.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace CodeReviewDaemon.Sample.Hosting;

/// <summary>
/// Restricts MVC controller discovery to the daemon's two gateway callbacks plus the private audit and
/// typed-publication controllers only when their independent feature flags are explicitly enabled.
/// </summary>
/// <remarks>
/// Both callbacks come from the same gateway and are authenticated the same way (a per-session secret
/// via <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.Auth.SessionSecretStore"/>); the discovery route
/// exists solely so a missing route (404) no longer tears down the sandbox session. The
/// daemon still exposes no other surface: filtering at the <see cref="ControllerFeatureProvider"/> level
/// — rather than relying on "we happen to reference an assembly with these controllers" — keeps that
/// guarantee explicit, and is what the route-exposure test (AC#4) asserts against.
/// </remarks>
internal sealed class DaemonControllerFeatureProvider(
    bool enableReviewAuditIngestion,
    bool enableTypedReviewPublication = false
) : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo typeInfo)
    {
        if (!base.IsController(typeInfo))
        {
            return false;
        }

        var type = typeInfo.AsType();
        return type == typeof(AuthWebhookController)
            || type == typeof(DiscoveryController)
            || (enableReviewAuditIngestion && type == typeof(ReviewAuditController))
            || (enableTypedReviewPublication && type == typeof(ReviewPublicationController));
    }
}
