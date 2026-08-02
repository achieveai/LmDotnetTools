using System.Text;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Collaboration;

/// <summary>
///     Thrown when a workflow controller cannot be admitted to the launching caller's collaboration. The
///     <see cref="FailureCode"/> is one of <see cref="SubAgentCollaborationFailureCodes"/> or
///     <see cref="WorkflowCollaboration.NestedWorkflowFailureCode"/>, so a tool handler can map the refusal
///     to a stable, machine-readable error without parsing the message.
/// </summary>
public sealed class WorkflowCollaborationException(string failureCode, string message)
    : InvalidOperationException(message)
{
    /// <summary>The stable refusal code.</summary>
    public string FailureCode { get; } = failureCode;
}

/// <summary>
///     Admits a workflow controller into the launching caller's hierarchy-wide collaboration (issue #244) and
///     owns everything that admission implies: the root-wide capacity lease, the directory registration with
///     the controller's read/write endpoints, and the single release at teardown.
/// </summary>
/// <remarks>
///     <para>
///         <b>The controller is a zero-cost hop.</b> It is a genuine, visible structural node — one level
///         deeper in the tree than its caller — but it does NOT consume delegation budget, so it sits at the
///         caller's own delegation depth and the delegates it spawns land at depth + 1, exactly where an
///         ordinary sub-agent spawned by the caller would have. The arithmetic itself lives in
///         <see cref="AgentCollaborationContext.CreateChild"/>; this type only names the kind.
///     </para>
///     <para>
///         <b>LmWorkflow adapts, LmMultiTurn defines.</b> The contracts (directory, context, endpoints,
///         persisted node record) all belong to <c>LmMultiTurn</c>; nothing here is a second representation of
///         them, and <c>LmMultiTurn</c> never references <c>LmWorkflow</c>.
///     </para>
/// </remarks>
public static class WorkflowCollaboration
{
    /// <summary>The fixed, trusted role every workflow controller is registered under.</summary>
    public const string ControllerRole = "workflow-controller";

    /// <summary>The agent type recorded for a controller node, so a roster can group workflow runs.</summary>
    public const string ControllerAgentType = "workflow";

    /// <summary>Refusal code for an attempt to launch a workflow from inside a workflow controller.</summary>
    public const string NestedWorkflowFailureCode = "nested_workflow";

    private const string AgentIdPrefix = "wfctl-";
    private const string NamePrefix = "workflow-";

    /// <summary>The deterministic collaboration id of the controller for <paramref name="workflowId"/>.</summary>
    /// <remarks>
    ///     Deterministic so a resumed run reacquires capacity under the SAME identity it was persisted with,
    ///     rather than appearing as a second, unrelated node.
    /// </remarks>
    public static string ComposeControllerAgentId(string workflowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        return AgentIdPrefix + workflowId;
    }

