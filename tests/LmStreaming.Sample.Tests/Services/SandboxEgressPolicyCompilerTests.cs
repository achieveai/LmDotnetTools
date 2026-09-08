using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the appsettings-driven sandbox egress policy: comma-list parsing, the fail-closed validator
/// (every rejection names the offending rule/provider/header id and field, never a value), and the
/// compiled wire shape (<c>cfg-</c>-prefixed provider ids, headers providers pointed at this app's
/// own webhook, webhook providers pointed at their configured endpoint with their own secret).
/// </summary>
public class SandboxEgressPolicyCompilerTests
{
    /// <summary>A canary that must never appear in a validation message or a create-request body.</summary>
    private const string SecretCanary = "sk-live-CANARY-NEVER-LEAKS";

    /// <summary>
    /// An EXTERNAL webhook provider's own gateway↔endpoint secret. Unlike <see cref="SecretCanary"/>
    /// this one legitimately travels on the create request (the gateway must present it when calling
    /// the external endpoint) — it is a canary only for validation messages.
    /// </summary>
    private const string ExternalSecretCanary = "gw-auth-CANARY-EXTERNAL";

    private static SandboxGatewayOptions Bind(Dictionary<string, string?> values)
    {
        var options = new SandboxGatewayOptions();
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().Bind(options);
        return options;
    }

    private static Dictionary<string, string?> HeadersProviderConfig(
        string key = "partner-headers",
        string hosts = "api.example.com"
    ) =>
        new()
        {
            [$"AuthProviders:{key}:Type"] = "headers",
            [$"AuthProviders:{key}:Hosts"] = hosts,
            [$"AuthProviders:{key}:CacheTtlSeconds"] = "30",
            [$"AuthProviders:{key}:Headers:X-Api-Key:Value"] = SecretCanary,
        };

    [Fact]
    public void Comma_lists_are_trimmed_deduped_and_empties_dropped()
    {
        var options = Bind(
            new Dictionary<string, string?>
            {
                ["Network:Rules:docs:Hosts"] = " docs.stripe.com , ,*.apple.com,docs.stripe.com ",
                ["Network:Rules:docs:Ports"] = "443, 443 ,",
                ["Network:Rules:docs:Methods"] = "get, GET , Head",
                ["Network:Rules:docs:Paths"] = "*",
            }
        );

        SandboxEgressPolicyCompiler.Validate(options).Should().BeEmpty();
        var rule = SandboxEgressPolicyCompiler.CompileRules(options).Should().ContainSingle().Subject;

        rule.Id.Should().Be("docs");
        rule.Action.Should().Be("allow");
        rule.Hosts.Should().Equal("docs.stripe.com", "*.apple.com");
        rule.Ports.Should().Equal(443);
        rule.Methods.Should().Equal("get", "Head"); // case-insensitive dedupe keeps the first spelling
        rule.Paths.Should().Equal("*");
        rule.Priority.Should().Be(500); // default
        rule.AuthProvider.Should().BeNull();
    }

    [Fact]
    public void Disabled_rule_and_provider_are_tombstones()
    {
        var values = HeadersProviderConfig();
        values["AuthProviders:partner-headers:Enabled"] = "false";
        values["Network:Rules:docs:Enabled"] = "false";
        values["Network:Rules:docs:Hosts"] = "docs.stripe.com";
        values["Network:Rules:docs:Ports"] = "443";
        values["Network:Rules:docs:Methods"] = "GET";
        var options = Bind(values);

        SandboxEgressPolicyCompiler.Validate(options).Should().BeEmpty();
        SandboxEgressPolicyCompiler.CompileRules(options).Should().BeEmpty();
        SandboxEgressPolicyCompiler.CompileProviders(options, "http://localhost:5000", "sess").Should().BeEmpty();
    }

    [Fact]
    public void Headers_provider_compiles_to_this_apps_webhook_with_the_session_secret()
    {
        var options = Bind(HeadersProviderConfig());

        SandboxEgressPolicyCompiler.Validate(options).Should().BeEmpty();
        var provider = SandboxEgressPolicyCompiler
            .CompileProviders(options, "http://localhost:5000", "the-session-secret")
            .Should()
            .ContainSingle()
            .Subject;

        provider.Id.Should().Be("cfg-partner-headers");
        provider.Type.Should().Be("webhook"); // the gateway's only native provider type
        provider.Endpoint.Should().Be("http://localhost:5000/api/auth/webhook/cfg-partner-headers");
        provider.GatewayAuth.Should().Be("the-session-secret");
        provider.CacheTtlSeconds.Should().Be(30);
        // The header VALUE is resolved by the webhook, never carried on the create request — proven by
        // Header_values_never_reach_the_sandbox_create_request over the serialized request body.
    }

