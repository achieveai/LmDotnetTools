namespace AchieveAi.LmDotnetTools.LmCore.Core;

/// <summary>
/// Specifies the amount of reasoning effort requested from a model.
/// </summary>
/// <remarks>
/// Enum ordinals are not provider effort ranks and must not be persisted or sent on the wire.
/// Providers map these named values to their own canonical tokens and ranking schemes.
/// </remarks>
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
