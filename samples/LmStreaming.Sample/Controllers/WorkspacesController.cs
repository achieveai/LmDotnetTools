using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

[ApiController]
[Route("api/workspaces")]
[InboundS2SAuth]
public sealed class WorkspacesController(
    IWorkspaceStore store,
    WorkspaceCatalogCompatibilityService compatibility,
    GatewayWorkspaceCatalogIdentity identity) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        try
        {
            var workspaces = await store.GetAllAsync(ct);
            var views = new List<WorkspaceView>(workspaces.Count);
            WorkspaceCompatibilityResult? gatewayProbe = null;
            foreach (var workspace in workspaces)
            {
                var result = await compatibility.EvaluateAsync(workspace, ct);
                gatewayProbe ??= result;
                views.Add(workspace.ToView(result));
            }

            var available = gatewayProbe?.Compatibility is not WorkspaceCompatibility.Unknown;
            return Ok(
                new WorkspaceListResponse(
                    new WorkspaceGatewayView(
                        identity.CanonicalBaseUrl,
                        identity.AppId,
                        available,
                        available ? null : gatewayProbe?.Error
                    ),
                    views
                )
            );
        }
        catch (WorkspaceCatalogCorruptException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message, code = "workspace_catalog_unavailable" }
            );
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct = default)
    {
        try
        {
            var workspace = await store.GetAsync(id, ct);
            return workspace is null
                ? NotFound()
                : Ok(workspace.ToView(await compatibility.EvaluateAsync(workspace, ct)));
        }
        catch (WorkspaceCatalogCorruptException ex)
        {
            return CatalogUnavailable(ex);
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] WorkspaceCreate createData,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(createData);
        try
        {
            await compatibility.ValidateForMutationAsync(createData.Marketplaces ?? [], ct);
            var workspace = await store.CreateAsync(createData, ct);
            return Created(
                $"/api/workspaces/{workspace.Id}",
                workspace.ToView(await compatibility.EvaluateAsync(workspace, ct))
            );
        }
        catch (UnsupportedWorkspaceMarketplacesException ex)
        {
            return BadRequest(
                new
                {
                    error = ex.Message,
                    code = "unsupported_marketplaces",
                    unsupportedMarketplaces = ex.UnsupportedMarketplaces,
                    availableMarketplaces = ex.AvailableMarketplaces,
                }
            );
        }
        catch (WorkspaceGatewayCatalogUnavailableException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message, code = "gateway_catalog_unavailable" }
            );
        }
        catch (WorkspaceCatalogCorruptException ex)
        {
            return CatalogUnavailable(ex);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] WorkspaceUpdate updateData,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(updateData);
        try
        {
            await compatibility.ValidateForMutationAsync(updateData.Marketplaces, ct);
            var workspace = await store.UpdateAsync(id, updateData, ct);
            return Ok(workspace.ToView(await compatibility.EvaluateAsync(workspace, ct)));
        }
        catch (UnsupportedWorkspaceMarketplacesException ex)
        {
            return BadRequest(
                new
                {
                    error = ex.Message,
                    code = "unsupported_marketplaces",
                    unsupportedMarketplaces = ex.UnsupportedMarketplaces,
                    availableMarketplaces = ex.AvailableMarketplaces,
                }
            );
        }
        catch (WorkspaceGatewayCatalogUnavailableException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message, code = "gateway_catalog_unavailable" }
            );
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private ObjectResult CatalogUnavailable(WorkspaceCatalogCorruptException ex) =>
        StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new { error = ex.Message, code = "workspace_catalog_unavailable" }
        );
}
