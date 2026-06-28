namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Shared JSON fixtures for the model and validator tests. Kept as raw JSON strings so they also
///     exercise the converters end to end.
/// </summary>
internal static class WorkflowFixtures
{
    /// <summary>
    ///     A representative, fully-valid V1 workflow:
    ///     start → procedural(forEach agent) → conditional(structured gate) → procedural → terminal.
    /// </summary>
    public const string ValidWorkflow = """
        {
          "schemaVersion": 1,
          "objective": "Summarize and route documents",
          "sharedContext": "You are part of a document pipeline.",
          "inputs": { "documents": [] },
          "state": { "summaries": [] },
          "maxStepBudget": 50,
          "onBudgetExhausted": "term_done",
          "$defs": {
            "Summary": {
              "type": "object",
              "properties": { "text": { "type": "string" } },
              "required": ["text"]
            }
          },
          "finalOutputSchema": { "$ref": "#/$defs/Summary" },
          "nodes": [
            {
              "id": "start",
              "type": "start",
              "title": "Start",
              "next": ["proc_summarize"]
            },
            {
              "id": "proc_summarize",
              "type": "procedural",
              "title": "Summarize each document",
              "tasksMode": "authored",
              "joinPolicy": { "mode": "all" },
              "maxParallel": 3,
              "onFailure": "term_failed",
              "taskList": [
                {
                  "id": "summarize",
                  "label": "Summarize document",
                  "delegate": "agent",
                  "subagent_type": "summarizer",
                  "forEach": "state.documents",
                  "parallel": true,
                  "promptTemplate": "Summarize: {{item}}",
                  "outputSchema": { "$ref": "#/$defs/Summary" },
                  "writes": { "from": "summary", "to": "state.summaries", "mode": "append" },
                  "onFailure": "term_failed",
                  "maxValidationRetries": 2
                }
              ],
              "next": ["gate"]
            },
            {
              "id": "gate",
              "type": "conditional",
              "title": "Route based on count",
              "branches": [
                {
                  "when": { "op": "gt", "path": "state.summaries.length", "value": 0 },
                  "to": "proc_finalize"
                }
              ],
              "else": "term_failed"
            },
            {
              "id": "proc_finalize",
              "type": "procedural",
              "title": "Finalize",
              "joinPolicy": { "mode": "all" },
              "taskList": [
                {
                  "id": "finalize",
                  "delegate": "agent",
                  "subagent_type": "finalizer",
                  "promptTemplate": "Finalize the summaries.",
                  "writes": { "to": "state.final", "mode": "set" },
                  "maxValidationRetries": 0
                }
              ],
              "next": ["term_done"]
            },
            {
              "id": "term_done",
              "type": "terminal",
              "title": "Done",
              "finalOutputSchema": { "$ref": "#/$defs/Summary" },
              "resultTemplate": { "result": "{{state.final}}" }
            },
            {
              "id": "term_failed",
              "type": "terminal",
              "title": "Failed"
            }
          ]
        }
        """;

    /// <summary>The smallest valid workflow: a start that flows directly into a terminal.</summary>
    public const string MinimalValid = """
        {
          "schemaVersion": 1,
          "objective": "trivial",
          "nodes": [
            { "id": "s", "type": "start", "title": "Start", "next": ["t"] },
            { "id": "t", "type": "terminal", "title": "Done" }
          ]
        }
        """;
}