    /// <summary>
    ///     Admits a controller into <paramref name="caller"/>'s collaboration, or returns <c>null</c> when the
    ///     caller supplied no collaboration at all (absence of a setup is the feature gate — a host that never
    ///     opts in keeps today's behaviour exactly).
    /// </summary>
    /// <param name="caller">The launching agent's own handle, or null when collaboration is off.</param>
    /// <param name="workflowId">The opaque workflow handle the controller's identity is derived from.</param>
    /// <param name="definition">The workflow being run; its objective is the source of the trusted description.</param>
    /// <param name="threadId">The controller loop's persistence thread id (the transcript read key).</param>
    /// <param name="conversationStore">The store the controller's own turns are persisted to, if any.</param>
    /// <param name="isComplete">Reads whether the run has reached a terminal node, for the live status facet.</param>
    /// <param name="persisted">
    ///     The node record captured when the run was first admitted, when this is a RESUME. Its role and
    ///     description are reused verbatim — trusted metadata is validated once, at the original spawn, and a
    ///     restart must not become an opportunity to re-derive it from anything the model can influence.
    /// </param>
    /// <exception cref="WorkflowCollaborationException">
    ///     The launcher is itself a workflow controller (nested workflows are prohibited), the root-wide agent
    ///     cap cannot admit the controller, or the directory refused the registration. Thrown BEFORE any loop
    ///     is built, so a refused run never starts.
    /// </exception>
    internal static WorkflowControllerRegistration? TryAdmitController(
        AgentCollaborationSetup? caller,
        string workflowId,
        WorkflowDefinition? definition,
        string threadId,
        IConversationStore? conversationStore,
        Func<bool> isComplete,
        CollaborationNodeRecord? persisted = null
    )
    {
        if (caller is null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        ArgumentNullException.ThrowIfNull(isComplete);

        // A workflow controller may never launch another workflow. The launch tools are already excluded
        // structurally from everything below a controller (NonInheritedToolNames), so reaching here means a
        // host wired them back in by hand; refuse loudly rather than build a nested run.
        if (caller.Context.Kind == AgentKind.WorkflowController)
        {
            throw new WorkflowCollaborationException(
                NestedWorkflowFailureCode,
                "A workflow controller cannot launch another workflow."
            );
        }

        var role = persisted?.Role ?? ControllerRole;
        var description = persisted?.Description ?? DeriveDescription(definition, workflowId);
        if (!AgentCollaborationContext.IsMetadataValid(role, description))
        {
            throw new WorkflowCollaborationException(
                SubAgentCollaborationFailureCodes.InvalidMetadata,
                "The workflow controller's collaboration role/description could not be derived."
            );
        }

        var agentId = ComposeControllerAgentId(workflowId);
        var name = NamePrefix + workflowId;
        var context = caller.Context.CreateChild(agentId, AgentKind.WorkflowController, role, description);

        // Capacity first, and non-blockingly: the root-wide lease is what makes a refused run fail visibly
        // before any loop, gate, or conversation thread is touched. Mirrors SubAgentManager's admission order.
        var lease =
            caller.Directory.TryAcquireCapacity(agentId)
            ?? throw new WorkflowCollaborationException(
                SubAgentCollaborationFailureCodes.CapacityExhausted,
                "The collaboration's agent limit is reached; the workflow controller cannot be admitted."
            );

        var endpoint = new WorkflowControllerEndpoint(
            () => isComplete() ? AgentCollaborationStatuses.Completed : AgentCollaborationStatuses.Running,
            threadId,
            conversationStore
        );

        var registration = caller.Directory.TryRegister(
            context,
            name,
            AgentCollaborationStatuses.Running,
            writeEndpoint: endpoint,
            readEndpoint: endpoint,
            agentType: ControllerAgentType
        );

        if (registration.Entry is not { } entry)
        {
            _ = lease.Release();
            throw new WorkflowCollaborationException(
                registration.FailureCode ?? SubAgentCollaborationFailureCodes.RegistrationFailed,
                "The workflow controller could not be registered in the collaboration directory."
            );
        }

        return new WorkflowControllerRegistration(
            caller.ForChild(context, name),
            CollaborationNodeRecord.FromEntry(entry),
            endpoint,
            lease
        );
    }

    /// <summary>
    ///     Derives the controller's trusted description from the workflow's own objective label, falling back
    ///     to the workflow handle when there is no objective. Truncated on a Unicode scalar boundary so a long
    ///     objective is shortened rather than rejected.
    /// </summary>
    private static string DeriveDescription(WorkflowDefinition? definition, string workflowId)
    {
        var objective = definition?.Objective;
        return string.IsNullOrWhiteSpace(objective)
            ? Truncate(
                $"Workflow controller for '{workflowId}'.",
                AgentCollaborationContext.MaxDescriptionLength
            )
            : Truncate(objective.Trim(), AgentCollaborationContext.MaxDescriptionLength);
    }

    /// <summary>
    ///     Derives a workflow delegate's trusted role from the authored task's own label, falling back to its
    ///     id when the author supplied none. Both come from the workflow definition, so a controller model
    ///     cannot relabel a delegate the directory then advertises to every other agent — the same guarantee a
    ///     role-fixed template gives an ordinary sub-agent.
    /// </summary>
    internal static string DeriveDelegateRole(WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var label = string.IsNullOrWhiteSpace(task.Label) ? task.Id : task.Label.Trim();
        return Truncate(label, AgentCollaborationContext.MaxRoleLength);
    }

    /// <summary>
    ///     Derives a workflow delegate's trusted description from the owning node's title and the task's own
    ///     label — the two labels the workflow author already writes — rather than introducing a parallel
    ///     description field for the controller to fill in.
    /// </summary>
    internal static string DeriveDelegateDescription(ProceduralNode node, WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(task);
        var label = string.IsNullOrWhiteSpace(task.Label) ? task.Id : task.Label.Trim();
        return Truncate(
            $"Workflow task '{label}' for node '{node.Title}'.",
            AgentCollaborationContext.MaxDescriptionLength
        );
    }

    /// <summary>Shortens <paramref name="value"/> to <paramref name="maxScalars"/> whole Unicode scalars.</summary>
    private static string Truncate(string value, int maxScalars)
    {
        var builder = new StringBuilder();
        var scalars = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (++scalars > maxScalars)
            {
                break;
            }

            _ = builder.Append(rune);
        }

        return builder.ToString();
    }
}

/// <summary>
///     One admitted workflow controller: its collaboration handle, the persisted node record describing it,
///     and the capacity lease it holds until the run is torn down.
/// </summary>
internal sealed class WorkflowControllerRegistration
{
    private readonly WorkflowControllerEndpoint _endpoint;
    private readonly AgentCapacityLease _lease;
    private int _finished;

    internal WorkflowControllerRegistration(
        AgentCollaborationSetup setup,
        CollaborationNodeRecord record,
        WorkflowControllerEndpoint endpoint,
        AgentCapacityLease lease
    )
    {
        Setup = setup;
        Record = record;
        _endpoint = endpoint;
        _lease = lease;
    }

    /// <summary>The controller's own handle onto the collaboration, handed to its loop.</summary>
    internal AgentCollaborationSetup Setup { get; }

    /// <summary>The persisted shape of this node, written into the workflow snapshot so a resume can reacquire.</summary>
    internal CollaborationNodeRecord Record { get; }

    /// <summary>Binds the controller loop, making the write endpoint live.</summary>
    internal void AttachLoop(IMultiTurnAgent loop) => _endpoint.Attach(loop);

    /// <summary>
    ///     Settles the node exactly once at teardown: records the terminal status, retains the entry so the
    ///     hierarchy stays inspectable after the run, unbinds the disposed loop, and returns the capacity
    ///     permit. Safe to call from more than one teardown path.
    /// </summary>
    /// <remarks>
    ///     Retention and capacity are independent: the retained entry is an inspectable record, not a live
    ///     routing target, and it costs the collaboration nothing. Callers must therefore invoke this as soon
    ///     as the loop is gone rather than behind any remaining teardown I/O.
    /// </remarks>
    internal void Finish(bool succeeded)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        _ = Setup.Directory.TryUpdateStatus(
            Setup.AgentId,
            succeeded ? AgentCollaborationStatuses.Completed : AgentCollaborationStatuses.Error
        );
        _ = Setup.Directory.TryMarkRetained(Setup.AgentId);
        _endpoint.Detach();
        _ = _lease.Release();
    }
}
