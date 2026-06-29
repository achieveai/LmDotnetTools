using System.Globalization;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Shared JSON fixtures for the Phase 4b tests (conditional recommended-branch, maxVisits/onMaxVisits
///     rail, maxStepBudget/onBudgetExhausted rail, and resultTemplate deep-render). Sentinel placeholders
///     (<c>__MAXVISITS__</c>/<c>__ONMAXVISITS__</c>/<c>__REQUIRED__</c>) are substituted by the helper
///     methods so the literal <c>{{state.x}}</c> binding tokens survive verbatim.
/// </summary>
internal static class Phase4bFixtures
{
    /// <summary>
    ///     A <c>start → gate(conditional) → {prose_target, high, low, zero}</c> workflow. The first branch
    ///     is prose (no structured form, skipped for the recommendation); the next two are structured
    ///     <c>gte</c> gates on <c>state.count</c>; the fallback is <c>zero</c>.
    /// </summary>
    public const string ConditionalRouting = """
        {
          "schemaVersion": 1,
          "objective": "Route on a count.",
          "state": { "count": 0 },
          "maxStepBudget": 50,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["gate"] },
            {
              "id": "gate",
              "type": "conditional",
              "title": "Gate",
              "branches": [
                { "when": "loosely when it feels right", "to": "prose_target" },
                { "when": { "op": "gte", "path": "state.count", "value": 5 }, "to": "high" },
                { "when": { "op": "gte", "path": "state.count", "value": 1 }, "to": "low" }
              ],
              "else": "zero"
            },
            { "id": "prose_target", "type": "terminal", "title": "Prose" },
            { "id": "high", "type": "terminal", "title": "High" },
            { "id": "low", "type": "terminal", "title": "Low" },
            { "id": "zero", "type": "terminal", "title": "Zero" }
          ]
        }
        """;

    private const string MaxVisitsTemplate = """
        {
          "schemaVersion": 1,
          "objective": "Loop through a gate with a visit ceiling.",
          "state": { "count": 0 },
          "maxStepBudget": 100,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["a"] },
            { "id": "a", "type": "procedural", "title": "A", "next": ["gate"] },
            {
              "id": "gate",
              "type": "conditional",
              "title": "Gate",
              "maxVisits": __MAXVISITS__,__ONMAXVISITS__
              "branches": [
                { "when": { "op": "gte", "path": "state.count", "value": 99 }, "to": "author" }
              ],
              "else": "a"
            },
            { "id": "author", "type": "procedural", "title": "Author", "next": ["done"] },
            { "id": "done", "type": "terminal", "title": "Done" }
          ]
        }
        """;

    /// <summary>
    ///     A <c>start → a → gate(conditional, back-edge to a) → author → done</c> loop where <c>gate</c> has
    ///     the given <paramref name="maxVisits"/> ceiling and (optionally) routes to <c>author</c> on
    ///     <c>onMaxVisits</c>. The back-edge (<c>gate.else = a</c>) lets a test loop <c>a → gate</c>.
    /// </summary>
    public static string MaxVisitsLoop(int maxVisits, bool withOnMaxVisits = true) =>
        MaxVisitsTemplate
            .Replace("__MAXVISITS__", maxVisits.ToString(CultureInfo.InvariantCulture))
            .Replace(
                "__ONMAXVISITS__",
                withOnMaxVisits ? "\n              \"onMaxVisits\": \"author\"," : string.Empty
            );

    /// <summary>
    ///     A <c>start → a → b → a</c> loop whose only route to the <c>done</c> terminal is the definition's
    ///     <c>onBudgetExhausted</c> escape (no node declares an edge to <c>done</c>), so the budget escape
    ///     must bypass the declared-edge check. <c>maxStepBudget</c> is 3.
    /// </summary>
    public const string BudgetWithEscape = """
        {
          "schemaVersion": 1,
          "objective": "Exhaust the step budget.",
          "maxStepBudget": 3,
          "onBudgetExhausted": "done",
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["a"] },
            { "id": "a", "type": "procedural", "title": "A", "next": ["b"] },
            { "id": "b", "type": "procedural", "title": "B", "next": ["a"] },
            { "id": "done", "type": "terminal", "title": "Done" }
          ]
        }
        """;

    /// <summary>
    ///     A <c>start → a → b → {a, done}</c> loop with a small <c>maxStepBudget</c> (2) and <b>no</b>
    ///     <c>onBudgetExhausted</c> escape, so a normal advance past the budget is allowed rather than
    ///     deadlocking the controller.
    /// </summary>
    public const string BudgetNoEscape = """
        {
          "schemaVersion": 1,
          "objective": "Exhaust the step budget without an escape.",
          "maxStepBudget": 2,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["a"] },
            { "id": "a", "type": "procedural", "title": "A", "next": ["b"] },
            { "id": "b", "type": "procedural", "title": "B", "next": ["a", "done"] },
            { "id": "done", "type": "terminal", "title": "Done" }
          ]
        }
        """;

    private const string ResultTemplateTemplate = """
        {
          "schemaVersion": 1,
          "objective": "Compose a final result from state.",
          "state": {
            "curriculum": { "problemCount": 2, "problems": [ { "id": "p0" }, { "id": "p1" } ] },
            "authored": [ { "solution": "s0" }, { "solution": "s1" } ]
          },
          "maxStepBudget": 50,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["done"] },
            {
              "id": "done",
              "type": "terminal",
              "title": "Done",
              "resultTemplate": {
                "curriculum": "{{state.curriculum}}",
                "authored": "{{state.authored}}"
              },
              "finalOutputSchema": {
                "type": "object",
                "required": __REQUIRED__,
                "properties": {
                  "curriculum": { "type": "object" },
                  "authored": { "type": "array" }
                }
              }
            }
          ]
        }
        """;

    /// <summary>
    ///     A <c>start → done(terminal with resultTemplate)</c> workflow. When <paramref name="schemaFail"/>
    ///     is <c>true</c> the terminal's <c>finalOutputSchema</c> additionally requires a <c>missing</c>
    ///     field the template never produces, so the composed result fails validation.
    /// </summary>
    public static string ResultTemplateWorkflow(bool schemaFail = false) =>
        ResultTemplateTemplate.Replace(
            "__REQUIRED__",
            schemaFail
                ? "[\"curriculum\", \"authored\", \"missing\"]"
                : "[\"curriculum\", \"authored\"]"
        );
}
