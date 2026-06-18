using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

[ApiController]
[Route("api/workspaces")]
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
