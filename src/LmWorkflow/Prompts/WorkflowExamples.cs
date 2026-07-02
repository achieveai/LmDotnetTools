namespace AchieveAi.LmDotnetTools.LmWorkflow.Prompts;

/// <summary>
///     Canonical, copy-pasteable workflow-definition examples shown to the controller LLM. Kept as a
///     single source of truth so the JSON the model is taught with is the exact JSON the tests prove
///     authors cleanly (see <c>WorkflowAuthoringErgonomicsTests</c>) — the example can never drift from a
///     working definition.
/// </summary>
public static class WorkflowExamples
{
    /// <summary>
    ///     The smallest useful workflow: a start node routing into one procedural node that runs a single
    ///     sub-agent task, then a terminal. It deliberately shows the exact field names the runtime
    ///     requires — <c>taskList</c>, and within a task <c>subagent_type</c> (snake_case) and
    ///     <c>promptTemplate</c> — because these are the ones an LLM most often guesses wrong.
    /// </summary>
    public const string MinimalProcedural = """
        {
          "schemaVersion": 1,
          "objective": "Research a topic and write a short brief.",
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["work"] },
            {
              "id": "work",
              "type": "procedural",
              "title": "Research the topic",
              "joinPolicy": { "mode": "all" },
              "taskList": [
                {
                  "id": "research",
                  "subagent_type": "general-purpose",
                  "promptTemplate": "Research {{objective}} and return a concise brief.",
                  "writes": { "to": "state.brief", "mode": "set" }
                }
              ],
              "next": ["done"]
            },
            { "id": "done", "type": "terminal", "title": "Done" }
          ]
        }
        """;
}
