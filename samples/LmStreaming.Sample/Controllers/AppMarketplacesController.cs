using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>Body of <c>PUT api/marketplaces/app/{alias}</c>. A blank <see cref="Ref"/> means <c>latest</c>.</summary>
public sealed record RegisterAppMarketplaceRequest(string Url, string? Ref);

/// <summary>
/// Lets the SPA manage this app's git marketplaces on the sandbox gateway (gateway ADR 0045): register (or
/// re-point) an alias, list the registrations with their resolved commit and last sync error, sync now, and
/// remove. A gateway rejection keeps its status and <c>error_code</c> (<c>{ error, detail }</c>) so the UI can say
/// why; an unreachable gateway is <c>503 marketplace_gateway_unavailable</c>, like the catalog endpoint.
/// Register, sync and remove change what every user's new sandboxes mount, so they also need the operator secret
/// (<see cref="OperatorSecretAuthAttribute"/>); listing does not.
/// </summary>
[ApiController]
[Route("api/marketplaces/app")]
public class AppMarketplacesController(IAppMarketplaceClient client, ILogger<AppMarketplacesController> logger)
    : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> List(CancellationToken ct) =>
        Run("list", alias: null, async () => Ok(await client.ListAsync(ct)));

    [HttpPut("{alias}")]
    [OperatorSecretAuth(RefuseWithForbidden = true)]
    public Task<IActionResult> Register(
        string alias,
        [FromBody] RegisterAppMarketplaceRequest request,
        CancellationToken ct
    ) =>
        Run(
            "register",
            alias,
            async () =>
            {
                var row = await client.RegisterAsync(
                    alias,
                    request.Url,
                    string.IsNullOrWhiteSpace(request.Ref) ? null : request.Ref.Trim(),
                    ct
                );
                logger.LogInformation(
                    "Registered app marketplace {Alias} {Url} {Ref} at {ResolvedSha}",
                    row.Alias,
                    row.Url,
                    row.Ref,
                    row.ResolvedSha
                );
                return Ok(row);
            }
        );

    [HttpPost("{alias}/sync")]
    [OperatorSecretAuth(RefuseWithForbidden = true)]
    public Task<IActionResult> Sync(string alias, CancellationToken ct) =>
        Run("sync", alias, async () => Ok(await client.SyncAsync(alias, ct)));

    [HttpDelete("{alias}")]
    [OperatorSecretAuth(RefuseWithForbidden = true)]
    public Task<IActionResult> Remove(string alias, CancellationToken ct) =>
        Run(
            "remove",
            alias,
            async () =>
                await client.RemoveAsync(alias, ct)
                    ? NoContent()
                    : NotFound(new { error = "marketplace_not_registered", detail = $"'{alias}' is not registered." })
        );

    private async Task<IActionResult> Run(string action, string? alias, Func<Task<IActionResult>> call)
    {
        try
        {
            return await call();
        }
        catch (SandboxException ex) when (ex.StatusCode is { } status and >= 400)
        {
            logger.LogWarning(
                ex,
                "App marketplace {Action} for {Alias} rejected: {StatusCode} {ErrorCode}",
                action,
                alias,
                status,
                ex.ErrorCode
            );
            // A gateway 401/403 rejects the SAMPLE's app credential, not the user's session: passing it through
            // would make the client treat it as an expired sign-in. Report it as a bad gateway instead.
            var forwarded = status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden
                ? StatusCodes.Status502BadGateway
                : status;
            return StatusCode(forwarded, new { error = ex.ErrorCode ?? "gateway_error", detail = ex.Message });
        }
        catch (SandboxException ex)
        {
            // No HTTP error status: the gateway was unreachable, timed out, or answered a 2xx the SDK could not read.
            logger.LogWarning(ex, "App marketplace {Action} for {Alias} failed: {Kind}", action, alias, ex.Kind);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "marketplace_gateway_unavailable", detail = ex.Message }
            );
        }
    }
}
