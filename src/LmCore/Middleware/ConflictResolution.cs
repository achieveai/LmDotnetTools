namespace AchieveAi.LmDotnetTools.LmCore.Middleware;

/// <summary>
/// Conflict resolution strategies for when multiple providers have same function name
/// </summary>
public enum ConflictResolution
{
    /// <summary>
    /// Throw exception on conflict (default - fail fast)
    /// </summary>
    Throw,
    
    /// <summary>
    /// Use first provider's function (first registered wins)
    /// </summary>
    TakeFirst,
    
    /// <summary>
    /// Use last provider's function (last registered wins)
    /// </summary>
    TakeLast,
    
    /// <summary>
    /// Prefer MCP functions over others
    /// </summary>
    PreferMcp,
    
    /// <summary>
    /// Prefer Natural functions over others
    /// </summary>
    PreferNatural,
    
    /// <summary>
    /// Require explicit handling via conflict handler callback
    /// </summary>
    RequireExplicit
}