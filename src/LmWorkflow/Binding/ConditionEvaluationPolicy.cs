namespace AchieveAi.LmDotnetTools.LmWorkflow.Binding;

/// <summary>Controls how a structured workflow condition compares resolved values.</summary>
public enum ConditionEvaluationPolicy
{
    /// <summary>Preserves the original controller-workflow comparison behavior.</summary>
    Legacy,

    /// <summary>
    ///     Enforces authored-workflow contracts: required operands must exist and match their expected
    ///     JSON types, ordered comparisons are numeric, and string equality ignores case ordinally.
    /// </summary>
    StrictWorkflow,
}
