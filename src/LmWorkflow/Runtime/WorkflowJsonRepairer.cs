using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

/// <summary>
///     The outcome of a PURE, side-effect-free schema check for a spawn's raw result text (see
///     <see cref="TaskCoordinator.CheckSpawnResult"/>). It carries no payload — only whether the correlated
///     task declares an output schema, whether the text already satisfies it, and (when it does not) the
///     schema string to repair against. Producing it records NOTHING, so it is safe to call before the
///     deterministic validation path and again to confirm a repair.
/// </summary>
internal readonly record struct SpawnSchemaCheck(bool HasSchema, bool IsValid, string? SchemaJson)
{
    /// <summary>The tool call is uncorrelated/unknown, or the task has no output schema: repair does not apply.</summary>
    internal static readonly SpawnSchemaCheck NoSchema = new(false, false, null);

    /// <summary>The result already extracts + parses + validates against the task's schema: no repair needed.</summary>
    internal static readonly SpawnSchemaCheck Valid = new(true, true, null);

    /// <summary>The schema'd result does NOT satisfy the schema; <paramref name="schemaJson"/> is what to repair against.</summary>
    internal static SpawnSchemaCheck Invalid(string schemaJson) => new(true, false, schemaJson);
}

/// <summary>
///     Best-effort JSON repair for a schema'd sub-agent result. When a sub-agent was asked to return JSON that
///     matches a required schema and its reply does not parse / does not validate, this re-asks a CHEAP LLM
///     (the lowest wired tier) to rewrite the reply into schema-valid JSON — the same "hand the raw text back
///     to a fallback parser and re-validate" pattern the natural-tool-use parser uses. Repair is strictly
///     best-effort and non-regressive: the caller re-validates the returned text and substitutes it ONLY when
///     it now satisfies the schema, so a failed or garbage repair can never worsen the deterministic failure
///     path.
/// </summary>
/// <remarks>
///     De-identification: the raw sub-agent text is sent ONLY to the repair LLM — an egress the sub-agent's
///     own output already traversed. It is NEVER written into durable/projected workflow state (recorded
///     failure reasons continue to carry only length/count/type metadata), and it is NEVER logged.
/// </remarks>
internal sealed class WorkflowJsonRepairer
{
    private readonly IAgent _repairAgent;
    private readonly ILogger? _logger;

    internal WorkflowJsonRepairer(IAgent repairAgent, ILogger? logger = null)
    {
        _repairAgent = repairAgent ?? throw new ArgumentNullException(nameof(repairAgent));
        _logger = logger;
    }

    /// <summary>
    ///     Asks the cheap repair agent to rewrite <paramref name="rawText"/> into JSON that satisfies
    ///     <paramref name="schemaJson"/>. Returns the (trimmed) rewritten text on success, or <c>null</c> when
    ///     the agent yields no usable text or the call fails — in which case the caller keeps the original
    ///     result and the deterministic failure path runs unchanged. Cancellation propagates.
    /// </summary>
    internal async Task<string?> TryRepairAsync(string rawText, string schemaJson, CancellationToken ct)
    {
        try
        {
            var messages = new List<IMessage>
            {
                new TextMessage { Text = BuildPrompt(rawText, schemaJson), Role = Role.User },
            };

            // json_object mode nudges JSON-only output across cheap-tier transports without depending on the
            // provider's own schema enforcement; correctness is owned by the caller's re-validation, not here.
            var options = new GenerateReplyOptions { ResponseFormat = ResponseFormat.JSON };

            var replies = await _repairAgent.GenerateReplyAsync(messages, options, ct).ConfigureAwait(false);
            var text = replies.OfType<TextMessage>().FirstOrDefault()?.Text;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort: any repair failure degrades to the original deterministic path. The template
            // carries NO payload (rawText/schema are never interpolated into the log).
            _logger?.LogDebug(ex, "Best-effort sub-agent JSON repair failed; keeping the original result");
            return null;
        }
    }

    private static string BuildPrompt(string rawText, string schemaJson) =>
        "A sub-agent was asked to return JSON matching a required schema, but its reply did not parse or did "
        + "not satisfy the schema. Rewrite the reply into a SINGLE valid JSON value that matches the schema. "
        + "Preserve the sub-agent's intent and data; do not invent values. Return ONLY the JSON — no prose, no "
        + "Markdown fences.\n\nRequired JSON schema:\n"
        + schemaJson
        + "\n\nSub-agent reply to fix:\n"
        + rawText;
}
