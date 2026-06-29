namespace AchieveAi.LmDotnetTools.LmWorkflow.Model;

/// <summary>
///     The kind of a workflow node. V1 supports only <see cref="Start"/>, <see cref="Procedural"/>,
///     <see cref="Conditional"/> and <see cref="Terminal"/>; an out-of-V1 discriminator deserializes to an
///     <see cref="Unknown"/> placeholder that the validator rejects with a "not supported in V1" error.
/// </summary>
public enum NodeType
{
    /// <summary>The single entry point of the workflow.</summary>
    Start,

    /// <summary>A node that runs an authored list of tasks (delegated to sub-agents).</summary>
    Procedural,

    /// <summary>A node that branches to a target based on structured/prose conditions.</summary>
    Conditional,

    /// <summary>A node that ends the workflow and shapes the final output.</summary>
    Terminal,

    /// <summary>An unrecognized (out-of-V1) node kind; carries the raw discriminator for validator reporting.</summary>
    Unknown,
}

/// <summary>
///     How a procedural node sources its task list. V1 supports only <see cref="Authored"/>; the
///     <see cref="Runtime"/> and <see cref="Hybrid"/> members exist for forward compatibility and are
///     rejected by the validator.
/// </summary>
public enum TasksMode
{
    /// <summary>Tasks are authored statically in the workflow definition.</summary>
    Authored,

    /// <summary>Tasks are produced at runtime (not supported in V1).</summary>
    Runtime,

    /// <summary>A mix of authored and runtime tasks (not supported in V1).</summary>
    Hybrid,
}

/// <summary>
///     How a procedural node joins the results of its tasks. V1 supports <see cref="All"/> and
///     <see cref="Any"/>; <see cref="Quorum"/> exists for forward compatibility and is rejected by the
///     validator.
/// </summary>
public enum JoinMode
{
    /// <summary>Wait for all tasks to complete.</summary>
    All,

    /// <summary>Complete as soon as any task completes.</summary>
    Any,

    /// <summary>Complete once a threshold fraction of tasks complete (not supported in V1).</summary>
    Quorum,
}

/// <summary>
///     How a task write merges into workflow state. V1 supports <see cref="Set"/>, <see cref="Append"/>
///     and <see cref="Merge"/>; <see cref="Upsert"/> exists for forward compatibility and is rejected
///     by the validator.
/// </summary>
public enum WriteMode
{
    /// <summary>Replace the target with the written value.</summary>
    Set,

    /// <summary>Append the written value to a target array.</summary>
    Append,

    /// <summary>Shallow-merge the written value into a target object.</summary>
    Merge,

    /// <summary>Insert-or-update keyed by a property (not supported in V1).</summary>
    Upsert,
}

/// <summary>
///     The comparison operator of a structured <see cref="Condition"/> leaf. This is a closed set; an
///     unrecognized operator string in the JSON is preserved by the converter and rejected by the
///     validator as an "unknown condition op" error.
/// </summary>
public enum ConditionOp
{
    /// <summary>Equal.</summary>
    Eq,

    /// <summary>Not equal.</summary>
    Ne,

    /// <summary>Less than.</summary>
    Lt,

    /// <summary>Less than or equal.</summary>
    Lte,

    /// <summary>Greater than.</summary>
    Gt,

    /// <summary>Greater than or equal.</summary>
    Gte,

    /// <summary>Membership test (value in collection).</summary>
    In,

    /// <summary>The referenced path is empty / absent.</summary>
    Empty,

    /// <summary>The referenced path is non-empty / present.</summary>
    NonEmpty,
}

/// <summary>
///     The delegate target of a task. V1 supports only <see cref="Agent"/>; <see cref="Workflow"/> and
///     <see cref="Human"/> exist for forward compatibility and are rejected by the validator.
/// </summary>
public enum DelegateKind
{
    /// <summary>Delegate the task to a specialized sub-agent.</summary>
    Agent,

    /// <summary>Delegate the task to a nested workflow (not supported in V1).</summary>
    Workflow,

    /// <summary>Delegate the task to a human (not supported in V1).</summary>
    Human,
}