    [Fact]
    public void Webhook_provider_compiles_to_its_own_endpoint_and_its_own_secret()
    {
        var options = Bind(
            new Dictionary<string, string?>
            {
                ["AuthProviders:partner-webhook:Type"] = "webhook",
                ["AuthProviders:partner-webhook:Hosts"] = "api.example.com",
                ["AuthProviders:partner-webhook:Endpoint"] = "https://auth.example.com/egress",
                ["AuthProviders:partner-webhook:GatewayAuth"] = ExternalSecretCanary,
                ["AuthProviders:partner-webhook:CacheTtlSeconds"] = "60",
                ["AuthProviders:partner-webhook:RequiredScopes"] = "read, write",
            }
        );

        SandboxEgressPolicyCompiler.Validate(options).Should().BeEmpty();
        var provider = SandboxEgressPolicyCompiler
            .CompileProviders(options, "http://localhost:5000", "the-session-secret")
            .Should()
            .ContainSingle()
            .Subject;

        provider.Id.Should().Be("cfg-partner-webhook");
        provider.Endpoint.Should().Be("https://auth.example.com/egress");
        // SECURITY: an external webhook must NEVER be handed the app's per-session secret.
        provider.GatewayAuth.Should().Be(ExternalSecretCanary).And.NotBe("the-session-secret");
        provider.CacheTtlSeconds.Should().Be(60);
        provider.RequiredScopes.Should().Equal("read", "write");
    }

    [Fact]
    public void Authenticated_rule_references_the_prefixed_provider_id()
    {
        var values = HeadersProviderConfig();
        values["Network:Rules:partner-api:Hosts"] = "api.example.com";
        values["Network:Rules:partner-api:Ports"] = "443";
        values["Network:Rules:partner-api:Methods"] = "GET,POST";
        values["Network:Rules:partner-api:Paths"] = "/v1/*";
        values["Network:Rules:partner-api:AuthProvider"] = "partner-headers";
        values["Network:Rules:partner-api:Priority"] = "100";
        var options = Bind(values);

        SandboxEgressPolicyCompiler.Validate(options).Should().BeEmpty();
        var rule = SandboxEgressPolicyCompiler.CompileRules(options).Should().ContainSingle().Subject;

        rule.AuthProvider.Should().Be("cfg-partner-headers");
        rule.Paths.Should().Equal("/v1/*");
        rule.Priority.Should().Be(100);
    }

    [Theory]
    [InlineData("partner-headers")]
    [InlineData("PARTNER-headers")]
    [InlineData("Partner-Headers")]
    public void Rule_auth_provider_reference_resolves_to_the_configured_provider_key(string reference)
    {
        // Configuration keys are case-insensitive, but the gateway looks a provider id up by EXACT byte
        // equality. Emitting the operator's spelling would validate clean and then deny every request,
        // so the compiled rule must carry the provider's own dictionary key.
        var values = HeadersProviderConfig();
        values["Network:Rules:partner-api:Hosts"] = "api.example.com";
        values["Network:Rules:partner-api:Ports"] = "443";
        values["Network:Rules:partner-api:Methods"] = "GET";
        values["Network:Rules:partner-api:AuthProvider"] = reference;
        values["Network:Rules:partner-api:Priority"] = "100";
        var options = Bind(values);

        SandboxEgressPolicyCompiler.Validate(options).Should().BeEmpty();

        var rule = SandboxEgressPolicyCompiler.CompileRules(options).Should().ContainSingle().Subject;
        var provider = SandboxEgressPolicyCompiler
            .CompileProviders(options, "http://localhost:5000", "sess")
            .Should()
            .ContainSingle()
            .Subject;

        rule.AuthProvider.Should().Be("cfg-partner-headers");
        rule.AuthProvider.Should().Be(provider.Id, "the gateway resolves auth_provider by exact match");
    }

