using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// Read-only egress (a judge thread's sandbox) may only GET and HEAD ADO. Everything else in the policy, and the
/// whole policy of a normal (lane) sandbox, must stay exactly as it is.
/// </summary>
public sealed class SandboxReadOnlyEgressPolicyTests
{
    [Fact]
    public void ReadOnly_narrows_only_the_ado_rule_to_GET_and_HEAD_and_keeps_its_credential()
    {
        var (auth, options) = HostLikePolicy();

        var normal = SandboxSessionRegistry.BuildAuthPolicy(auth, options, "test-secret").Network!;
        var readOnly = SandboxSessionRegistry
            .BuildAuthPolicy(auth, options, "test-secret", readOnlyEgress: true)
            .Network!;

        // The lane regression pin: a normal sandbox's ado rule still allows every method.
        var normalAdo = normal.Single(rule => rule.Id == "ado");
        normalAdo.Methods.Should().BeEmpty();
        normalAdo.AuthProvider.Should().Be("ado-auth");

        var readOnlyAdo = readOnly.Single(rule => rule.Id == "ado");
        readOnlyAdo.Methods.Should().Equal("GET", "HEAD");
        Shape(readOnlyAdo, withMethods: false).Should().Be(Shape(normalAdo, withMethods: false));

        // github, github-egress, public-docs and agency_proxy are byte-for-byte the normal rules.
        readOnly
            .Where(rule => rule.Id != "ado")
            .Select(rule => Shape(rule))
            .Should()
            .Equal(normal.Where(rule => rule.Id != "ado").Select(rule => Shape(rule)));
        readOnly.Select(rule => rule.Id).Should().Contain(["github", "github-egress", "public-docs", "agency_proxy"]);
    }

    [Theory]
    [InlineData("dev.azure.com", "GET")]
    [InlineData("dev.azure.com", "HEAD")]
    [InlineData("dev.azure.com", "POST")]
    [InlineData("dev.azure.com", "PUT")]
    [InlineData("dev.azure.com", "PATCH")]
    [InlineData("dev.azure.com", "DELETE")]
    [InlineData("x.visualstudio.com", "GET")]
    [InlineData("x.visualstudio.com", "HEAD")]
    [InlineData("x.visualstudio.com", "POST")]
    [InlineData("x.visualstudio.com", "PUT")]
    [InlineData("x.visualstudio.com", "PATCH")]
    [InlineData("x.visualstudio.com", "DELETE")]
    public void First_matching_rule_lets_a_read_only_sandbox_only_GET_and_HEAD_ADO(string host, string method)
    {
        var (auth, options) = HostLikePolicy();
        var (_, normal) = SandboxSessionRegistry.BuildAuthPolicy(auth, options, "test-secret");
        var (_, readOnly) = SandboxSessionRegistry.BuildAuthPolicy(auth, options, "test-secret", readOnlyEgress: true);

        FirstMatch(readOnly!, host, 443, method).Should().Be(method is "GET" or "HEAD" ? "allow" : "deny");
        FirstMatch(normal!, host, 443, method).Should().Be("allow");
    }

    [Fact]
    public void ReadOnly_narrows_configured_rules_that_replace_or_add_ADO_egress_and_precedence_still_passes()
    {
        var auth = new AuthOptions();
        auth.Ado.ClientId = "ado-client";
        var options = new SandboxGatewayOptions();
        // Replaces the managed ado rule (any method, no credential).
        options.Network.Rules["ado"] = new()
        {
            Hosts = "dev.azure.com,*.visualstudio.com",
            Ports = "443",
            Methods = "*",
            Priority = 100,
        };
        options.Network.Rules["ado-mixed"] = new()
        {
            Hosts = "dev.azure.com",
            Ports = "443",
            Methods = "get,POST",
            Priority = 200,
        };
        options.Network.Rules["ado-writes"] = new()
        {
            Hosts = "*.dev.azure.com",
            Ports = "443",
            Methods = "POST,PATCH",
            Priority = 300,
        };

        var (_, normal) = SandboxSessionRegistry.BuildAuthPolicy(auth, options, "test-secret");
        var (_, readOnly) = SandboxSessionRegistry.BuildAuthPolicy(auth, options, "test-secret", readOnlyEgress: true);

        normal!.Single(rule => rule.Id == "ado").Methods.Should().Equal("*");
        readOnly!.Single(rule => rule.Id == "ado").Methods.Should().Equal("GET", "HEAD");
        readOnly!.Single(rule => rule.Id == "ado").AuthProvider.Should().BeNull();
        readOnly!.Single(rule => rule.Id == "ado-mixed").Methods.Should().Equal("GET");
        readOnly!.Should().NotContain(rule => rule.Id == "ado-writes", "a rule with no read method left is dropped");
        SandboxEgressPolicyCompiler.ValidateEffectivePrecedence(readOnly!).Should().BeEmpty();
    }

    /// <summary>The live host's rule shape: managed github and ado, a GET-only docs rule, and an any-method proxy.</summary>
    private static (AuthOptions Auth, SandboxGatewayOptions Options) HostLikePolicy()
    {
        var auth = new AuthOptions();
        auth.Github.ClientId = "github-client";
        auth.Ado.ClientId = "ado-client";
        var options = new SandboxGatewayOptions();
        options.Network.Rules["public-docs"] = new()
        {
            Hosts = "learn.microsoft.com",
            Ports = "443",
            Methods = "GET",
        };
        options.Network.Rules["agency_proxy"] = new()
        {
            Hosts = "agency.example",
            Ports = "8765",
            Methods = "*",
            Priority = 50,
        };
        return (auth, options);
    }

    /// <summary>
    /// The gateway's evaluation: ascending priority, first rule whose host, port and method all match decides;
    /// no match is deny. Empty methods or <c>*</c> match any method, case-insensitively.
    /// </summary>
    private static string FirstMatch(IReadOnlyList<SandboxNetworkRule> rules, string host, int port, string method) =>
        rules
            .OrderBy(rule => rule.Priority)
            .FirstOrDefault(rule =>
                (rule.Hosts.Count == 0 || EgressHostMatcher.IsAllowed(rule.Hosts, host))
                && (rule.Ports.Count == 0 || rule.Ports.Contains(port))
                && (
                    rule.Methods.Count == 0
                    || rule.Methods.Any(m => m == "*" || string.Equals(m, method, StringComparison.OrdinalIgnoreCase))
                )
            )
            ?.Action
        ?? "deny";

    private static string Shape(SandboxNetworkRule rule, bool withMethods = true) =>
        string.Join(
            " | ",
            rule.Id,
            rule.Action,
            string.Join(",", rule.Hosts),
            string.Join(",", rule.Ports),
            withMethods ? string.Join(",", rule.Methods) : "-",
            string.Join(",", rule.Paths),
            rule.AuthProvider ?? "(none)",
            string.Join(",", rule.RequiredScopes),
            rule.Priority
        );
}
