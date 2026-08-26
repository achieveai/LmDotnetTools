using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>Serves the tool catalog the Modes editor renders its checkboxes from.</summary>
[ApiController]
[Route("api/tools")]
public class ToolsController(IToolCatalog toolCatalog) : ControllerBase
{
    /// <summary>
    ///     Lists every selectable tool, grouped, including the synthetic <c>group:*</c> wildcard rows.
    /// </summary>
    /// <remarks>
    ///     The response is still a flat array of <c>ToolDefinition</c> — the grouping travels on each
    ///     entry rather than as a nested structure, so an older client that only reads
    ///     <c>name</c>/<c>description</c> keeps working.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct = default) =>
        Ok(await toolCatalog.GetAsync(ct));
}
