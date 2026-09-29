using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// The chat-mode catalog: a mode is a persona, a system prompt and a tool allowlist, so every route
/// below decides what an agent is subsequently allowed to say and do.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two guards cover this controller, and they answer different questions.</b>
/// <c>IdentityMiddleware</c> already gates <c>/api/chat-modes</c> — it guards every <c>/api</c> path
/// that is not one of its three named exemptions, and this is not one of them — so under
/// <c>Identity:Enforce</c> an anonymous caller is refused 401 before any action here runs. That guard
/// asks "is there a principal at all". <see cref="InboundS2SAuthAttribute"/> asks a different
/// question: does a caller <i>claiming to be a service</i> hold the inbound shared secret.
/// </para>
/// <para>
/// The second guard was missing here while three sibling controllers carried it
/// (<see cref="ConversationsController"/>, <see cref="WorkspacesController"/>,
/// <see cref="FileBrowserController"/>), and the gap was observable: with
/// <c>Identity:Enforce</c> off — the default — and <c>Auth:S2SInboundSecret</c> set, a caller
/// presenting <c>X-Sbx-App-Id</c> with a wrong or absent <c>X-S2S-Auth</c> was refused 401 on all
/// three of those and reached <c>Create</c>/<c>Update</c>/<c>Delete</c>/<c>Copy</c> here. That is the
/// same shape as the <c>api/workspaces</c> hole closed in <c>7c9b4dc3</c>, on a surface where the
/// forged write edits the instructions an agent then executes.
/// </para>
/// <para>
/// Ownership and per-tenant scoping of modes are deliberately NOT here: <see cref="ChatMode"/> has no
/// owner field and <see cref="IChatModeStore"/> takes no caller, by design. That is issue #304's work,
/// and this guard neither anticipates nor blocks it.
/// </para>
/// </remarks>
[ApiController]
[Route("api/chat-modes")]
[InboundS2SAuth]
public class ChatModesController(
    IChatModeStore modeStore,
    Services.SandboxEnvApplier envApplier,
    SandboxApps.SandboxAppCatalog? sandboxAppCatalog = null,
    Identity.ConversationAuthorizer? authorizer = null,
    SandboxApps.ISandboxAppModeReadiness? modeReadiness = null
) : ControllerBase
{
    private bool HostAvailable =>
        sandboxAppCatalog is not null
        && authorizer is not null
        && authorizer.Current is { Source: PrincipalSource.Interactive, Actor.Kind: PrincipalKind.EndUser }
        && sandboxAppCatalog.IsAvailableFor(Request.Host.Host, Request.IsHttps, authorizer.IsEnforced);

    private async Task<bool> BuilderAvailableAsync(string workspace, CancellationToken ct) =>
        HostAvailable && modeReadiness is not null && await modeReadiness.IsReadyAsync(workspace, ct);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string workspace = "default", CancellationToken ct = default)
    {
        var modes = await modeStore.GetAllModesAsync(ct);
        if (HostAvailable)
            Response.Headers["X-Mini-Web-App-Activation"] = "available";
        return Ok(
            await BuilderAvailableAsync(workspace, ct)
                ? modes
                : [.. modes.Where(m => m.Id != SystemChatModes.MiniWebAppBuilderModeId)]
        );
    }

    [HttpGet("{modeId}")]
    public async Task<IActionResult> Get(
        string modeId,
        [FromQuery] string workspace = "default",
        CancellationToken ct = default
    )
    {
        var mode = await modeStore.GetModeAsync(modeId, ct);
        if (modeId == SystemChatModes.MiniWebAppBuilderModeId && !await BuilderAvailableAsync(workspace, ct))
            return NotFound();
        return mode != null ? Ok(mode) : NotFound();
    }

    [HttpPost("mini-web-app-builder/activate")]
    public async Task<IActionResult> ActivateMiniWebApps(
        [FromQuery] string workspace = "default",
        CancellationToken ct = default
    )
    {
        if (!HostAvailable || modeReadiness is null)
            return NotFound();
        return await modeReadiness.ActivateAsync(workspace, ct) ? NoContent() : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ChatModeCreateUpdate createData, CancellationToken ct = default)
    {
        if (InvalidMode(createData) is { } invalid)
        {
            return invalid;
        }

        try
        {
            var mode = await modeStore.CreateModeAsync(createData, ct);
            return Created($"/api/chat-modes/{mode.Id}", mode);
        }
        catch (SandboxEnvValidationException ex)
        {
            return BadRequest(
                new
                {
                    error = ex.Message,
                    code = "invalid_env",
                    layer = ex.Layer,
                    keys = ex.Keys,
                }
            );
        }
    }

    [HttpPut("{modeId}")]
    public async Task<IActionResult> Update(
        string modeId,
        [FromBody] ChatModeCreateUpdate updateData,
        CancellationToken ct = default
    )
    {
        if (InvalidMode(updateData) is { } invalid)
        {
            return invalid;
        }

        try
        {
            var mode = await modeStore.UpdateModeAsync(modeId, updateData, ct);

            if (updateData.EnvIsSet)
            {
                // The mode is persisted from here on; see WorkspacesController.Update for why the reapply
                // runs on CancellationToken.None and how a partial failure is reported.
                var reapply = await envApplier.ReapplyForModeAsync(modeId, CancellationToken.None);
                if (reapply.InvalidEnvFailure is not null)
                {
                    return BadRequest(reapply.ToSavedButNotAppliedBody($"Mode '{modeId}'", "mode"));
                }
            }

            return Ok(mode);
        }
        catch (SandboxEnvValidationException ex)
        {
            return BadRequest(
                new
                {
                    error = ex.Message,
                    code = "invalid_env",
                    layer = ex.Layer,
                    keys = ex.Keys,
                }
            );
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

    /// <summary>
    /// The CRUD-boundary guard for the per-mode sub-agent prompt fragment (#610): an invalid
    /// <c>subAgentPromptPlacement</c> is refused with 400 here so it can never be persisted —
    /// the fold point downstream never re-validates. System modes get the same rule at yaml load
    /// (<see cref="Persistence.SystemChatModes"/>).
    /// </summary>
    private static BadRequestObjectResult? InvalidMode(ChatModeCreateUpdate data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (
            data.SubAgentPromptPlacementIsSet
            && !Services.ModeSubAgentPrompt.IsValidPlacement(data.SubAgentPromptPlacement)
        )
        {
            return new BadRequestObjectResult(
                new
                {
                    error = $"Invalid subAgentPromptPlacement '{data.SubAgentPromptPlacement}'. "
                        + $"Valid values: {Services.ModeSubAgentPrompt.Prepend}, {Services.ModeSubAgentPrompt.Append}.",
                }
            );
        }

        var policyError = Services.ModeSubAgentPolicy.Validate(
            data.SubAgentReasoningEffort,
            data.SubAgentModelIntelligenceByType,
            data.DefaultSubAgentModelIntelligence
        );
        return policyError is null ? null : new BadRequestObjectResult(new { error = policyError });
    }

    [HttpPost("{modeId}/copies")]
    public async Task<IActionResult> Copy(
        string modeId,
        [FromBody] ChatModeCopy copyData,
        CancellationToken ct = default
    )
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
