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

    /// <summary>
    ///     Which workflow tools to expose, across BOTH the authoring and launch families: <c>null</c>
    ///     means every tool in the group. A non-null set is an explicit allow-list.
    /// </summary>
    /// <remarks>
    ///     Kept alongside the two booleans rather than replacing them: the booleans decide whether the
    ///     backing runtime and manager are stood up at all, and this decides which of that provider's
    ///     tools the model may see. Without it the editor's per-tool checkboxes would be a lie — ticking
    ///     one authoring tool would grant all seven.
    /// </remarks>
    public required IReadOnlySet<string>? WorkflowToolAllowList { get; init; }

    /// <summary>Whether sub-agent delegation (<c>Agent</c>, <c>SendMessage</c>, …) is offered.</summary>
    public required bool SubAgents { get; init; }

    /// <summary>
    ///     Which sub-agent tools to expose: <c>null</c> means the whole surface the loop would emit for
    ///     this mode's collaboration shape. A non-null set is an explicit allow-list applied on top of
    ///     that shape.
    /// </summary>
    /// <remarks>
    ///     The shape (legacy four vs collaboration seven) is still chosen by
    ///     <see cref="Collaboration" />; this only narrows within it. Filtering cannot widen a shape,
    ///     so selecting a collaboration-only name is what turns collaboration on, and the allow-list
    ///     then keeps exactly the names selected.
    /// </remarks>
    public required IReadOnlySet<string>? SubAgentToolAllowList { get; init; }

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
            WorkflowToolAllowList = null,
            SubAgents = true,
            // Null, not the legacy four by name: a legacy mode gets whatever surface the loop emits,
            // exactly as it did before capability selection existed.
            SubAgentToolAllowList = null,
            Collaboration = false,
        };

    /// <summary>
    ///     Value equality over the allow-list SETS, not over their references.
    /// </summary>
    /// <remarks>
    ///     A <c>record</c> advertises value equality, but its synthesized comparison uses
    ///     <see cref="object.Equals(object)" /> on each member — reference equality for a set. Two
    ///     resolutions of the SAME selection build distinct <c>HashSet</c> instances, so the
    ///     synthesized version reports a mode and its own copy as different capabilities. That is
    ///     precisely the question this type exists to answer, so the comparison has to be spelled out
    ///     rather than inherited.
    /// </remarks>
    public bool Equals(ModeCapabilities? other) =>
        other is not null
        && NeedsSandbox == other.NeedsSandbox
        && WorkflowAuthoringTools == other.WorkflowAuthoringTools
        && StartWorkflowTools == other.StartWorkflowTools
        && SubAgents == other.SubAgents
        && Collaboration == other.Collaboration
        && SameSet(SandboxToolAllowList, other.SandboxToolAllowList)
        && SameSet(WorkflowToolAllowList, other.WorkflowToolAllowList)
        && SameSet(SubAgentToolAllowList, other.SubAgentToolAllowList);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(NeedsSandbox);
        hash.Add(WorkflowAuthoringTools);
        hash.Add(StartWorkflowTools);
        hash.Add(SubAgents);
        hash.Add(Collaboration);
        AddSet(ref hash, SandboxToolAllowList);
        AddSet(ref hash, WorkflowToolAllowList);
        AddSet(ref hash, SubAgentToolAllowList);
        return hash.ToHashCode();
    }

    /// <summary>Null-aware, order-independent set comparison. Null and empty stay DISTINCT.</summary>
    private static bool SameSet(IReadOnlySet<string>? left, IReadOnlySet<string>? right) =>
        left is null
            ? right is null
            : right is not null && left.Count == right.Count && left.All(right.Contains);

    /// <summary>Order-independent set hash, so equal sets built in any order agree.</summary>
    private static void AddSet(ref HashCode hash, IReadOnlySet<string>? set)
    {
        if (set is null)
        {
            hash.Add(0);
            return;
        }

        var combined = 0;
        foreach (var name in set)
        {
            combined ^= StringComparer.Ordinal.GetHashCode(name);
        }

        hash.Add(set.Count);
        hash.Add(combined);
    }

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
        var needsSubAgents = selection.IsEnabled(ToolGroups.SubAgents);
        var workflowAuthoring = selection.AnySelected(
            ToolGroups.Workflow,
            WorkflowToolProvider.AllToolNames
        );
        var startWorkflow = selection.AnySelected(
            ToolGroups.Workflow,
            StartWorkflowToolProvider.ToolNames
        );

        return new ModeCapabilities
        {
            NeedsSandbox = needsSandbox,
            // Only meaningful when a sandbox is needed; keep it null otherwise so a caller cannot
            // accidentally read an empty allow-list as "connect and expose nothing".
            SandboxToolAllowList = needsSandbox ? selection.AllowListFor(ToolGroups.Sandbox) : null,
            WorkflowAuthoringTools = workflowAuthoring,
            StartWorkflowTools = startWorkflow,
            // Same null-means-everything contract as the sandbox allow-list, and null whenever no
            // workflow tool is wanted at all so a caller cannot read an empty set as "expose nothing
            // from a provider that was never registered".
            WorkflowToolAllowList = workflowAuthoring || startWorkflow
                ? selection.AllowListFor(ToolGroups.Workflow)
                : null,
            SubAgents = needsSubAgents,
            SubAgentToolAllowList = needsSubAgents
                ? selection.AllowListFor(ToolGroups.SubAgents)
                : null,
            Collaboration = selection.AnySelected(ToolGroups.SubAgents, CollaborationToolNames),
        };
    }
}
