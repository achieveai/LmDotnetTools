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
}
