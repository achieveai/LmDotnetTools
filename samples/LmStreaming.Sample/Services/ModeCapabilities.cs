using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
///     What a chat mode is allowed to do, derived from the mode's own tool selection.
/// </summary>
/// <remarks>
///     <para>
///         This replaces gating on <c>mode.Id == SystemChatModes.WorkspaceAgentModeId</c>. That
///         equality check meant a COPY of Workspace Agent — a new id — silently got no sandbox
///         session, no sandbox tools, no workflow launch tools and no collaboration surface, which is
///         exactly the "my cloned mode has none of the tools" symptom. Deriving from the selection
///         makes the capability a property of what the mode asks for, so a copy behaves like its
///         original and a narrowed copy behaves like what the user narrowed it to.
///     </para>
///     <para>
///         <b>Legacy modes keep their old behaviour.</b> When a mode records no capability selection
///         (<see cref="ModeToolSelection.IsLegacy" />), the result is <see cref="LegacyDefaults" />:
///         no sandbox, no workflow tools, sub-agents ON, collaboration off. Sub-agents are on because
///         they always have been for every middleware-provider conversation — treating a legacy null
///         as "nothing selected" would strip the Agent tool from every existing mode.
///     </para>
/// </remarks>
public sealed record ModeCapabilities
{
    /// <summary>Whether this mode needs a sandbox gateway session for each conversation.</summary>
    public required bool NeedsSandbox { get; init; }

    /// <summary>
    ///     Which sandbox tools to expose: <c>null</c> means "every tool the gateway offers", including
    ///     ones a marketplace plugin adds later. A non-null set is an explicit allow-list.
    /// </summary>
    public required IReadOnlySet<string>? SandboxToolAllowList { get; init; }

    /// <summary>Whether the workflow authoring/mutation tools (<c>SetWorkflow</c>, <c>AddNode</c>, …) are exposed.</summary>
    public required bool WorkflowAuthoringTools { get; init; }

    /// <summary>Whether the <c>StartWorkflowAgent</c> launch family is exposed.</summary>
    public required bool StartWorkflowTools { get; init; }

    /// <summary>Whether sub-agent delegation (<c>Agent</c>, <c>SendMessage</c>, …) is offered.</summary>
    public required bool SubAgents { get; init; }

    /// <summary>
    ///     Whether the hierarchy-wide collaboration surface (<c>CheckAgents</c>/<c>WaitForAgents</c>/
    ///     <c>GetAgents</c>) is on by default for this mode, absent an explicit host override.
    /// </summary>
    public required bool Collaboration { get; init; }

    /// <summary>
    ///     The sub-agent tools that only exist under collaboration. Selecting any of them is how a mode
    ///     asks for the collaboration surface.
    /// </summary>
    public static readonly IReadOnlyList<string> CollaborationToolNames =
        [
            SubAgentToolProvider.CheckAgentsToolName,
            SubAgentToolProvider.WaitForAgentsToolName,
            SubAgentToolProvider.GetAgentsToolName,
        ];

    /// <summary>
    ///     What every mode got before capability selection existed: sub-agents, nothing else. A mode
    ///     with a null <c>EnabledCapabilityTools</c> resolves to exactly this.
    /// </summary>
    public static ModeCapabilities LegacyDefaults { get; } =
        new()
        {
            NeedsSandbox = false,
            SandboxToolAllowList = null,
            WorkflowAuthoringTools = false,
            StartWorkflowTools = false,
            SubAgents = true,
            Collaboration = false,
        };

    /// <summary>Resolves the capabilities <paramref name="mode" /> asks for.</summary>
    public static ModeCapabilities Resolve(ChatMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        return Resolve(mode.EnabledCapabilityTools);
    }

    /// <summary>Resolves the capabilities declared by a raw <c>EnabledCapabilityTools</c> list.</summary>
    public static ModeCapabilities Resolve(IReadOnlyList<string>? capabilityTools)
    {
        var selection = ModeToolSelection.Parse(capabilityTools);
        if (selection.IsLegacy)
        {
            return LegacyDefaults;
        }

        var needsSandbox = selection.IsEnabled(ToolGroups.Sandbox);
        return new ModeCapabilities
        {
            NeedsSandbox = needsSandbox,
            // Only meaningful when a sandbox is needed; keep it null otherwise so a caller cannot
            // accidentally read an empty allow-list as "connect and expose nothing".
            SandboxToolAllowList = needsSandbox ? selection.AllowListFor(ToolGroups.Sandbox) : null,
            WorkflowAuthoringTools = selection.AnySelected(
                ToolGroups.Workflow,
                WorkflowToolProvider.AllToolNames
            ),
            StartWorkflowTools = selection.AnySelected(
                ToolGroups.Workflow,
                StartWorkflowToolProvider.ToolNames
            ),
            SubAgents = selection.IsEnabled(ToolGroups.SubAgents),
            Collaboration = selection.AnySelected(ToolGroups.SubAgents, CollaborationToolNames),
        };
    }
}
