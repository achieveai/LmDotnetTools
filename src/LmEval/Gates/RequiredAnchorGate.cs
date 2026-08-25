using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmEval.Running;

namespace AchieveAi.LmDotnetTools.LmEval.Gates;

/// <summary>
/// Rejects a candidate that cites nothing resolvable — no <c>path/to/file.ext:line</c> anchor.
/// <para>
/// This is deliberately the SHALLOW version of a per-finding check: it asks whether citations are
/// present and well-formed, not whether the line they name exists. Answering the deeper question
/// needs a parsed finding model, which does not exist yet and which the eval runner will have to
/// build for its own reasons.
/// </para>
/// <para>
/// It is also the structural half of the anti-verbosity rule: a review is credited for citations
/// that resolve, not for prose that reads as thorough.
/// </para>
/// </summary>
public sealed partial class RequiredAnchorGate : GateBase, IConfigurationFingerprint
{
    /// <summary>The stable id this gate records on every decision.</summary>
    public const string Id = "required-anchor";

    private readonly int _minimumAnchors;

    /// <summary>Creates the gate.</summary>
    /// <param name="minimumAnchors">How many distinct citations the content must carry.</param>
    /// <param name="appliesTo">Task types this gate applies to; empty means all.</param>
    public RequiredAnchorGate(int minimumAnchors = 1, IEnumerable<string>? appliesTo = null)
        : base(Id, appliesTo)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumAnchors, 1);
        _minimumAnchors = minimumAnchors;
    }

    /// <summary>The citation floor, which is the whole of this gate's configuration.</summary>
    public string? ConfigurationFingerprint => $"minimumAnchors={_minimumAnchors}";

    /// <inheritdoc />
    protected override GateDecision Evaluate(Candidate candidate)
    {
        var found = AnchorPattern().Matches(candidate.Content).Count;

        // The COUNT is reported, never the anchors themselves: a file path is candidate content and
        // the reason string is persisted.
        return found < _minimumAnchors
            ? GateDecision.Reject(
                Id,
                $"found {found} file:line citations, fewer than the {_minimumAnchors} required"
            )
            : GateDecision.Pass(Id, $"found {found} file:line citations");
    }

    /// <summary>
    /// A <c>path/file.ext:line</c> citation. The directory separator is required so that ordinary
    /// prose containing a bare "word.cs:1" style token does not read as a resolvable path.
    /// </summary>
    [GeneratedRegex(@"[\w./\\-]+[/\\][\w.-]+\.\w+:\d+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AnchorPattern();
}
