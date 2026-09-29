using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.SandboxApps;

[ApiController]
[Route("api/conversations/{threadId}/apps")]
public sealed class SandboxAppsController(
    SandboxAppCatalog catalog,
    SandboxAppDiscovery discovery,
    SandboxAppCapability capability,
    SandboxAppAccess access,
    SandboxAppInstanceStore instances,
    ConversationAuthorizer authorizer
) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string threadId, CancellationToken ct)
    {
        if (!catalog.IsAvailableFor(Request.Host.Host, Request.IsHttps, authorizer.IsEnforced))
            return NotFound();
        var context = await access.ResolveContextAsync(threadId, authorizer.Current, ct);
        if (context.Status != 200)
            return StatusCode(context.Status);
        if (!await capability.IsReadyAsync(context.SessionId!, ct))
            return NotFound();
        try
        {
            var discovered = await discovery.ListAsync(context.SessionId!, context.WorkspaceId!, ct);
            var configured = catalog.Apps.Select(app => AppDto(app, context.WorkspaceId!));
            return Ok(new { workspaceId = context.WorkspaceId, apps = configured.Concat(discovered.Select(AppDto)) });
        }
        catch (SandboxException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpGet("{appId}")]
    public async Task<IActionResult> Resolve(
        string threadId,
        string appId,
        [FromQuery] string? workspace,
        CancellationToken ct
    )
    {
        if (!catalog.IsAvailableFor(Request.Host.Host, Request.IsHttps, authorizer.IsEnforced))
            return NotFound();
        var context = await access.ResolveContextAsync(threadId, authorizer.Current, ct);
        if (context.Status != 200)
            return StatusCode(context.Status);
        if (workspace is not null && workspace != context.WorkspaceId)
            return NotFound();
        if (!await capability.IsReadyAsync(context.SessionId!, ct))
            return NotFound();
        try
        {
            if (catalog.TryGet(appId, out var fixedApp) && fixedApp is not null)
                return Ok(AppDto(fixedApp, context.WorkspaceId!));
            var app = await discovery.ResolveAsync(context.SessionId!, context.WorkspaceId!, appId, ct);
            return app is null ? NotFound() : Ok(AppDto(app));
        }
        catch (SandboxException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpPost("{appId}/launch")]
    public async Task<IActionResult> Launch(
        string threadId,
        string appId,
        [FromQuery] string? workspace,
        CancellationToken ct
    )
    {
        Response.Headers.CacheControl = "no-store";
        if (!catalog.IsAvailableFor(Request.Host.Host, Request.IsHttps, authorizer.IsEnforced))
            return NotFound();
        var principal = authorizer.Current;
        var context = await access.ResolveContextAsync(threadId, principal, ct);
        if (context.Status != 200)
            return StatusCode(context.Status);
        if (string.IsNullOrWhiteSpace(workspace) || workspace != context.WorkspaceId)
            return NotFound();
        if (!await capability.IsReadyAsync(context.SessionId!, ct))
            return NotFound();
        SandboxAppDefinition? app;
        try
        {
            if (!catalog.TryGet(appId, out app) || app is null)
                app = (await discovery.ResolveAsync(context.SessionId!, context.WorkspaceId!, appId, ct))?.Definition;
        }
        catch (SandboxException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (app is null)
            return NotFound();

        // Grants live at most ten minutes, and never outlive the login token when it carries exp.
        var expiry = authorizer.Clock.GetUtcNow().AddMinutes(10);
        var exp = HttpContext.User.FindFirst("exp")?.Value;
        if (long.TryParse(exp, out var epoch))
        {
            var loginExpiry = DateTimeOffset.FromUnixTimeSeconds(epoch);
            if (loginExpiry < expiry)
                expiry = loginExpiry;
        }
        if (expiry <= authorizer.Clock.GetUtcNow())
            return Unauthorized();

        SandboxAppLaunch launch;
        try
        {
            launch = instances.Issue(threadId, context.WorkspaceId!, app, principal!, catalog.AppDomain, expiry);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        var port = catalog.HttpsPort == 443 ? string.Empty : $":{catalog.HttpsPort}";
        return Ok(new { url = $"https://{launch.Host}{port}/_launch", ticket = launch.Ticket });
    }

    private static object AppDto(SandboxAppDefinition app, string workspaceId) =>
        new
        {
            kind = "mini-web-app",
            workspaceId,
            id = app.Id,
            name = app.Name,
            link = $"#mini-app?workspace={Uri.EscapeDataString(workspaceId)}&app={Uri.EscapeDataString(app.Id)}",
        };

    private static object AppDto(MiniWebAppRecord app) =>
        new
        {
            kind = app.Kind,
            workspaceId = app.WorkspaceId,
            id = app.Id,
            name = app.Name,
            link = app.Link,
        };
}
