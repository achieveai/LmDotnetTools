namespace AchieveAi.LmDotnetTools.LmEval.Running;

/// <summary>
/// A component that can describe every score-affecting setting it holds, so that setting can enter
/// the evaluator config hash.
/// <para>
/// This exists because identity is not configuration. Two <c>length-bounds</c> gates share a
/// <see cref="IGate.GateId"/> and reject different candidates; two judges can share an id, a model
/// and a family and render different prompts. A hash built from identity alone would call those
/// pairs equal, and the comparison refusal built on that hash would then wave through the exact
/// change it exists to catch — a retuned gate bound moving the reported pass rate with nothing
/// about the candidate having changed.
/// </para>
/// </summary>
public interface IConfigurationFingerprint
{
    /// <summary>
    /// A stable, deterministic description of this component's score-affecting configuration, or
    /// <b>null</b> when the component cannot describe it.
    /// <para>
    /// Null is a real answer, not a failure to implement: a judge handed an opaque host-supplied
    /// prompt renderer genuinely does not know what bytes it will send. It is reported as such
    /// rather than papered over with a constant, because a constant would make two different
    /// configurations hash identically — and the caller's response to null is to <i>refuse</i>, so
    /// the honest answer costs a loud error where the dishonest one costs a wrong number.
    /// </para>
    /// <para>
    /// It must not contain a secret, a candidate excerpt or anything else non-stable: it is held to
    /// the same rail as <see cref="GateDecision.Reason"/>, and it reaches persistence through the
    /// hash of the baseline it is frozen into.
    /// </para>
    /// </summary>
    string? ConfigurationFingerprint { get; }
}