    [Fact]
    public void Rule_referencing_a_missing_provider_compiles_to_an_unresolvable_gate_not_an_open_allow()
    {
        // N1. Validate rejects a dangling AuthProvider, so this is unreachable on the shipped path --
        // but CompileRules is also reached from the auth-webhook via FirstMatchingRule, where a
        // programmatic consumer may never have called Validate. Dropping the reference there would
        // downgrade an authenticated rule into an ANONYMOUS allow: fail-open. Emitting the unresolved
        // spelling instead keeps it fail-closed, because the gateway denies an auth_provider it cannot
        // find and the webhook denies a provider id that does not match the rule.
        var options = Bind(
            new Dictionary<string, string?>
            {
                ["Network:Rules:partner-api:Hosts"] = "api.example.com",
                ["Network:Rules:partner-api:Ports"] = "443",
                ["Network:Rules:partner-api:Methods"] = "GET",
                ["Network:Rules:partner-api:AuthProvider"] = "ghost",
                ["Network:Rules:partner-api:Priority"] = "100",
            }
        );

        SandboxEgressPolicyCompiler.Validate(options).Should().NotBeEmpty("the reference does not resolve");

        var rule = SandboxEgressPolicyCompiler.CompileRules(options).Should().ContainSingle().Subject;
        rule.AuthProvider.Should().Be("cfg-ghost").And.NotBeNull();

        // Whitespace around a dangling reference must not turn into a distinct unresolvable id.
        options.Network.Rules["partner-api"].AuthProvider = "  ghost  ";
        SandboxEgressPolicyCompiler.CompileRules(options).Single().AuthProvider.Should().Be("cfg-ghost");

        // Only a BLANK reference may compile to null -- that rule was never authenticated to begin with.
        options.Network.Rules["partner-api"].AuthProvider = "   ";
        SandboxEgressPolicyCompiler.CompileRules(options).Single().AuthProvider.Should().BeNull();
    }

    [Fact]
    public void Rules_compile_in_ascending_priority_order()
    {
        var options = Bind(
            new Dictionary<string, string?>
            {
                ["Network:Rules:late:Hosts"] = "a.example.com",
                ["Network:Rules:late:Ports"] = "443",
                ["Network:Rules:late:Methods"] = "GET",
                ["Network:Rules:late:Priority"] = "900",
                ["Network:Rules:early:Hosts"] = "b.example.com",
                ["Network:Rules:early:Ports"] = "443",
                ["Network:Rules:early:Methods"] = "GET",
                ["Network:Rules:early:Priority"] = "50",
            }
        );

        SandboxEgressPolicyCompiler.CompileRules(options).Select(r => r.Id).Should().Equal("early", "late");
    }

