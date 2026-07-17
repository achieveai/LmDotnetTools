namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Result of <see cref="SandboxClient.PreviewMarketplacesAsync"/>: the marketplace aliases that
/// were resolved plus the nested plugin/skill/agent catalog for each — a read-only browse that
/// requires no sandbox session.
/// </summary>
public sealed class SandboxMarketplaceCatalog
{
    /// <summary>Marketplace aliases the gateway actually resolved, defensively copied at construction.</summary>
    public IReadOnlyList<string> Selected { get; }

    /// <summary>One entry per resolved marketplace, defensively copied at construction.</summary>
    public IReadOnlyList<SandboxMarketplaceEntry> Marketplaces { get; }

    public SandboxMarketplaceCatalog(IReadOnlyList<string>? selected, IReadOnlyList<SandboxMarketplaceEntry>? marketplaces)
    {
        Selected = selected is null ? [] : [.. selected];
        Marketplaces = marketplaces is null ? [] : [.. marketplaces];
    }
}

/// <summary>One marketplace alias and the plugins it exposes (or an <see cref="Error"/> if it failed to load).</summary>
public sealed class SandboxMarketplaceEntry
{
    public string Alias { get; }
    public string? Error { get; }
    public IReadOnlyList<SandboxMarketplacePlugin> Plugins { get; }

    public SandboxMarketplaceEntry(string alias, string? error, IReadOnlyList<SandboxMarketplacePlugin>? plugins)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        Alias = alias;
        Error = error;
        Plugins = plugins is null ? [] : [.. plugins];
    }
}

/// <summary>A plugin within a marketplace and the skills/agents it contributes.</summary>
public sealed class SandboxMarketplacePlugin
{
    public string Name { get; }
    public string? Version { get; }
    public string Description { get; }
    public IReadOnlyList<SandboxMarketplaceSkill> Skills { get; }
    public IReadOnlyList<SandboxMarketplaceAgent> Agents { get; }

    public SandboxMarketplacePlugin(
        string name,
        string? version,
        string? description,
        IReadOnlyList<SandboxMarketplaceSkill>? skills,
        IReadOnlyList<SandboxMarketplaceAgent>? agents
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Version = version;
        Description = description ?? string.Empty;
        Skills = skills is null ? [] : [.. skills];
        Agents = agents is null ? [] : [.. agents];
    }
}

/// <summary>A skill the gateway discovered in a plugin.</summary>
public sealed class SandboxMarketplaceSkill
{
    public string Name { get; }
    public string Description { get; }
    public string Plugin { get; }
    public string Marketplace { get; }
    public string Path { get; }

    public SandboxMarketplaceSkill(string name, string? description, string? plugin, string? marketplace, string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Description = description ?? string.Empty;
        Plugin = plugin ?? string.Empty;
        Marketplace = marketplace ?? string.Empty;
        Path = path ?? string.Empty;
    }
}

/// <summary>A sub-agent the gateway discovered in a plugin.</summary>
public sealed class SandboxMarketplaceAgent
{
    public string Name { get; }
    public string Description { get; }
    public string Plugin { get; }
    public string Marketplace { get; }
    public string Path { get; }

    public SandboxMarketplaceAgent(string name, string? description, string? plugin, string? marketplace, string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Description = description ?? string.Empty;
        Plugin = plugin ?? string.Empty;
        Marketplace = marketplace ?? string.Empty;
        Path = path ?? string.Empty;
    }
}
