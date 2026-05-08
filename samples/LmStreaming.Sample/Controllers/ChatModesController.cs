using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

[ApiController]
[Route("api/chat-modes")]
public class ChatModesController(IChatModeStore modeStore) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default)
    {
        var modes = await modeStore.GetAllModesAsync(ct);
        return Ok(modes);
    }

    [HttpGet("{modeId}")]
    public async Task<IActionResult> Get(string modeId, CancellationToken ct = default)
    {
        var mode = await modeStore.GetModeAsync(modeId, ct);
        return mode != null ? Ok(mode) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] ChatModeCreateUpdate createData,
        CancellationToken ct = default)
    {
        var mode = await modeStore.CreateModeAsync(createData, ct);
        return Created($"/api/chat-modes/{mode.Id}", mode);
    }

    [HttpPut("{modeId}")]
    public async Task<IActionResult> Update(
        string modeId,
        [FromBody] ChatModeCreateUpdate updateData,
        CancellationToken ct = default)
    {
        try
        {
            var mode = await modeStore.UpdateModeAsync(modeId, updateData, ct);
            return Ok(mode);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{modeId}")]
    public async Task<IActionResult> Delete(string modeId, CancellationToken ct = default)
    {
        try
        {
            await modeStore.DeleteModeAsync(modeId, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{modeId}/copies")]
    public async Task<IActionResult> Copy(
        string modeId,
        [FromBody] ChatModeCopy copyData,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(copyData);
        try
        {
            var mode = await modeStore.CopyModeAsync(modeId, copyData.NewName, ct);
            return Created($"/api/chat-modes/{mode.Id}", mode);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
