using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Model;

/// <summary>
///     The machine-readable schema advertised to the model for the flat <see cref="SimpleWorkflow"/> authoring
///     DSL. Unlike the internal <see cref="WorkflowDefinition"/> (a polymorphic node union the schema
///     generator can't express), this surface is flat and uniform, so a single hand-authored schema fully
///     describes it and cannot drift by node type. Shared by every workflow tool that takes a workflow or a
///     single step (SetWorkflow / StartWorkflowAgent / AddNode) so they describe the DSL identically.
/// </summary>
public static class SimpleWorkflowSchema
{
    /// <summary>Schema for a whole workflow: an objective plus a flat list of steps.</summary>
    public static JsonSchemaObject Workflow() =>
        JsonSchemaObject
            .Create("object")
            .WithDescription(
                "A workflow: an 'objective' plus a flat list of 'steps'. Exactly one step has kind 'start' "
                    + "and at least one has kind 'end'. See the worked example in the system prompt."
            )
            .WithProperty("objective", JsonSchemaObject.String("The high-level objective the workflow pursues."), required: true)
            .WithProperty("steps", JsonSchemaObject.Array(Step(), "The workflow steps."), required: true)
            .AllowAdditionalProperties(true)
            .Build();

    /// <summary>Schema for one uniform step. Which optional fields apply depends on <c>kind</c>.</summary>
    public static JsonSchemaObject Step() =>
        JsonSchemaObject
            .Create("object")
            .WithDescription("One workflow step. The fields used depend on 'kind'.")
            .WithProperty("id", JsonSchemaObject.String("Unique step id."), required: true)
            .WithProperty(
                "kind",
                new JsonSchemaObject
                {
                    Type = new("string"),
                    Description = "The step kind.",
                    Enum = ["start", "agent", "parallel", "branch", "end"],
                },
                required: true
            )
            .WithProperty("title", JsonSchemaObject.String("Human-readable title. Defaults to 'id' if omitted."))
            .WithProperty(
                "next",
                JsonSchemaObject.String(
                    "start/agent/parallel: the next step id. May point BACK to an earlier step to form a loop."
                )
            )
            .WithProperty("agent", JsonSchemaObject.String("agent steps: the sub-agent type to delegate to."))
            .WithProperty(
                "prompt",
                JsonSchemaObject.String(
                    "agent steps: the prompt for the sub-agent. Use {{item}} inside a forEach step; reference "
                        + "an earlier step's saved output with {{state.<saveAs>}}."
                )
            )
            .WithProperty(
                "forEach",
                JsonSchemaObject.String(
                    "agent steps (optional): fan the SAME agent out over each element of a state array, e.g. "
                        + "'state.files' — runs SEQUENTIALLY in V1. For concurrent DIFFERENT agents, use kind 'parallel'."
                )
            )
            .WithProperty(
                "saveAs",
                JsonSchemaObject.String(
                    "optional: capture the agent's output. A plain agent SETs state.<saveAs>; a forEach step "
                        + "APPENDS each element's output into the state.<saveAs> array."
                )
            )
            .WithProperty(
                "agents",
                JsonSchemaObject.Array(Agent(), "parallel steps: the sub-agents to run concurrently; the step joins when all finish.")
            )
            .WithProperty("branches", JsonSchemaObject.Array(Branch(), "branch steps: ordered conditions; the first that holds wins."))
            .WithProperty("else", JsonSchemaObject.String("branch steps: the fallback step id when no branch holds."))
            .WithProperty("maxVisits", JsonSchemaObject.Integer("optional loop cap: the maximum times this step may be entered."))
            .WithProperty("onMaxVisits", JsonSchemaObject.String("optional loop escape: the step id to go to once maxVisits is exceeded."))
            .AllowAdditionalProperties(true)
            .Build();

    private static JsonSchemaObject Agent() =>
        JsonSchemaObject
            .Create("object")
            .WithProperty("agent", JsonSchemaObject.String("The sub-agent type to delegate to."), required: true)
            .WithProperty("prompt", JsonSchemaObject.String("The prompt for the sub-agent."), required: true)
            .WithProperty("saveAs", JsonSchemaObject.String("optional: capture this agent's output into state.<saveAs>."))
            .AllowAdditionalProperties(true)
            .Build();

    private static JsonSchemaObject Branch() =>
        JsonSchemaObject
            .Create("object")
            .WithProperty("when", JsonSchemaObject.String("The (prose) condition that selects this branch."), required: true)
            .WithProperty("goto", JsonSchemaObject.String("The step id to go to when 'when' holds."), required: true)
            .AllowAdditionalProperties(true)
            .Build();
}
