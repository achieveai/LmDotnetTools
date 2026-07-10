namespace AchieveAi.LmDotnetTools.LmCore.Core;

/// <summary>
/// Specifies the amount of reasoning effort requested from a model.
/// </summary>
public enum ReasoningEffort
{
    /// <summary>Requests low reasoning effort.</summary>
    Low,

    /// <summary>Requests medium reasoning effort.</summary>
    Medium,

    /// <summary>Requests high reasoning effort.</summary>
    High,

    /// <summary>Requests extra-high reasoning effort.</summary>
    Xhigh,
}
