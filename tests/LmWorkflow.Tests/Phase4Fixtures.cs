using System.Globalization;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Shared fixtures for the Phase 4a tests (forEach fan-out, background correlation, join surfacing,
///     validation retries). The templates use sentinel placeholders (<c>__JOIN__</c>/<c>__RETRIES__</c>)
///     rather than raw-string interpolation so the literal <c>{{item}}</c>/<c>{{index}}</c>/<c>{{count}}</c>
///     binding tokens survive verbatim.
/// </summary>
internal static class Phase4Fixtures
{
    /// <summary>Marker proving the shared context is prepended to a fan-out unit's composed prompt.</summary>
    public const string SharedContextMarker = "SHARED_CTX: fan-out pipeline.";

    private const string ForEachTemplate = """
        {
          "schemaVersion": 1,
          "objective": "Fan out over the items.",
          "sharedContext": "SHARED_CTX: fan-out pipeline.",
          "inputs": { "items": ["alpha", "beta", "gamma"] },
          "state": {},
          "maxStepBudget": 50,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["fan"] },
            {
              "id": "fan",
              "type": "procedural",
              "title": "Fan out",
              "tasksMode": "authored",
              "joinPolicy": { "mode": "__JOIN__" },
              "onFailure": "fail",
              "taskList": [
                {
                  "id": "task",
                  "delegate": "agent",
                  "subagent_type": "general-purpose",
                  "forEach": "inputs.items",
                  "promptTemplate": "Process {{item}} at {{index}} of {{count}}.",
                  "outputSchema": {
                    "type": "object",
                    "required": ["text"],
                    "properties": { "text": { "type": "string" } }
                  },
                  "writes": { "from": "text", "to": "state.results", "mode": "append" },
                  "onFailure": "fail",
                  "maxValidationRetries": __RETRIES__
                }
              ],
              "next": ["done"]
            },
            { "id": "done", "type": "terminal", "title": "Done" },
            { "id": "fail", "type": "terminal", "title": "Failed" }
          ]
        }
        """;

    private const string SingleTaskTemplate = """
        {
          "schemaVersion": 1,
          "objective": "Analyze the topic.",
          "sharedContext": "SHARED_CTX: fan-out pipeline.",
          "inputs": { "topic": "widgets" },
          "state": {},
          "maxStepBudget": 50,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["analyze"] },
            {
              "id": "analyze",
              "type": "procedural",
              "title": "Analyze",
              "tasksMode": "authored",
              "joinPolicy": { "mode": "all" },
              "onFailure": "fail",
              "taskList": [
                {
                  "id": "task",
                  "delegate": "agent",
                  "subagent_type": "general-purpose",
                  "promptTemplate": "Analyze {{inputs.topic}}.",
                  "outputSchema": {
                    "type": "object",
                    "required": ["summary"],
                    "properties": { "summary": { "type": "string" } }
                  },
                  "writes": { "to": "state.analysis", "mode": "set" },
                  "onFailure": "fail",
                  "maxValidationRetries": __RETRIES__
                }
              ],
              "next": ["done"]
            },
            { "id": "done", "type": "terminal", "title": "Done" },
            { "id": "fail", "type": "terminal", "title": "Failed" }
          ]
        }
        """;

    private const string EmptyForEachTemplate = """
        {
          "schemaVersion": 1,
          "objective": "Fan out over an empty set.",
          "sharedContext": "SHARED_CTX: fan-out pipeline.",
          "inputs": { "items": [] },
          "state": {},
          "maxStepBudget": 50,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["fan"] },
            {
              "id": "fan",
              "type": "procedural",
              "title": "Fan out",
              "tasksMode": "authored",
              "joinPolicy": { "mode": "all" },
              "onFailure": "fail",
              "taskList": [
                {
                  "id": "task",
                  "delegate": "agent",
                  "subagent_type": "general-purpose",
                  "forEach": "inputs.items",
                  "promptTemplate": "Process {{item}}.",
                  "outputSchema": {
                    "type": "object",
                    "required": ["text"],
                    "properties": { "text": { "type": "string" } }
                  },
                  "onFailure": "fail",
                  "maxValidationRetries": 0
                }
              ],
              "next": ["done"]
            },
            { "id": "done", "type": "terminal", "title": "Done" },
            { "id": "fail", "type": "terminal", "title": "Failed" }
          ]
        }
        """;

    /// <summary>A <c>start → fan(forEach inputs.items[3]) → done</c> workflow with a configurable join + retry budget.</summary>
    public static string ForEachWorkflow(string joinMode = "all", int maxValidationRetries = 0) =>
        ForEachTemplate
            .Replace("__JOIN__", joinMode)
            .Replace("__RETRIES__", maxValidationRetries.ToString(CultureInfo.InvariantCulture));

    /// <summary>A <c>start → fan(forEach over an EMPTY array) → done</c> workflow: the node has nothing to spawn.</summary>
    public static string EmptyForEachWorkflow() => EmptyForEachTemplate;

    /// <summary>A single-task <c>start → analyze → done</c> workflow (with a <c>fail</c> onFailure terminal) and a configurable retry budget.</summary>
    public static string SingleTask(int maxValidationRetries) =>
        SingleTaskTemplate.Replace(
            "__RETRIES__",
            maxValidationRetries.ToString(CultureInfo.InvariantCulture)
        );
}
