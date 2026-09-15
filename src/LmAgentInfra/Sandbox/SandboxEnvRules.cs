using System.Text;
using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// App-side mirror of the gateway's per-sandbox environment-variable validation
/// (<c>gateway_types::sandbox_env</c>, SandboxedOstoolsMcpServer #183): key grammar, size limits, and the
/// reserved-name list. Kept in lockstep with the gateway on purpose — the app validates BEFORE sending, so a
/// map that fails here would otherwise round-trip to the gateway only to come back as a
/// <see cref="AchieveAi.LmDotnetTools.Sandbox.SandboxErrorKind.InvalidEnv"/> the caller has to re-parse.
/// </summary>
/// <remarks>
/// SECURITY / PRIVACY: nothing in this type ever logs, throws, or otherwise surfaces an environment
/// VALUE — only key names. Values may carry secrets (API tokens, connection strings) the caller intends
/// for the sandbox process alone.
/// </remarks>
public static partial class SandboxEnvRules
{
    /// <summary>Maximum UTF-8 byte length of a single key.</summary>
    public const int MaxKeyBytes = 256;

    /// <summary>Maximum UTF-8 byte length of a single value.</summary>
    public const int MaxValueBytes = 32 * 1024;

    /// <summary>Maximum number of keys in one environment map.</summary>
    public const int MaxKeys = 256;

    /// <summary>Maximum combined UTF-8 byte length of every key plus every value in the map.</summary>
    public const int MaxTotalBytes = 128 * 1024;

    /// <summary>
    /// Names the sandbox runtime/gateway itself owns and that a caller can never override — rejected
    /// case-insensitively regardless of the grammar/size checks. <c>PATH</c> is deliberately NOT here:
    /// it is a normal, caller-settable variable.
    /// </summary>
    public static readonly IReadOnlyList<string> ProtectedNames =
    [
        "SANDBOX_ALLOWED_PATHS",
        "SANDBOX_WORKSPACE",
        "SANDBOX_HOME",
        "PWSH_STATE_DIR",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "NO_PROXY",
        "NODE_TLS_REJECT_UNAUTHORIZED",
        "PYTHONHTTPSVERIFY",
        "GIT_SSL_NO_VERIFY",
        "REQUESTS_CA_BUNDLE",
        "SSL_CERT_FILE",
        "CURL_CA_BUNDLE",
        "NODE_EXTRA_CA_CERTS",
        "NODE_USE_ENV_PROXY",
    ];

    private static readonly HashSet<string> ProtectedNamesLookup = new(
        ProtectedNames,
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Grammar every key must satisfy: starts with a letter or underscore, then letters/digits/underscores.</summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyGrammar();

    /// <summary>
    /// Returns every offending key in <paramref name="env"/>, or an empty list when the whole map is
    /// valid. A per-key violation (bad grammar, oversize, embedded NUL, protected name) names only that
    /// key. A case-insensitive duplicate group (two distinct <see cref="System.Collections.Generic.IReadOnlyDictionary{TKey,TValue}"/>
    /// entries that collide under <see cref="StringComparer.OrdinalIgnoreCase"/> — possible because the
    /// map itself is Ordinal-keyed) names every key in that group. A map-level violation (too many keys,
    /// or the combined byte total too large) names EVERY key, since there is no single offending entry to
    /// point at.
    /// </summary>
    public static IReadOnlyList<string> FindInvalidKeys(IReadOnlyDictionary<string, string> env)
    {
        ArgumentNullException.ThrowIfNull(env);

        if (env.Count == 0)
        {
            return [];
        }

        // Map-level: too many keys. Nothing else can be checked meaningfully at that point, so every key
        // is named and per-key/duplicate checks are skipped.
        if (env.Count > MaxKeys)
        {
            return [.. env.Keys];
        }

        long totalBytes = 0;
        var invalid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in env)
        {
            var keyBytes = Encoding.UTF8.GetByteCount(key);
            var valueBytes = Encoding.UTF8.GetByteCount(value);
            totalBytes += keyBytes + valueBytes;

            if (
                key.Length == 0
                || !KeyGrammar().IsMatch(key)
                || keyBytes > MaxKeyBytes
                || key.Contains('\0')
                || value.Contains('\0')
                || valueBytes > MaxValueBytes
                || ProtectedNamesLookup.Contains(key)
            )
            {
                invalid.Add(key);
            }
        }

        // Map-level: combined size too large. Every key is named — the violation is not attributable to
        // any single entry.
        if (totalBytes > MaxTotalBytes)
        {
            return [.. env.Keys];
        }

        // Case-insensitive duplicate groups: the map is Ordinal-keyed, so "FOO" and "foo" can coexist as
        // distinct entries even though the gateway treats them as one name. Every key in a colliding group
        // is named, not just the second one seen.
        foreach (var group in env.Keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
            {
                foreach (var key in group)
                {
                    invalid.Add(key);
                }
            }
        }

        return invalid.Count == 0 ? [] : [.. invalid];
    }

