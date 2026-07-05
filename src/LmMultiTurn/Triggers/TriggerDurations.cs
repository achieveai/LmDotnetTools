using System.Globalization;
using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

/// <summary>
/// Parses the compact duration / absolute-time strings the LLM supplies for <c>timeout</c> and
/// timer <c>args</c> — e.g. <c>"30s"</c>, <c>"10m"</c>, <c>"2h"</c>, <c>"1d"</c>, or an absolute
/// ISO-8601 instant like <c>"2026-07-31T12:00:00Z"</c>.
/// </summary>
internal static partial class TriggerDurations
{
    [GeneratedRegex(@"^\s*(?<value>\d+)\s*(?<unit>ms|s|m|h|d)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    /// <summary>
    /// Parses a relative duration string ("10m", "500ms", …). Returns false for absolute times or
    /// malformed input.
    /// </summary>
    public static bool TryParseDuration(string? spec, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var match = DurationRegex().Match(spec);
        if (!match.Success)
        {
            return false;
        }

        if (!long.TryParse(match.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        duration = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "ms" => TimeSpan.FromMilliseconds(value),
            "s" => TimeSpan.FromSeconds(value),
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            "d" => TimeSpan.FromDays(value),
            _ => TimeSpan.Zero,
        };
        return duration > TimeSpan.Zero;
    }

    /// <summary>
    /// Resolves a spec that is EITHER a relative duration (added to <paramref name="from"/>) OR an
    /// absolute ISO-8601 instant, into an absolute instant. Returns false with a reason otherwise.
    /// </summary>
    public static bool TryResolveInstant(
        string? spec,
        DateTimeOffset from,
        out DateTimeOffset instant,
        out string? error)
    {
        instant = default;
        error = null;

        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "value is required";
            return false;
        }

        if (TryParseDuration(spec, out var duration))
        {
            instant = from + duration;
            return true;
        }

        if (DateTimeOffset.TryParse(
                spec,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            instant = parsed;
            return true;
        }

        error = $"'{spec}' is not a valid duration (e.g. \"10m\") or ISO-8601 time";
        return false;
    }

    /// <summary>
    /// Resolves a wait's ceiling deadline from a <c>timeout</c> spec relative to
    /// <paramref name="from"/>, clamped so the deadline is never further out than
    /// <paramref name="max"/> and never in the past.
    /// </summary>
    public static bool TryResolveDeadline(
        string? spec,
        DateTimeOffset from,
        TimeSpan max,
        out DateTimeOffset deadline,
        out string? error)
    {
        deadline = default;

        if (!TryResolveInstant(spec, from, out var instant, out error))
        {
            return false;
        }

        if (instant <= from)
        {
            error = "timeout must be in the future";
            return false;
        }

        var maxDeadline = from + max;
        deadline = instant > maxDeadline ? maxDeadline : instant;
        return true;
    }
}
