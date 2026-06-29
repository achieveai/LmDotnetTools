namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Shared fixtures for the Phase 3 runtime/session tests: a minimal linear workflow
///     (<c>start → procedural(single authored agent task) → terminal</c>) plus the marker strings the
///     tests assert on to prove prompt composition.
/// </summary>
internal static class Phase3Fixtures
{
    /// <summary>Marker proving the shared context is prepended to the composed prompt.</summary>
    public const string SharedContextMarker = "SHARED_CTX: you are part of the analysis pipeline.";

    /// <summary>Marker proving node-level controllerInstructions never leak into the composed prompt.</summary>
    public const string ControllerInstructionsMarker = "CTRL_ONLY: never leak this to the agent.";

    /// <summary>The schema-return directive the runtime appends when the task declares an output schema.</summary>
    public const string SchemaDirective = "Return ONLY a JSON object that conforms to this schema:";

    /// <summary>
    ///     A linear workflow: <c>start → analyze(one authored general-purpose agent task) → done</c>. The
    ///     task binds <c>{{inputs.topic}}</c>, validates against a <c>{summary}</c> output schema, and writes
    ///     to <c>state.analysis</c>; the terminal shapes a <c>{summary}</c> final output.
    /// </summary>
    public const string LinearBlockingAgent = """
        {
          "schemaVersion": 1,
          "objective": "Analyze the topic and finish.",
          "sharedContext": "SHARED_CTX: you are part of the analysis pipeline.",
          "inputs": { "topic": "widgets" },
          "state": {},
          "maxStepBudget": 50,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["analyze"] },
            {
              "id": "analyze",
              "type": "procedural",
              "title": "Analyze",
              "controllerInstructions": "CTRL_ONLY: never leak this to the agent.",
              "tasksMode": "authored",
              "joinPolicy": { "mode": "all" },
              "taskList": [
                {
                  "id": "task",
                  "delegate": "agent",
                  "subagent_type": "general-purpose",
                  "promptTemplate": "Analyze the topic {{inputs.topic}}.",
                  "outputSchema": {
                    "type": "object",
                    "required": ["summary"],
                    "properties": { "summary": { "type": "string" } }
                  },
                  "writes": { "to": "state.analysis", "mode": "set" }
                }
              ],
              "next": ["done"]
            },
            {
              "id": "done",
              "type": "terminal",
              "title": "Done",
              "finalOutputSchema": {
                "type": "object",
                "required": ["summary"],
                "properties": { "summary": { "type": "string" } }
              }
            }
          ]
        }
        """;
}