    /// <summary>
    /// Throws <see cref="SandboxEnvValidationException"/> when <paramref name="env"/> has any invalid
    /// key (see <see cref="FindInvalidKeys"/>). A <see langword="null"/> or empty map is always valid.
    /// </summary>
    /// <param name="env">The environment map to validate, or <see langword="null"/>.</param>
    /// <param name="layer">
    /// Which layer produced <paramref name="env"/> (e.g. <c>"workspace"</c>, <c>"app"</c>) — carried on
    /// the exception so a caller merging several layers can tell which one introduced the bad key.
    /// </param>
    public static void Validate(IReadOnlyDictionary<string, string>? env, string layer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layer);

        if (env is null || env.Count == 0)
        {
            return;
        }

        var invalidKeys = FindInvalidKeys(env);
        if (invalidKeys.Count > 0)
        {
            throw new SandboxEnvValidationException(layer, invalidKeys);
        }
    }

    /// <summary>
    /// Merges environment-variable layers left-to-right: a later layer's value for a key overrides an
    /// earlier layer's. A <see langword="null"/> layer is skipped entirely (never treated as "clear
    /// everything"). The result is a fresh, Ordinal-keyed map — inputs are never mutated.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Merge(params IReadOnlyDictionary<string, string>?[] layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            if (layer is null)
            {
                continue;
            }

            foreach (var (key, value) in layer)
            {
                merged[key] = value;
            }
        }

        return merged;
    }

    /// <summary>
    /// Computes the flat change set that turns <paramref name="lastApplied"/> into
    /// <paramref name="desired"/>, in the shape <see cref="AchieveAi.LmDotnetTools.Sandbox.SandboxClient.PatchEnvAsync"/>
    /// expects: a changed or newly-added key maps to its new value, and a key present in
    /// <paramref name="lastApplied"/> but absent from <paramref name="desired"/> maps to
    /// <see langword="null"/> (unset). A key whose value is unchanged is omitted. Empty when the two maps
    /// are equal.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Diff(
        IReadOnlyDictionary<string, string> lastApplied,
        IReadOnlyDictionary<string, string> desired
    )
    {
        ArgumentNullException.ThrowIfNull(lastApplied);
        ArgumentNullException.ThrowIfNull(desired);

        var diff = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in desired)
        {
            if (
                !lastApplied.TryGetValue(key, out var existing)
                || !string.Equals(existing, value, StringComparison.Ordinal)
            )
            {
                diff[key] = value;
            }
        }

        foreach (var key in lastApplied.Keys)
        {
            if (!desired.ContainsKey(key))
            {
                diff[key] = null;
            }
        }

        return diff;
    }
}

/// <summary>
/// Thrown by <see cref="SandboxEnvRules.Validate"/> when an environment-variable map fails validation.
/// The message NEVER includes a value — only the offending key names — since values may carry secrets.
/// </summary>
public sealed class SandboxEnvValidationException : Exception
{
    /// <summary>Which layer (e.g. <c>"workspace"</c>, <c>"app"</c>) produced the invalid map.</summary>
    public string Layer { get; }

    /// <summary>The offending key names. Never includes a value.</summary>
    public IReadOnlyList<string> Keys { get; }

    public SandboxEnvValidationException(string layer, IReadOnlyList<string> keys)
        : base($"Invalid sandbox environment in layer '{layer}': {string.Join(", ", keys)}")
    {
        Layer = layer;
        Keys = keys;
    }
}
