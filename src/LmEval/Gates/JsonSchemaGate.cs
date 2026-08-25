using System.Text.Json;
using AchieveAi.LmDotnetTools.LmEval.Running;

namespace AchieveAi.LmDotnetTools.LmEval.Gates;

/// <summary>
/// Rejects a candidate whose content is not a JSON object, or which is missing a property the task
/// requires.
/// <para>
/// A schema violation is exactly the kind of failure a deterministic check settles outright: asking
/// a model whether well-formed JSON is well-formed spends tokens to get a less reliable answer.
/// </para>
/// </summary>
public sealed class JsonSchemaGate : GateBase, IConfigurationFingerprint
{
    /// <summary>The stable id this gate records on every decision.</summary>
    public const string Id = "json-schema";

    private readonly IReadOnlyList<string> _requiredProperties;

    /// <summary>Creates the gate.</summary>
    /// <param name="requiredProperties">Property names the object must carry. May be empty.</param>
    /// <param name="appliesTo">Task types this gate applies to; empty means all.</param>
    public JsonSchemaGate(
        IEnumerable<string>? requiredProperties = null,
        IEnumerable<string>? appliesTo = null
    )
        : base(Id, appliesTo)
    {
        _requiredProperties = [.. requiredProperties ?? []];
    }

    /// <summary>
    /// The required-property set. Ordered so that the same set declared in a different order
    /// fingerprints identically — order does not change which candidates this gate rejects.
    /// </summary>
    public string? ConfigurationFingerprint =>
        "required="
        + string.Join(',', _requiredProperties.OrderBy(p => p, StringComparer.Ordinal));

    /// <inheritdoc />
    protected override GateDecision Evaluate(Candidate candidate)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(candidate.Content);
        }
        catch (JsonException)
        {
            return GateDecision.Reject(Id, "content is not well-formed JSON");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return GateDecision.Reject(
                    Id,
                    $"content is a JSON {document.RootElement.ValueKind}, not an object"
                );
            }

            var missing = _requiredProperties
                .Where(p => !document.RootElement.TryGetProperty(p, out _))
                .ToList();

            // The property NAMES are the task's own vocabulary, not the candidate's content, so
            // naming them keeps the reason actionable without quoting anything the model wrote.
            return missing.Count > 0
                ? GateDecision.Reject(Id, $"missing required properties: {string.Join(", ", missing)}")
                : GateDecision.Pass(Id, "content is a JSON object carrying every required property");
        }
    }
}
