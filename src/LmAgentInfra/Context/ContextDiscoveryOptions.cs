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
    /// When true, a <c>context_file</c> discovery carrying a non-blank <c>agent_id</c> is routed to
    /// the sub-agent that opened the file instead of fanned out to the primary conversation. Default
    /// <c>false</c> so the routing path is dormant-but-correct until the gateway actually stamps
    /// <c>agent_id</c> (cf. #187): with the flag off, discovery behaves byte-identically to today.
    /// </summary>
    public bool RouteToOpeningSubAgent { get; set; } = false;
}
