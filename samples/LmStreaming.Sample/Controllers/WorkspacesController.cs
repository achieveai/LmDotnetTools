using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
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
    GatewayWorkspaceCatalogIdentity identity,
    IWorkspacePluginSelectionService pluginSelection) : ControllerBase
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

            // Only "the catalog could not be read" makes the gateway unavailable. An Incompatible
            // probe means the catalog answered — the gateway is up, this workspace just names
            // marketplaces it does not offer — so reporting it as unavailable would blame the
            // gateway for a per-workspace verdict and blank the whole picker on one bad row.
            var available = gatewayProbe?.Compatibility is not WorkspaceCompatibility.Unavailable;
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
            return CatalogUnavailable(ex);
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
            var marketplaces = createData.Marketplaces ?? [];
            await compatibility.ValidateForMutationAsync(marketplaces, ct);

            // A create can carry an explicit plugin selection too, and it has to be checked before
            // it is stored: persisting an unsupported selection would leave a workspace that fails
            // on every later session create, with no request left to blame.
            await compatibility.ValidatePluginsForMutationAsync(marketplaces, createData.PluginSelection, ct);

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
        catch (MalformedWorkspacePluginSelectionException ex)
        {
            // Deliberately BEFORE the unsupported-plugins catch: an unreadable entry is a bad
            // request in its own right, not a plugin the gateway happens not to offer, and it
            // previously escaped as a 500 from the message formatter of the exception below.
            return BadRequest(
                new
                {
                    error = ex.Message,
                    code = "malformed_plugin_selection",
                    malformedIndexes = ex.Indexes,
                }
            );
        }
        catch (UnsupportedWorkspacePluginsException ex)
        {
            return BadRequest(
                new
                {
                    error = ex.Message,
                    code = "unsupported_plugins",
                    unsupportedPlugins = ex.UnsupportedPlugins,
                    availablePlugins = ex.AvailablePlugins,
                }
            );
        }
        catch (GatewayPluginFilteringUnsupportedException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message, code = "gateway_plugin_filtering_unsupported" }
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

            // Only an explicit plugin selection can invalidate a live sandbox session, so only that
            // case pays for the migration. An update that omits PluginSelection is an ordinary store
            // write: routing it through the orchestrator would make every rename take the workspace
            // gate, wait for runs to go idle, and demand a pluginsRevision it has no reason to know.
            var workspace = updateData.PluginSelection.IsSet
                ? await pluginSelection.ApplyPluginSelectionUpdateAsync(id, updateData, ct)
                : await store.UpdateAsync(id, updateData, ct);

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
        catch (MalformedWorkspacePluginSelectionException ex)
        {
            // Deliberately BEFORE the unsupported-plugins catch: an unreadable entry is a bad
            // request in its own right, not a plugin the gateway happens not to offer, and it
            // previously escaped as a 500 from the message formatter of the exception below.
            return BadRequest(
                new
                {
                    error = ex.Message,
                    code = "malformed_plugin_selection",
                    malformedIndexes = ex.Indexes,
                }
            );
        }
        catch (UnsupportedWorkspacePluginsException ex)
        {
            return BadRequest(
                new
                {
                    error = ex.Message,
                    code = "unsupported_plugins",
                    unsupportedPlugins = ex.UnsupportedPlugins,
                    availablePlugins = ex.AvailablePlugins,
                }
            );
        }
        catch (GatewayPluginFilteringUnsupportedException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message, code = "gateway_plugin_filtering_unsupported" }
            );
        }
        catch (WorkspaceGatewayCatalogUnavailableException ex)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message, code = "gateway_catalog_unavailable" }
            );
        }
        catch (WorkspaceRevisionConflictException ex)
        {
            // 409, never 400: the request was well-formed and would have been accepted a moment
            // earlier. Both revisions go back so the client can re-read and retry without guessing.
            return Conflict(
                new
                {
                    error = ex.Message,
                    code = "workspace_revision_conflict",
                    expectedRevision = ex.ExpectedRevision,
                    actualRevision = ex.ActualRevision,
                }
            );
        }
        catch (SandboxSessionRestartTimeoutException ex)
        {
            // 503, not 500: nothing is broken and nothing changed — a run was simply still going.
            // The same request will succeed once it finishes, so this is explicitly retryable.
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = ex.Message, code = "sandbox_restart_timeout" }
            );
        }
        catch (SandboxSessionReplacementFailedException ex)
        {
            // 502: the failure came from the downstream gateway, not from this request. The old
            // sessions are still serving and nothing was persisted.
            return StatusCode(
                StatusCodes.Status502BadGateway,
                new { error = ex.Message, code = "sandbox_replacement_failed" }
            );
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (WorkspaceCatalogCorruptException ex)
        {
            // MUST stay above the InvalidOperationException catch below: this derives from it, so the
            // broader clause would otherwise swallow a corrupt catalog into a 400 that blames the
            // caller for a server-side storage fault. List/Get/Create already answer 503 here.
            return CatalogUnavailable(ex);
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
