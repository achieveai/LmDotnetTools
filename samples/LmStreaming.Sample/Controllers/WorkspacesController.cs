using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// Workspace CRUD. Guarded by <see cref="InboundS2SAuthAttribute"/> for the same reason
/// <c>ConversationsController</c> is: this is part of the <b>credential-passthrough surface</b>. The
/// daemon's S2S client calls <c>GET</c>/<c>POST api/workspaces</c> with the caller-credential marker
/// (<c>X-Sbx-App-Id</c>) so the workspace it creates belongs to the app whose identity it forwards.
/// Without the guard those routes accepted a forged <c>X-Sbx-App-Id</c> and a missing or wrong
/// <c>X-S2S-Auth</c> and still returned <c>201 Created</c> — while the identical credentials were
/// correctly rejected with <c>401</c> one controller over. A workspace is a directory the gateway
/// mounts into an agent container, so minting one under another app's identity is not a bookkeeping
/// nuisance.
/// <para>
/// The guard is marker-gated, so this does NOT lock the interactive API: the SPA calls these routes
/// with plain <c>fetch</c> and no S2S markers, and still passes through untouched. Only requests that
/// claim to be a service must prove it.
/// </para>
/// </summary>
[ApiController]
[Route("api/workspaces")]
[InboundS2SAuth]
public sealed class WorkspacesController(IWorkspaceStore store) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var workspaces = await store.GetAllAsync(ct);
        return Ok(workspaces);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct = default)
    {
        var workspace = await store.GetAsync(id, ct);
        return workspace != null ? Ok(workspace) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] WorkspaceCreate createData, CancellationToken ct = default)
    {
        try
        {
            var workspace = await store.CreateAsync(createData, ct);
            return Created($"/api/workspaces/{workspace.Id}", workspace);
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
        CancellationToken ct = default
    )
    {
        try
        {
            var workspace = await store.UpdateAsync(id, updateData, ct);
            return Ok(workspace);
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
}
