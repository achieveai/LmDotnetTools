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
                    + "and at least one has kind 'end'. Concurrency lives in the GRAPH, not in a prompt: when a "
                    + "step's work is really several independent checks, express it as ONE 'parallel' step whose "
                    + "'agents' each run a check — do NOT write one 'agent' step that tells its sub-agent to "
                    + "'dispatch'/'spawn'/'delegate to' other agents (a step's sub-agent cannot spawn further "
                    + "sub-agents, so that instruction silently collapses to one agent doing everything alone). "
                    + "Gather shared context once (an early 'agent' step with 'saveAs') and pass it to each lane "
                    + "via {{state.<saveAs>}} instead of re-deriving it per step. See the worked example in the "
                    + "system prompt."
            )
            .WithProperty("objective", JsonSchemaObject.String("The high-level objective the workflow pursues."), required: true)
            .WithProperty("steps", JsonSchemaObject.Array(Step(), "The workflow steps."), required: true)
            .AllowAdditionalProperties(false)
            .Build();

    /// <summary>Schema for one uniform step. Which optional fields apply depends on <c>kind</c>.</summary>
    public static JsonSchemaObject Step() =>
        JsonSchemaObject
            .Create("object")
            .WithDescription(
                "One workflow step. The fields used depend on 'kind'. Only 'id' and 'kind' are always "
                    + "required; per kind, an agent/parallel step needs a 'prompt' (agents[].prompt for "
                    + "parallel) and a branch step needs at least one 'branches' entry. Every other field is "
                    + "genuinely optional and defaults sensibly — see each field's description."
            )
            .WithProperty("id", JsonSchemaObject.String("Unique step id."), required: true)
            .WithProperty(
                "kind",
                new JsonSchemaObject
                {
                    Type = new("string"),
                    Description =
                        "The step kind. Use 'parallel' (NOT several 'agent' steps, and NOT one 'agent' step "
                        + "instructed to run others) whenever multiple independent sub-agents should work at "
                        + "once — it is the only kind that fans work out concurrently.",
                    Enum = ["start", "agent", "parallel", "branch", "noop", "end"],
                },
                required: true
            )
            .WithProperty("title", JsonSchemaObject.String("Human-readable title. Defaults to 'id' if omitted."))
            .WithProperty(
                "next",
                JsonSchemaObject.String(
                    "start/agent/parallel: the next step id. May point BACK to an earlier step to form a "
                        + "loop. OPTIONAL — omit it to simply continue with the next step you declared, or "
                        + "omit it on the last step to end the workflow."
                )
            )
            .WithProperty(
                "agent",
                JsonSchemaObject.String(
                    "agent steps: the sub-agent type to delegate to. OPTIONAL — defaults to 'general-purpose'. "
                        + "Name a specific agent whenever one fits the task; leave it out only when any "
                        + "capable agent will do."
                )
            )
            .WithProperty(
                "prompt",
                JsonSchemaObject.String(
                    "agent steps: the prompt for the sub-agent. Use {{item}} inside a forEach step; reference "
                        + "an earlier step's saved output with {{state.<saveAs>}}. Write it as work for ONE "
                        + "sub-agent — do NOT tell it to dispatch/spawn/delegate to other agents (it cannot); to "
                        + "run several agents at once, use a 'parallel' step instead."
                )
            )
            .WithProperty(
                "modelIntelligence",
                JsonSchemaObject.Integer(
                    "agent steps (optional): capability tier forwarded to Agent.modelIntelligence. Higher values "
                        + "request a more capable configured model; omit to keep the selected agent's default."
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
                JsonSchemaObject.Array(Agent(), "parallel steps: the sub-agents to run concurrently, each its own lane; the step joins when all finish. This is how you fan work out to several specialists at once — give each lane a distinct 'agent'/'prompt' (and 'saveAs' to capture its result).")
            )
            .WithProperty("branches", JsonSchemaObject.Array(Branch(), "branch steps: ordered conditions; the first that holds wins."))
            .WithProperty(
                "else",
                JsonSchemaObject.String(
                    "branch steps: the fallback step id when no branch holds. OPTIONAL — follows the same "
                        + "fall-through rule as 'next'."
                )
            )
            .WithProperty("maxVisits", JsonSchemaObject.Integer("optional loop cap: the maximum times this step may be entered."))
            .WithProperty("onMaxVisits", JsonSchemaObject.String("optional loop escape: the step id to go to once maxVisits is exceeded."))
            .AllowAdditionalProperties(false)
            .Build();

    private static JsonSchemaObject Agent() =>
        JsonSchemaObject
            .Create("object")
            .WithProperty(
                "agent",
                JsonSchemaObject.String(
                    "The sub-agent type to delegate to. OPTIONAL — defaults to 'general-purpose'."
                )
            )
            .WithProperty("prompt", JsonSchemaObject.String("The prompt for the sub-agent."), required: true)
            .WithProperty(
                "modelIntelligence",
                JsonSchemaObject.Integer(
                    "optional capability tier forwarded to Agent.modelIntelligence; omit to keep the agent default."
                )
            )
            .WithProperty("saveAs", JsonSchemaObject.String("optional: capture this agent's output into state.<saveAs>."))
            .AllowAdditionalProperties(false)
            .Build();

    private static JsonSchemaObject Branch() =>
        JsonSchemaObject
            .Create("object")
            .WithProperty("when", JsonSchemaObject.String("The (prose) condition that selects this branch."), required: true)
            .WithProperty("goto", JsonSchemaObject.String("The step id to go to when 'when' holds."), required: true)
            .AllowAdditionalProperties(false)
            .Build();
}
