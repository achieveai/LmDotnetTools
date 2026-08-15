namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Identifies a single plugin within a marketplace, as referenced by a workspace's explicit plugin
/// selection. Mirrors the gateway's <c>{marketplace, plugin}</c> wire pair (spec Section 5.1).
/// </summary>
public sealed class SandboxPluginRef
{
    public SandboxPluginRef(string marketplace, string plugin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplace);
        ArgumentException.ThrowIfNullOrWhiteSpace(plugin);

        Marketplace = marketplace;
        Plugin = plugin;
    }

    public string Marketplace { get; }

    public string Plugin { get; }
}
