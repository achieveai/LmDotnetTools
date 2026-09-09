using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Compiles the appsettings-driven sandbox egress policy (<c>SandboxGateway:Network:Rules</c> +
/// <c>SandboxGateway:AuthProviders</c>) into the gateway's wire types, and validates it fail-closed.
/// </summary>
/// <remarks>
/// <para>
/// SECURITY. This is a trust boundary: every configured rule widens a default-deny egress policy, and
/// every configured provider is a credential-injection point. <see cref="Validate"/> therefore
/// aggregates ALL errors (so an operator fixes the whole file in one pass) and every message names the
/// offending rule/provider/header id and field — <b>never</b> a value, because these messages land in
/// startup logs.
/// </para>
/// <para>
/// Gateway semantics mirrored here: default action Deny; rules evaluated in ASCENDING priority with
/// first-match-wins; AND across dimensions, OR within one; an EMPTY dimension matches anything;
/// methods are case-insensitive with a <c>*</c> wildcard; the path grammar is only <c>*</c>, a
/// trailing <c>/*</c> inclusive prefix, or an exact case-sensitive path; hosts are exact or a single
/// leading <c>*.</c> suffix. The gateway itself hard-rejects a broad allow that sits at or in front of
/// an overlapping gate — the corresponding checks below fail that configuration at startup instead of
/// at sandbox creation.
/// </para>
/// <para>
/// APEX DIVERGENCE (deliberate). The gateway's ACL treats <c>*.example.com</c> as ALSO matching the
/// bare apex <c>example.com</c>; <see cref="EgressHostMatcher"/> does not, and is left strict so a
/// managed OAuth provider's token-injection scope is never widened by a wildcard. The two semantics
/// are reconciled here rather than in the matcher:
/// <list type="bullet">
/// <item>Shadow detection (<c>HostsOverlap</c> via <c>WildcardCoversExact</c>, and the apex candidate
/// selection in <c>ValidateShadowing</c>) uses the GATEWAY's wider rule, so a configuration the
/// gateway would consider overlapping cannot slip past startup.</item>
/// <item>Provider host-scope containment (<c>IsWithinScope</c>) keeps THIS app's strict rule, so an
/// exact apex rule host under a <c>*.</c> provider scope is rejected at startup instead of validating
/// and then being denied at runtime by the credential webhook. List the apex host explicitly — in both
/// the rule and the provider — when a credential must reach it.</item>
/// </list>
/// </para>
/// </remarks>
internal static partial class SandboxEgressPolicyCompiler
{
    /// <summary>Wire-id prefix for every CONFIGURED provider, so one can never collide with a managed id.</summary>
    public const string ConfiguredProviderIdPrefix = "cfg-";

    /// <summary>Priority the managed (github/ado/m365) gate rules are emitted at.</summary>
    private const int ManagedGatePriority = 100;

    /// <summary>Provider identities owned by code; configuration may not define or shadow them.</summary>
    private static readonly HashSet<string> ReservedProviderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "github",
        "github-auth",
        "ado",
        "ado-auth",
        "m365",
        "m365-auth",
    };

    /// <summary>The ordinary HTTP verb set a rule may name, plus the gateway's <c>*</c> wildcard.</summary>
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET",
        "HEAD",
        "POST",
        "PUT",
        "PATCH",
        "DELETE",
        "OPTIONS",
        "*",
    };

    private const string RuleSection = "SandboxGateway:Network:Rules";
    private const string ProviderSection = "SandboxGateway:AuthProviders";

    /// <summary>
    /// The grammar every configured rule and provider key must satisfy: 1–64 characters of letters,
    /// digits, <c>.</c>, <c>_</c> or <c>-</c>, starting alphanumeric. A key is emitted verbatim as a
    /// wire id and, for a <c>headers</c> provider, as the LAST path segment of this app's auth-webhook
    /// URL — which <c>WebhookVerificationMiddleware.IsWebhookRoute</c> accepts only as exactly one
    /// non-empty segment. Separators, query/fragment characters, whitespace and percent-escapes would
    /// therefore validate clean and then never be routable.
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdGrammar();

    private const string IdGrammarReason =
        "id must be 1-64 chars of letters, digits, '.', '_' or '-' and start alphanumeric.";

    /// <summary>
    /// Splits a comma-delimited configuration scalar: entries are trimmed, empties dropped, and
    /// duplicates removed under <paramref name="comparer"/> (the first spelling wins). The default is
    /// case-insensitive, which is right for hosts, methods, ports and scopes; PATHS must be deduped
    /// with <see cref="StringComparer.Ordinal"/> because path matching is case-sensitive, so
    /// <c>/Admin/*</c> and <c>/admin/*</c> are two distinct patterns.
    /// </summary>
    public static IReadOnlyList<string> ParseList(string? value, StringComparer? comparer = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(comparer ?? StringComparer.OrdinalIgnoreCase);
        foreach (var raw in value.Split(','))
        {
            var entry = raw.Trim();
            if (entry.Length > 0 && seen.Add(entry))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Validates the whole egress policy. Returns an EMPTY list when the configuration is usable, and
    /// otherwise every problem found, each naming the offending id and field but never a value.
    /// </summary>
    public static IReadOnlyList<string> Validate(SandboxGatewayOptions options)
    {
        var errors = new List<string>();
        var providers = options.AuthProviders ?? [];
        var rules = options.Network?.Rules ?? [];

        foreach (var (key, provider) in providers)
        {
            ValidateProvider(key, provider, errors);
        }

        foreach (var (key, rule) in rules)
        {
            ValidateRule(key, rule, providers, errors);
        }

        ValidateShadowing(rules, errors);
        return errors;
    }

    private static void ValidateProvider(string key, SandboxEgressAuthProviderOptions provider, List<string> errors)
    {
        if (key.StartsWith(PredefinedKeyRegistry.ProviderIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{ProviderSection}:{key} — the '{PredefinedKeyRegistry.ProviderIdPrefix}' prefix is reserved for runtime egress keys."
            );
            return;
        }

        if (ReservedProviderKeys.Contains(key))
        {
            errors.Add($"{ProviderSection}:{key} — this id is a managed provider identity and cannot be configured.");
            return;
        }

        // Checked before the tombstone test: the key is the wire id and the webhook route segment,
        // and a tombstone that cannot name anything routable is a mistake worth surfacing.
        if (!IdGrammar().IsMatch(key))
        {
            errors.Add($"{ProviderSection}:{key} — {IdGrammarReason}");
            return;
        }

        // A tombstone carries no policy of its own; nothing left to validate. Rules that still
        // reference it are rejected by ValidateRule.
        if (!provider.Enabled)
        {
            return;
        }

        var type = provider.Type?.Trim() ?? string.Empty;
        var isHeaders = string.Equals(type, "headers", StringComparison.OrdinalIgnoreCase);
        var isWebhook = string.Equals(type, "webhook", StringComparison.OrdinalIgnoreCase);
        if (!isHeaders && !isWebhook)
        {
            errors.Add($"{ProviderSection}:{key}:Type — must be 'headers' or 'webhook'.");
        }

        var hosts = ParseList(provider.Hosts);
        if (hosts.Count == 0)
        {
            errors.Add($"{ProviderSection}:{key}:Hosts — at least one host is required.");
        }

        foreach (var host in hosts)
        {
            if (EgressHostMatcher.ValidateHostPattern(host) is { } hostError)
            {
                errors.Add($"{ProviderSection}:{key}:Hosts — {hostError}");
            }
        }

        if (provider.CacheTtlSeconds < 0)
        {
            errors.Add($"{ProviderSection}:{key}:CacheTtlSeconds — must not be negative.");
        }

        if (isHeaders)
        {
            ValidateHeaders(key, provider, errors);
        }

        if (isWebhook)
        {
            ValidateWebhookEndpoint(key, provider, errors);
        }
    }

    private static void ValidateHeaders(string key, SandboxEgressAuthProviderOptions provider, List<string> errors)
    {
        var enabled = provider.Headers.Where(h => h.Value.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            errors.Add($"{ProviderSection}:{key}:Headers — a headers provider needs at least one enabled header.");
        }

        foreach (var (name, header) in enabled)
        {
            if (EgressHostMatcher.ValidateHeaderName(name) is { } nameError)
            {
                errors.Add($"{ProviderSection}:{key}:Headers:{name} — {nameError}");
            }

            // Only the failure MODE is reported; the value itself is a secret and never echoed.
            if (EgressHostMatcher.ValidateHeaderValue(header.Value) is { } valueError)
            {
                errors.Add($"{ProviderSection}:{key}:Headers:{name}:Value — {valueError}");
            }
        }
    }

    private static void ValidateWebhookEndpoint(
        string key,
        SandboxEgressAuthProviderOptions provider,
        List<string> errors
    )
    {
        if (string.IsNullOrWhiteSpace(provider.GatewayAuth))
        {
            errors.Add(
                $"{ProviderSection}:{key}:GatewayAuth — a webhook provider needs its own gateway↔endpoint secret."
            );
        }

        if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint))
        {
            errors.Add($"{ProviderSection}:{key}:Endpoint — must be an absolute https:// URL.");
            return;
        }

        var isLoopback = endpoint.IsLoopback;
        if (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && isLoopback))
        {
            errors.Add($"{ProviderSection}:{key}:Endpoint — must use https (http is allowed only for loopback).");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            errors.Add($"{ProviderSection}:{key}:Endpoint — must not embed userinfo credentials.");
        }

        if (!string.IsNullOrEmpty(endpoint.Fragment))
        {
            errors.Add($"{ProviderSection}:{key}:Endpoint — must not contain a fragment.");
        }
    }

    private static void ValidateRule(
        string key,
        SandboxNetworkRuleOptions rule,
        IDictionary<string, SandboxEgressAuthProviderOptions> providers,
        List<string> errors
    )
    {
        if (key.StartsWith(PredefinedKeyRegistry.ProviderIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{RuleSection}:{key} — the '{PredefinedKeyRegistry.ProviderIdPrefix}' prefix is reserved for runtime egress keys."
            );
            return;
        }

        // Rule keys are emitted verbatim as wire ids and echoed back by the gateway as rule_id, so
        // they share the provider-key grammar.
        if (!IdGrammar().IsMatch(key))
        {
            errors.Add($"{RuleSection}:{key} — {IdGrammarReason}");
            return;
        }

        // A tombstone only names a rule to remove; it carries no policy to validate.
        if (!rule.Enabled)
        {
            return;
        }

        var action = rule.Action?.Trim() ?? string.Empty;
        if (
            !string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(action, "deny", StringComparison.OrdinalIgnoreCase)
        )
        {
            errors.Add($"{RuleSection}:{key}:Action — must be 'allow' or 'deny'.");
        }

        var hosts = ParseList(rule.Hosts);
        if (hosts.Count == 0)
        {
            errors.Add($"{RuleSection}:{key}:Hosts — at least one host is required.");
        }

        foreach (var host in hosts)
        {
            if (EgressHostMatcher.ValidateHostPattern(host) is { } hostError)
            {
                errors.Add($"{RuleSection}:{key}:Hosts — {hostError}");
            }
        }

        var portTokens = ParseList(rule.Ports);
        if (portTokens.Count == 0)
        {
            errors.Add($"{RuleSection}:{key}:Ports — at least one port is required.");
        }

        // Every message below names the entry by its 1-based POSITION in the parsed list, never by its
        // value: these strings land in startup logs (see the class remarks).
        var ports = new List<int>();
        for (var i = 0; i < portTokens.Count; i++)
        {
            if (!int.TryParse(portTokens[i], out var port) || port is < 1 or > 65535)
            {
                errors.Add($"{RuleSection}:{key}:Ports — entry #{i + 1} is not a port in 1..65535.");
            }
            else
            {
                ports.Add(port);
            }
        }

        var methods = ParseList(rule.Methods);
        if (methods.Count == 0)
        {
            errors.Add($"{RuleSection}:{key}:Methods — at least one method is required (use '*' for any).");
        }

        for (var i = 0; i < methods.Count; i++)
        {
            if (!AllowedMethods.Contains(methods[i]))
            {
                errors.Add($"{RuleSection}:{key}:Methods — entry #{i + 1} is not an HTTP method this policy accepts.");
            }
        }

        var paths = ParseList(rule.Paths, StringComparer.Ordinal);
        for (var i = 0; i < paths.Count; i++)
        {
            if (!IsValidPathPattern(paths[i]))
            {
                errors.Add(
                    $"{RuleSection}:{key}:Paths — entry #{i + 1} is not a supported pattern (use '*', an exact '/path', or a trailing '/prefix/*')."
                );
            }
        }

        if (string.IsNullOrWhiteSpace(rule.AuthProvider))
        {
            return;
        }

        var providerKey = rule.AuthProvider.Trim();
        if (!providers.TryGetValue(providerKey, out var provider) || !provider.Enabled)
        {
            errors.Add($"{RuleSection}:{key}:AuthProvider — does not name a configured, enabled auth provider.");
            return;
        }

        // An authenticated rule is a credential-injection scope. It must stay on HTTPS/443 (so the
        // secret never egresses in cleartext) and inside its provider's declared host scope (so a
        // rule can never widen where the credential goes beyond what the provider declares).
        if (ports.Exists(p => p != 443))
        {
            errors.Add($"{RuleSection}:{key}:Ports — an authenticated rule is HTTPS/443 only.");
        }

        var providerHosts = ParseList(provider.Hosts);
        for (var i = 0; i < hosts.Count; i++)
        {
            if (!IsWithinScope(providerHosts, hosts[i]))
            {
                errors.Add(
                    $"{RuleSection}:{key}:Hosts — entry #{i + 1} is outside the host scope of the rule's auth provider."
                );
            }
        }
    }

    /// <summary>
    /// Rejects a broad ALLOW rule that would be evaluated at or before a CONFIGURED gate (deny, or
    /// auth-provider-bearing) it overlaps, naming both configuration keys. First-match-wins by ascending
    /// priority means such a rule silently turns a credential-gated (or denied) host into open egress.
    /// </summary>
    /// <remarks>
    /// Scope is deliberately configuration-only. Managed and <c>predefined-*</c> gates exist only in the
    /// MERGED rule set — and a config rule named after a managed rule REPLACES it, leaving no gate to
    /// shadow — so judging them here would reject legal configurations. The merged set is checked by
    /// <see cref="ValidateEffectivePrecedence"/>, which is the authority for those gates.
    /// </remarks>
    private static void ValidateShadowing(IDictionary<string, SandboxNetworkRuleOptions> rules, List<string> errors)
    {
        var enabled = rules.Where(r => r.Value.Enabled).ToArray();
        var gates = enabled
            .Where(r =>
                string.Equals(r.Value.Action?.Trim(), "deny", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrWhiteSpace(r.Value.AuthProvider)
            )
            .ToArray();

        foreach (var (key, rule) in enabled)
        {
            // An auth-bearing wildcard allow is STILL a broad allow to the gateway — it is only exempt
            // from shadowing ITSELF (the self-pairing skip below). Do not filter it out here.
            if (!string.Equals(rule.Action?.Trim() ?? "allow", "allow", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Candidate hosts are the rule's wildcards (a broad allow is the dangerous shape), PLUS any
            // exact host that is the bare APEX of a gate's wildcard. The gateway's `*.s` also matches
            // the bare `s`, so an apex allow sits in front of a gate that already covers it — a shadow
            // an operator would not expect. A narrower sub-host in front of a broad deny is deliberately
            // NOT flagged: that is the intended carve-out idiom.
            var ruleHosts = ParseList(rule.Hosts);
            var gateApexes = gates
                .SelectMany(g => ParseList(g.Value.Hosts))
                .Where(IsWildcard)
                .Select(BareHost)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < ruleHosts.Count; i++)
            {
                var host = ruleHosts[i];
                if (!IsWildcard(host) && !gateApexes.Contains(host))
                {
                    continue;
                }

                foreach (var (gateKey, gate) in gates)
                {
                    if (gateKey == key || rule.Priority > gate.Priority)
                    {
                        continue;
                    }

                    if (ParseList(gate.Hosts).Any(gateHost => HostsOverlap(host, gateHost)))
                    {
                        // Names the entry by position, not value; the two rule keys are identifiers.
                        errors.Add(
                            $"{RuleSection}:{key}:Hosts — entry #{i + 1} overlaps rule '{gateKey}' at priority "
                                + $"{rule.Priority} <= {gate.Priority}, which would shadow that gate."
                        );
                    }
                }
            }
        }
    }

    /// <summary>
    /// Mirrors the gateway's create-time precedence check (<c>proxy_policy/precedence.rs</c>
    /// <c>validate_policy_precedence</c>, a hard 400) over the EFFECTIVE rule set — managed OAuth rules,
    /// runtime <c>predefined-*</c> key rules and configured rules merged together. Returns an empty list
    /// when the policy is acceptable.
    /// </summary>
    /// <remarks>
    /// Faithful to the gateway's definitions: a BROAD ALLOW is an allow rule whose hosts are empty, or
    /// contain <c>"*"</c> or any <c>"*."</c> pattern — carrying an <c>auth_provider</c> does NOT exempt
    /// it. A GATE is a deny rule or any rule bearing an <c>auth_provider</c>. A rule that is both cannot
    /// shadow itself, but is still compared against every other gate. An empty host set on either side
    /// means "any host", so the pair always overlaps. Only condition (a) — the hard error — is mirrored;
    /// the gateway's method/path-gap case (b) is a non-blocking warning there and is not reproduced.
    /// </remarks>
    public static IReadOnlyList<string> ValidateEffectivePrecedence(IReadOnlyList<SandboxNetworkRule> effectiveRules)
    {
        var errors = new List<string>();
        var gates = effectiveRules.Where(IsGate).ToArray();

        foreach (var broad in effectiveRules.Where(IsBroadAllow))
        {
            foreach (var gate in gates)
            {
                // A single rule can be BOTH a broad allow and a gate (e.g. the managed ADO rule:
                // *.dev.azure.com plus an auth provider). Its own auth still applies, so it cannot
                // shadow itself — but it is still checked against every OTHER gate.
                if (ReferenceEquals(broad, gate) || !BroadCouldMatchGate(broad, gate))
                {
                    continue;
                }

                if (broad.Priority <= gate.Priority)
                {
                    errors.Add(
                        $"broad-allow rule '{broad.Id}' (priority {broad.Priority}) shadows "
                            + $"{(gate.AuthProvider is not null ? "auth" : "deny")} rule '{gate.Id}' "
                            + $"(priority {gate.Priority}): a broad allow must have a strictly higher priority "
                            + "number than every deny/auth rule it could match (place it last)."
                    );
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// A port value no request can ever carry. An unparseable port token compiles to this rather than
    /// being dropped: dropping it could EMPTY the port list, and an empty dimension matches ANY port —
    /// silently widening the rule instead of failing closed.
    /// </summary>
    private const int UnmatchablePort = -1;

    /// <summary>
    /// Parses a comma-delimited port list. Validation rejects a non-numeric token up front; this keeps
    /// compilation total (no throw) for a consumer that bypassed validation, mapping the bad token to
    /// <see cref="UnmatchablePort"/> so the rule can never match instead of matching everything.
    /// </summary>
    private static IEnumerable<int> ParsePorts(string? value)
    {
        foreach (var token in ParseList(value))
        {
            yield return int.TryParse(token, out var port) ? port : UnmatchablePort;
        }
    }

    /// <summary>An allow rule whose host set is a catch-all: empty, <c>"*"</c>, or any <c>"*."</c> pattern.</summary>
    private static bool IsBroadAllow(SandboxNetworkRule rule) =>
        string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase)
        && (rule.Hosts.Count == 0 || rule.Hosts.Any(h => h == "*" || IsWildcard(h)));

    /// <summary>A deny rule, or any rule that injects a credential — what a shadowing allow would undermine.</summary>
    private static bool IsGate(SandboxNetworkRule rule) =>
        string.Equals(rule.Action, "deny", StringComparison.OrdinalIgnoreCase) || rule.AuthProvider is not null;

    /// <summary>Whether a broad allow could match a request the gate targets (an empty host set means any host).</summary>
    private static bool BroadCouldMatchGate(SandboxNetworkRule broad, SandboxNetworkRule gate) =>
        gate.Hosts.Count == 0
        || broad.Hosts.Count == 0
        || gate.Hosts.Any(gh => broad.Hosts.Any(bh => HostsOverlap(bh, gh)));

    /// <summary>Compiles the ENABLED configured rules into wire rules, ordered by ascending priority.</summary>
    public static IReadOnlyList<SandboxNetworkRule> CompileRules(SandboxGatewayOptions options) =>
        [
            .. (options.Network?.Rules ?? [])
                .Where(r => r.Value.Enabled)
                .OrderBy(r => r.Value.Priority)
                .ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                .Select(r => new SandboxNetworkRule(
                    id: r.Key,
                    action: (r.Value.Action ?? "allow").Trim().ToLowerInvariant(),
                    hosts: ParseList(r.Value.Hosts),
                    // Total (never throws): this runs on the auth-webhook hot path via FirstMatchingRule,
                    // where a programmatic consumer may have skipped Validate. See ParsePorts.
                    ports: [.. ParsePorts(r.Value.Ports)],
                    methods: ParseList(r.Value.Methods),
                    // Ordinal: MatchesPath is case-sensitive, so /Admin/* and /admin/* are both kept.
                    paths: ParseList(r.Value.Paths, StringComparer.Ordinal),
                    // The provider's OWN key, not the rule's spelling of it — see ResolveProviderKey.
                    // FAIL CLOSED when it does not resolve: dropping the reference would turn an
                    // authenticated rule into an ANONYMOUS allow. Validate rejects a dangling reference,
                    // but CompileRules also runs unvalidated on the auth-webhook hot path, so the
                    // unresolved spelling is emitted instead — the gateway denies an auth_provider it
                    // cannot find, and the webhook denies a provider id that does not match the rule.
                    // Only a BLANK reference may compile to null; that rule was never authenticated.
                    authProvider: string.IsNullOrWhiteSpace(r.Value.AuthProvider)
                        ? null
                        : ConfiguredProviderIdPrefix
                            + (ResolveProviderKey(options, r.Value.AuthProvider) ?? r.Value.AuthProvider.Trim()),
                    requiredScopes: ParseList(r.Value.RequiredScopes),
                    priority: r.Value.Priority
                )),
        ];

    /// <summary>
    /// Compiles the ENABLED configured providers into wire providers. A <c>headers</c> provider points
    /// at THIS app's auth webhook and is authenticated with the per-session secret (the header values
    /// stay here and are resolved on the callback); a <c>webhook</c> provider points at its own
    /// configured endpoint and carries its OWN secret — the app's session secret is never handed to an
    /// external endpoint.
    /// </summary>
    public static IReadOnlyList<SandboxAuthProvider> CompileProviders(
        SandboxGatewayOptions options,
        string? webhookBaseUrl,
        string sessionSecret
    ) =>
        [
            .. (options.AuthProviders ?? [])
                .Where(p => p.Value.Enabled)
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    var id = ConfiguredProviderIdPrefix + p.Key;
                    var isWebhook = string.Equals(p.Value.Type?.Trim(), "webhook", StringComparison.OrdinalIgnoreCase);
                    return new SandboxAuthProvider(
                        id: id,
                        type: "webhook", // the gateway's only native provider mechanism
                        endpoint: isWebhook ? p.Value.Endpoint! : $"{webhookBaseUrl}/api/auth/webhook/{id}",
                        gatewayAuth: isWebhook ? p.Value.GatewayAuth! : sessionSecret,
                        cacheTtlSeconds: p.Value.CacheTtlSeconds,
                        requiredScopes: ParseList(p.Value.RequiredScopes)
                    );
                }),
        ];

    /// <summary>True when <paramref name="providerId"/> is a CONFIGURED (appsettings) provider id.</summary>
    public static bool IsConfiguredProviderId(string? providerId) =>
        providerId is not null && providerId.StartsWith(ConfiguredProviderIdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Resolves the configured <c>headers</c> provider behind a <c>cfg-</c> wire id. Fails closed:
    /// returns <c>null</c> for an unknown id, a disabled entry, or a <c>webhook</c>-type entry (the
    /// gateway calls that provider's own endpoint, never this app).
    /// </summary>
    public static SandboxEgressAuthProviderOptions? ResolveHeadersProvider(
        SandboxGatewayOptions? options,
        string? wireProviderId
    )
    {
        if (
            options is null
            || string.IsNullOrEmpty(wireProviderId)
            || !wireProviderId.StartsWith(ConfiguredProviderIdPrefix, StringComparison.Ordinal)
        )
        {
            return null;
        }

        var key = wireProviderId[ConfiguredProviderIdPrefix.Length..];
        if (options.AuthProviders is null || !options.AuthProviders.TryGetValue(key, out var provider))
        {
            return null;
        }

        return provider.Enabled && string.Equals(provider.Type?.Trim(), "headers", StringComparison.OrdinalIgnoreCase)
            ? provider
            : null;
    }

    /// <summary>
    /// The first CONFIGURED rule (ascending priority) matching the destination, or <c>null</c> when
    /// none does — the gateway's own evaluation order, mirrored so the webhook's injection decision can
    /// never be broader than the rule that admitted the request.
    /// </summary>
    public static SandboxNetworkRule? FirstMatchingRule(
        SandboxGatewayOptions options,
        string? host,
        int port,
        string? method,
        string? path
    ) => CompileRules(options).FirstOrDefault(r => MatchesRule(r, host, port, method, path));

    /// <summary>
    /// AND across dimensions, OR within one, and an EMPTY dimension matches anything — the gateway's
    /// rule-matching semantics.
    /// </summary>
    private static bool MatchesRule(SandboxNetworkRule rule, string? host, int port, string? method, string? path) =>
        (rule.Hosts.Count == 0 || EgressHostMatcher.IsAllowed(rule.Hosts, host))
        && (rule.Ports.Count == 0 || rule.Ports.Contains(port))
        && (
            rule.Methods.Count == 0
            || rule.Methods.Any(m => m == "*" || string.Equals(m, method, StringComparison.OrdinalIgnoreCase))
        )
        && (rule.Paths.Count == 0 || rule.Paths.Any(p => MatchesPath(p, path)));

    /// <summary>The gateway path grammar: <c>*</c>, a trailing <c>/*</c> inclusive prefix, or an exact case-sensitive path.</summary>
    private static bool MatchesPath(string pattern, string? path)
    {
        if (pattern == "*")
        {
            return true;
        }

        if (path is null)
        {
            return false;
        }

        if (pattern.EndsWith("/*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            return path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);
        }

        return string.Equals(pattern, path, StringComparison.Ordinal);
    }

    private static bool IsValidPathPattern(string pattern) =>
        pattern == "*"
        || (
            pattern.StartsWith('/')
            && (
                !pattern.Contains('*', StringComparison.Ordinal)
                || (
                    pattern.EndsWith("/*", StringComparison.Ordinal)
                    && pattern.IndexOf('*', StringComparison.Ordinal) == pattern.Length - 1
                )
            )
        );

    /// <summary>Strips a single leading <c>*.</c> so a wildcard pattern can be compared as a concrete host.</summary>
    private static string BareHost(string host) => host.StartsWith("*.", StringComparison.Ordinal) ? host[2..] : host;

    private static bool IsWildcard(string host) => host.StartsWith("*.", StringComparison.Ordinal);

    /// <summary>
    /// True when the BARE suffix <paramref name="outer"/> covers the BARE suffix
    /// <paramref name="inner"/> — the same suffix, or a strictly deeper one (<c>eu.example.com</c> is
    /// covered by <c>example.com</c>). Both arguments are already stripped of a leading <c>*.</c>.
    /// </summary>
    private static bool SuffixCovers(string outer, string inner) =>
        string.Equals(inner, outer, StringComparison.OrdinalIgnoreCase)
        || inner.EndsWith("." + outer, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when every destination a RULE host pattern can reach also lies inside the provider's
    /// declared scope. This is a subset test between two PATTERNS, not a pattern-vs-literal match: an
    /// exact rule host is inside the scope when any provider entry matches it, but a WILDCARD rule host
    /// needs a provider WILDCARD that covers it — an exact provider host can never satisfy a wildcard
    /// rule host, because the rule would reach sibling hosts the provider never declared.
    /// </summary>
    private static bool IsWithinScope(IReadOnlyList<string> providerHosts, string ruleHost) =>
        IsWildcard(ruleHost)
            ? providerHosts.Any(p => IsWildcard(p) && SuffixCovers(BareHost(p), BareHost(ruleHost)))
            : EgressHostMatcher.IsAllowed(providerHosts, ruleHost);

    /// <summary>
    /// True when a wildcard pattern and an EXACT host can reach a common destination, under the
    /// GATEWAY's matching rule rather than this app's stricter one: <c>*.s</c> matches every subdomain
    /// of <c>s</c> AND the bare apex <c>s</c> itself. Shadow detection must use the gateway's (wider)
    /// semantics, or a configuration the gateway would treat as overlapping slips past startup.
    /// </summary>
    private static bool WildcardCoversExact(string wildcard, string exact) =>
        EgressHostMatcher.IsAllowed([wildcard], exact)
        || string.Equals(exact, BareHost(wildcard), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when two host PATTERNS can match a common destination. Two wildcards overlap when either
    /// suffix covers the other (<c>*.a.com</c> and <c>*.b.a.com</c> both match <c>x.b.a.com</c>) — a
    /// case a pattern-vs-bare-host comparison misses entirely, since neither literally matches the
    /// other's bare host. A wildcard and an exact host overlap under the gateway's apex rule
    /// (<see cref="WildcardCoversExact"/>).
    /// </summary>
    private static bool HostsOverlap(string a, string b)
    {
        // The gateway's host_patterns_overlap treats a bare "*" as overlapping every pattern.
        // ValidateHostPattern rejects "*" so configuration cannot reach this, but
        // ValidateEffectivePrecedence is public and takes already-compiled rules.
        if (a == "*" || b == "*")
        {
            return true;
        }

        return (IsWildcard(a), IsWildcard(b)) switch
        {
            (true, true) => SuffixCovers(BareHost(a), BareHost(b)) || SuffixCovers(BareHost(b), BareHost(a)),
            (true, false) => WildcardCoversExact(a, b),
            (false, true) => WildcardCoversExact(b, a),
            (false, false) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Resolves an operator-written <c>AuthProvider</c> reference to the provider's OWN configuration
    /// key. Configuration keys are case-insensitive, but the gateway resolves <c>auth_provider</c> by
    /// exact match, so the emitted rule must carry the key the provider is emitted under — not the
    /// spelling the rule happened to use.
    /// </summary>
    private static string? ResolveProviderKey(SandboxGatewayOptions options, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || options.AuthProviders is null)
        {
            return null;
        }

        var trimmed = reference.Trim();
        foreach (var key in options.AuthProviders.Keys)
        {
            if (string.Equals(key, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }
}
