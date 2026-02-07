using AchieveAi.LmDotnetTools.LmCore.Middleware;
using LmStreaming.Sample.Models;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

[ApiController]
[Route("api/tools")]
public class ToolsController(
    FunctionRegistry functionRegistry,
    IReadOnlyList<ToolDefinition> builtInToolDefinitions) : ControllerBase
{
    [HttpGet]
    public IActionResult List()
    {
        var (contracts, _) = functionRegistry.Build();
        var functionTools = contracts.Select(c => new ToolDefinition
        {
            Name = c.Name,
            Description = c.Description,
        });
        var allTools = builtInToolDefinitions.Concat(functionTools);
        return Ok(allTools);
    }
}
