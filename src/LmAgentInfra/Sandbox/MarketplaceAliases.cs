namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Canonical parser for a comma-separated marketplace-alias list. Shared by the two callers that
/// accept the same format from different sources — the sandbox-create path
/// (<see cref="SandboxGatewayOptions.Marketplaces"/> config) and the catalog-browse endpoint
/// (<c>GET /api/marketplaces?marketplaces=…</c> query) — so the "trim, drop blanks, null when empty"
/// rule has a single owner instead of being duplicated or borrowed across unrelated classes.
/// </summary>
public static class MarketplaceAliases
{
    /// <summary>
    /// Splits <paramref name="value"/> on commas, trims each entry and drops blanks. Returns
    /// <c>null</c> (never an empty list) when nothing remains, so callers omit the field and the
    /// gateway applies its own default set — an empty array would instead select zero marketplaces.
    /// </summary>
    public static IReadOnlyList<string>? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var aliases = value.Split(
            ',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return aliases.Length > 0 ? aliases : null;
    }

    /// <summary>
    /// Resolves the marketplace aliases a workspace actually runs under: its own selection when it
    /// enables any, otherwise the configured global default parsed from
    /// <paramref name="configuredDefault"/>. Returns <c>null</c> — never an empty list — when neither
    /// supplies anything, meaning "omit the field and let the gateway apply its own default set".
    /// </summary>
    /// <remarks>
    /// An EMPTY workspace selection means "this workspace names no preference", NOT "this workspace
    /// enables nothing". Those read alike and behave oppositely, so the rule has exactly one owner
    /// here and two callers: the sandbox-create path, which builds the session, and the workspace
    /// plugin-selection validator, which decides whether a selection is legal. Duplicating it let
    /// them disagree — the validator narrowed an explicit selection to an empty enabled set and
    /// rejected every plugin the session it was validating would have loaded quite happily.
    /// </remarks>
    public static IReadOnlyList<string>? ResolveEffective(
        IReadOnlyList<string>? workspaceMarketplaces,
        string? configuredDefault) =>
        workspaceMarketplaces is { Count: > 0 } ? workspaceMarketplaces : Parse(configuredDefault);
}