    public static TheoryData<string, string[], Dictionary<string, string?>> InvalidConfigurations()
    {
        static Dictionary<string, string?> Rule(params (string Key, string Value)[] overrides)
        {
            var values = new Dictionary<string, string?>
            {
                ["Network:Rules:bad-rule:Hosts"] = "api.example.com",
                ["Network:Rules:bad-rule:Ports"] = "443",
                ["Network:Rules:bad-rule:Methods"] = "GET",
            };
            foreach (var (key, value) in overrides)
            {
                values[$"Network:Rules:bad-rule:{key}"] = value;
            }

            return values;
        }

        static Dictionary<string, string?> Provider(string type, params (string Key, string Value)[] overrides)
        {
            var values = new Dictionary<string, string?>
            {
                ["AuthProviders:bad-provider:Type"] = type,
                ["AuthProviders:bad-provider:Hosts"] = "api.example.com",
            };
            if (type == "headers")
            {
                values["AuthProviders:bad-provider:Headers:X-Api-Key:Value"] = SecretCanary;
            }
            else
            {
                values["AuthProviders:bad-provider:Endpoint"] = "https://auth.example.com/egress";
                values["AuthProviders:bad-provider:GatewayAuth"] = ExternalSecretCanary;
            }

            foreach (var (key, value) in overrides)
            {
                values[$"AuthProviders:bad-provider:{key}"] = value;
            }

            return values;
        }

        return new TheoryData<string, string[], Dictionary<string, string?>>
        {
            { "invalid host", ["bad-rule", "Hosts"], Rule(("Hosts", "https://api.example.com")) },
            { "bare wildcard host", ["bad-rule", "Hosts"], Rule(("Hosts", "*")) },
            { "loopback host", ["bad-rule", "Hosts"], Rule(("Hosts", "localhost")) },
            { "empty hosts", ["bad-rule", "Hosts"], Rule(("Hosts", " , ")) },
            { "empty ports", ["bad-rule", "Ports"], Rule(("Ports", "")) },
            { "non-numeric port", ["bad-rule", "Ports"], Rule(("Ports", "https")) },
            { "port out of range", ["bad-rule", "Ports"], Rule(("Ports", "70000")) },
            { "port zero", ["bad-rule", "Ports"], Rule(("Ports", "0")) },
            { "empty methods", ["bad-rule", "Methods"], Rule(("Methods", "")) },
            { "unknown method", ["bad-rule", "Methods"], Rule(("Methods", "CONNECT")) },
            { "path without leading slash", ["bad-rule", "Paths"], Rule(("Paths", "v1/*")) },
            { "path with interior wildcard", ["bad-rule", "Paths"], Rule(("Paths", "/v1/*/items")) },
            { "unknown action", ["bad-rule", "Action"], Rule(("Action", "reject")) },
            { "unknown auth provider", ["bad-rule", "AuthProvider"], Rule(("AuthProvider", "nope")) },
            {
                "reserved rule id",
                ["predefined-x"],
                new Dictionary<string, string?>
                {
                    ["Network:Rules:predefined-x:Hosts"] = "api.example.com",
                    ["Network:Rules:predefined-x:Ports"] = "443",
                    ["Network:Rules:predefined-x:Methods"] = "GET",
                }
            },
            { "unknown provider type", ["bad-provider", "Type"], Provider("magic") },
            { "provider without hosts", ["bad-provider", "Hosts"], Provider("headers", ("Hosts", "")) },
            { "provider with invalid host", ["bad-provider", "Hosts"], Provider("headers", ("Hosts", "localhost")) },
            {
                "negative cache ttl",
                ["bad-provider", "CacheTtlSeconds"],
                Provider("headers", ("CacheTtlSeconds", "-1"))
            },
            {
                "headers provider without enabled headers",
                ["bad-provider", "Headers"],
                Provider("headers", ("Headers:X-Api-Key:Enabled", "false"))
            },
            {
                "invalid header name",
                ["bad-provider", "Headers", "Content-Length"],
                Provider("headers", ("Headers:Content-Length:Value", "12"))
            },
            {
                "empty header value",
                ["bad-provider", "Headers", "X-Empty"],
                Provider("headers", ("Headers:X-Empty:Value", ""))
            },
            {
                "webhook provider without gateway auth",
                ["bad-provider", "GatewayAuth"],
                Provider("webhook", ("GatewayAuth", ""))
            },
            {
                "webhook endpoint not absolute",
                ["bad-provider", "Endpoint"],
                Provider("webhook", ("Endpoint", "/egress"))
            },
            {
                "webhook endpoint cleartext non-loopback",
                ["bad-provider", "Endpoint"],
                Provider("webhook", ("Endpoint", "http://auth.example.com/egress"))
            },
            {
                "webhook endpoint with userinfo",
                ["bad-provider", "Endpoint"],
                Provider("webhook", ("Endpoint", "https://user:pw@auth.example.com/egress"))
            },
            {
                "webhook endpoint with fragment",
                ["bad-provider", "Endpoint"],
                Provider("webhook", ("Endpoint", "https://auth.example.com/egress#frag"))
            },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidConfigurations))]
    public void Invalid_configuration_fails_closed_naming_the_offender(
        string because,
        string[] expectedFragments,
        Dictionary<string, string?> values
    )
    {
        var errors = SandboxEgressPolicyCompiler.Validate(Bind(values));

        errors.Should().NotBeEmpty(because);
        var joined = string.Join(" | ", errors);
        foreach (var fragment in expectedFragments)
        {
            joined.Should().Contain(fragment, because);
        }

        // A validation message is operator-facing output: it must name the field, never the value —
        // neither an injected header value nor an external endpoint's shared secret.
        joined.Should().NotContain(SecretCanary).And.NotContain(ExternalSecretCanary);
    }

    /// <summary>
    /// Host-scope containment is a SUBSET test between two patterns, not a pattern-vs-literal match.
    /// A wildcard rule host is inside a scope only when some provider entry is a wildcard covering it —
    /// an EXACT provider host can never satisfy a wildcard rule host, because the rule would reach
    /// siblings the provider never declared.
    /// </summary>
    [Theory]
    // Identical wildcard scopes: the rule reaches exactly what the provider declares.
    [InlineData("*.example.com", "*.example.com", true)]
    // Nested wildcard: *.eu.example.com is a strict subset of *.example.com.
    [InlineData("*.eu.example.com", "*.example.com", true)]
    // An exact host inside a wildcard scope.
    [InlineData("api.example.com", "*.example.com", true)]
    // An exact host equal to the provider's exact host.
    [InlineData("api.example.com", "api.example.com", true)]
    // A wildcard rule host can NEVER be covered by an exact provider host.
    [InlineData("*.example.com", "api.example.com", false)]
    // Widening: *.example.com is NOT inside *.eu.example.com.
    [InlineData("*.example.com", "*.eu.example.com", false)]
    // APEX, deliberately rejected: the gateway would route example.com into the *.example.com rule, but
    // this app's credential webhook matches strictly and would deny it. Rejecting at startup keeps the
    // two ends consistent instead of letting a config validate and then silently fail at runtime.
    [InlineData("example.com", "*.example.com", false)]
    // Unrelated host.
    [InlineData("api.other.org", "*.example.com", false)]
    public void Authenticated_rule_host_must_be_a_subset_of_its_providers_scope(
        string ruleHost,
        string providerHosts,
        bool expectedValid
    )
    {
        var values = HeadersProviderConfig(hosts: providerHosts);
        values["Network:Rules:partner-api:Hosts"] = ruleHost;
        values["Network:Rules:partner-api:Ports"] = "443";
        values["Network:Rules:partner-api:Methods"] = "GET";
        values["Network:Rules:partner-api:AuthProvider"] = "partner-headers";
        values["Network:Rules:partner-api:Priority"] = "100";

        var errors = SandboxEgressPolicyCompiler.Validate(Bind(values));

        if (expectedValid)
        {
            errors.Should().BeEmpty();
        }
        else
        {
            string.Join(" | ", errors).Should().Contain("partner-api").And.Contain("Hosts");
        }
    }

    /// <summary>
    /// Shadow detection compares two PATTERNS, so two wildcards that cover a common destination must
    /// be seen as overlapping even though neither literally matches the other's bare host. Missing
    /// this leaves the configuration to be hard-rejected by the gateway at sandbox creation instead of
    /// at startup.
    /// </summary>
    [Theory]
    // Identical wildcards at the same priority: the allow is evaluated first and opens the denied host.
    [InlineData("*.a.com", 100, "*.a.com", 100, true)]
    // Nested: the broad allow swallows the narrower deny.
    [InlineData("*.a.com", 100, "*.b.a.com", 100, true)]
    // Nested the other way: the narrow allow still shadows the broad deny for its own subtree.
    [InlineData("*.b.a.com", 100, "*.a.com", 100, true)]
    // Behind the gate: first-match-wins by ascending priority means the deny wins, so this is fine.
    [InlineData("*.a.com", 500, "*.a.com", 100, false)]
    // Disjoint wildcards never overlap.
    [InlineData("*.a.com", 100, "*.c.com", 100, false)]
    // APEX: the gateway lets *.a.com match the bare a.com, so a broad allow does shadow an apex gate
    // even though this app's strict matcher would not pair them.
    [InlineData("*.a.com", 100, "a.com", 100, true)]
    // APEX, other direction: an allow on the apex sits in front of a wildcard gate that also covers it.
    [InlineData("a.com", 100, "*.a.com", 100, true)]
    // Behind the gate, so the deny still wins.
    [InlineData("a.com", 500, "*.a.com", 100, false)]
    // NOT a shadow: a narrow sub-host carve-out in front of a broad deny is the intended idiom.
    [InlineData("api.a.com", 100, "*.a.com", 200, false)]
    public void Wildcard_allow_overlapping_a_gate_is_detected_pattern_to_pattern(
        string allowHost,
        int allowPriority,
        string denyHost,
        int denyPriority,
        bool expectShadowError
    )
    {
        var options = Bind(
            new Dictionary<string, string?>
            {
                ["Network:Rules:broad:Hosts"] = allowHost,
                ["Network:Rules:broad:Ports"] = "443",
                ["Network:Rules:broad:Methods"] = "GET",
                ["Network:Rules:broad:Priority"] = allowPriority.ToString(),
                ["Network:Rules:blocked:Hosts"] = denyHost,
                ["Network:Rules:blocked:Ports"] = "443",
                ["Network:Rules:blocked:Methods"] = "*",
                ["Network:Rules:blocked:Action"] = "deny",
                ["Network:Rules:blocked:Priority"] = denyPriority.ToString(),
            }
        );

        var joined = string.Join(" | ", SandboxEgressPolicyCompiler.Validate(options));

        if (expectShadowError)
        {
            joined.Should().Contain("broad").And.Contain("blocked");
        }
        else
        {
            joined.Should().BeEmpty();
        }
    }

    [Fact]
    public void Authenticated_rule_must_stay_on_443_and_inside_its_providers_host_scope()
    {
        var offPort = HeadersProviderConfig();
        offPort["Network:Rules:partner-api:Hosts"] = "api.example.com";
        offPort["Network:Rules:partner-api:Ports"] = "8443";
        offPort["Network:Rules:partner-api:Methods"] = "GET";
        offPort["Network:Rules:partner-api:AuthProvider"] = "partner-headers";
        string.Join(" | ", SandboxEgressPolicyCompiler.Validate(Bind(offPort)))
            .Should()
            .Contain("partner-api")
            .And.Contain("Ports");

        var offHost = HeadersProviderConfig();
        offHost["Network:Rules:partner-api:Hosts"] = "other.example.org";
        offHost["Network:Rules:partner-api:Ports"] = "443";
        offHost["Network:Rules:partner-api:Methods"] = "GET";
        offHost["Network:Rules:partner-api:AuthProvider"] = "partner-headers";
        string.Join(" | ", SandboxEgressPolicyCompiler.Validate(Bind(offHost)))
            .Should()
            .Contain("partner-api")
            .And.Contain("Hosts");
    }

    [Fact]
    public void Rule_referencing_a_disabled_provider_fails_closed()
    {
        var values = HeadersProviderConfig();
        values["AuthProviders:partner-headers:Enabled"] = "false";
        values["Network:Rules:partner-api:Hosts"] = "api.example.com";
        values["Network:Rules:partner-api:Ports"] = "443";
        values["Network:Rules:partner-api:Methods"] = "GET";
        values["Network:Rules:partner-api:AuthProvider"] = "partner-headers";

        string.Join(" | ", SandboxEgressPolicyCompiler.Validate(Bind(values)))
            .Should()
            .Contain("partner-api")
            .And.Contain("AuthProvider");
    }

    [Theory]
    [InlineData("github")]
    [InlineData("github-auth")]
    [InlineData("m365")]
    [InlineData("predefined-e1")]
    public void Provider_key_colliding_with_a_managed_identity_fails_closed(string key)
    {
        var options = Bind(HeadersProviderConfig(key));

        string.Join(" | ", SandboxEgressPolicyCompiler.Validate(options)).Should().Contain(key);
    }

    /// <summary>
    /// Config-only validation must NOT reason about managed/predefined gates: those exist only in the
    /// merged effective rule set, and a config rule named after a managed rule REPLACES it. Shadowing
    /// against them is checked post-merge by <c>ValidateEffectivePrecedence</c> (see the registry
    /// tests), so a wildcard that merely resembles a managed host is fine here.
    /// </summary>
    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    public void Config_only_validation_does_not_judge_managed_gates(int priority)
    {
        var options = Bind(
            new Dictionary<string, string?>
            {
                ["Network:Rules:broad:Hosts"] = "*.github.com",
                ["Network:Rules:broad:Ports"] = "443",
                ["Network:Rules:broad:Methods"] = "GET",
                ["Network:Rules:broad:Priority"] = priority.ToString(),
            }
        );

        SandboxEgressPolicyCompiler.Validate(options).Should().BeEmpty();
    }

    /// <summary>
    /// Mirrors the gateway's create-time precedence check (<c>proxy_policy/precedence.rs</c>) over the
    /// MERGED rule set: a broad allow (empty hosts, <c>*</c>, or any <c>*.</c> host) at or in front of
    /// any gate (deny, or auth-provider-bearing) it could match is a hard error. A rule that is both a
    /// broad allow and a gate cannot shadow itself.
    /// </summary>
    [Fact]
    public void Effective_precedence_mirrors_the_gateways_broad_allow_rules()
    {
        static AchieveAi.LmDotnetTools.Sandbox.SandboxNetworkRule Rule(
            string id,
            string action,
            string[] hosts,
            int priority,
            string? auth = null
        ) =>
            new(id, action, hosts: hosts, ports: [443], methods: [], paths: [], authProvider: auth, priority: priority);

        // Broad allow in front of an auth gate it overlaps.
        SandboxEgressPolicyCompiler
            .ValidateEffectivePrecedence([
                Rule("broad", "allow", ["*.github.com"], 50),
                Rule("github", "allow", ["api.github.com"], 100, "github-auth"),
            ])
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("broad")
            .And.Contain("github");

        // Same pair, broad behind the gate.
        SandboxEgressPolicyCompiler
            .ValidateEffectivePrecedence([
                Rule("broad", "allow", ["*.github.com"], 500),
                Rule("github", "allow", ["api.github.com"], 100, "github-auth"),
            ])
            .Should()
            .BeEmpty();

        // An auth-bearing wildcard allow is STILL a broad allow to the gateway; it just cannot shadow
        // itself. Here it shadows a different gate.
        SandboxEgressPolicyCompiler
            .ValidateEffectivePrecedence([
                Rule("ado", "allow", ["*.x.com"], 50, "ado-auth"),
                Rule("blocked", "deny", ["*.x.com"], 100),
            ])
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("ado");

        // N2. The gateway's host_patterns_overlap treats a bare "*" as overlapping ANY pattern.
        // ValidateHostPattern rejects "*" so config can never reach here, but this method is public
        // and a programmatic caller can hand us one — it must not silently read as "no overlap".
        SandboxEgressPolicyCompiler
            .ValidateEffectivePrecedence([Rule("star", "allow", ["*"], 100), Rule("blocked", "deny", ["x.com"], 100)])
            .Should()
            .ContainSingle();

        SandboxEgressPolicyCompiler
            .ValidateEffectivePrecedence([
                Rule("broad", "allow", ["*.a.com"], 100),
                Rule("blocked", "deny", ["*"], 100),
            ])
            .Should()
            .ContainSingle();

        // Self-pairing only: an auth-gated wildcard allow alone is fine.
        SandboxEgressPolicyCompiler
            .ValidateEffectivePrecedence([Rule("ado", "allow", ["*.dev.azure.com"], 100, "ado-auth")])
            .Should()
            .BeEmpty();

        // Empty hosts is the catch-all: it matches every gate regardless of the gate's hosts.
        SandboxEgressPolicyCompiler
            .ValidateEffectivePrecedence([
                Rule("catch-all", "allow", [], 50),
                Rule("m365", "allow", ["graph.microsoft.com"], 100, "m365-auth"),
            ])
            .Should()
            .ContainSingle();

        // A gate with NO hosts is reachable by any broad allow.
        SandboxEgressPolicyCompiler
            .ValidateEffectivePrecedence([Rule("broad", "allow", ["*.a.com"], 50), Rule("deny-all", "deny", [], 100)])
            .Should()
            .ContainSingle();

        // Disjoint hosts never conflict.
        SandboxEgressPolicyCompiler
            .ValidateEffectivePrecedence([
                Rule("broad", "allow", ["*.a.com"], 50),
                Rule("blocked", "deny", ["*.c.com"], 100),
            ])
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void Wildcard_allow_shadowing_a_config_gate_rule_fails_closed()
    {
        var values = HeadersProviderConfig(hosts: "api.example.com");
        values["Network:Rules:partner-api:Hosts"] = "api.example.com";
        values["Network:Rules:partner-api:Ports"] = "443";
        values["Network:Rules:partner-api:Methods"] = "GET";
        values["Network:Rules:partner-api:AuthProvider"] = "partner-headers";
        values["Network:Rules:partner-api:Priority"] = "200";
        values["Network:Rules:broad:Hosts"] = "*.example.com";
        values["Network:Rules:broad:Ports"] = "443";
        values["Network:Rules:broad:Methods"] = "GET";
        values["Network:Rules:broad:Priority"] = "150";

        string.Join(" | ", SandboxEgressPolicyCompiler.Validate(Bind(values)))
            .Should()
            .Contain("broad")
            .And.Contain("partner-api");
    }

    [Fact]
    public void Errors_are_aggregated_not_thrown_one_at_a_time()
    {
        var options = Bind(
            new Dictionary<string, string?>
            {
                ["Network:Rules:a:Hosts"] = "localhost",
                ["Network:Rules:a:Ports"] = "0",
                ["Network:Rules:a:Methods"] = "CONNECT",
            }
        );

        SandboxEgressPolicyCompiler.Validate(options).Should().HaveCountGreaterThanOrEqualTo(3);
    }
}
