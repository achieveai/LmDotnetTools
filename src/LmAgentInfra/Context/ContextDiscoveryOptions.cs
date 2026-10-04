namespace AchieveAi.LmDotnetTools.LmAgentInfra.Context;

/// <summary>
/// Strongly-typed configuration for context-discovery routing. Bound from the
/// <c>ContextDiscovery</c> configuration section. Follows the same idiom as
/// <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox.SandboxGatewayOptions"/> /
/// <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.Auth.AuthOptions"/> (<c>sealed class</c> with
/// mutable <c>{ get; set; }</c> properties so the configuration binder can populate them).
/// </summary>
public sealed class ContextDiscoveryOptions
{
    /// <summary>
    /// Configuration section name these options are bound from.
    /// </summary>
    public const string SectionName = "ContextDiscovery";

    /// <summary>
    /// Enables owner-only passive delivery. The callback must identify an exact root or scoped
    /// sub-agent thread. When disabled, or ownership is absent, discovery is dropped, never broadcast.
    /// Explicit file reads and root context seeding remain available.
    /// </summary>
    public bool RouteToOpeningSubAgent { get; set; } = false;
}
