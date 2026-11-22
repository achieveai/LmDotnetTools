using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// Read-only egress (a judge thread's sandbox) may only GET and HEAD ADO. Everything else in the policy, and the
/// whole policy of a normal (lane) sandbox, must stay exactly as it is.
/// </summary>
public sealed class SandboxReadOnlyEgressPolicyTests(ITestOutputHelper output)
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
        // One org's host: not an ADO host verbatim, but inside *.visualstudio.com.
        options.Network.Rules["ado-org"] = new()
        {
            Hosts = "contoso.visualstudio.com",
            Ports = "443",
            Methods = "GET,POST",
            Priority = 400,
        };
        options.Network.Rules["ado-orgs"] = new()
        {
            Hosts = "*.visualstudio.com",
            Ports = "443",
            Methods = "HEAD,PUT",
            Priority = 500,
        };
        // ADO and a non-ADO host in one rule. A rule that reaches ADO is narrowed as a whole, so example.org loses
        // its writes too: one rule has one method list, and splitting it is not the gateway's rule model.
        options.Network.Rules["ado-and-docs"] = new()
        {
            Hosts = "vssps.dev.azure.com,example.org",
            Ports = "443",
            Methods = "*",
            Priority = 600,
        };

        var (_, normal) = SandboxSessionRegistry.BuildAuthPolicy(auth, options, "test-secret");
        var (_, readOnly) = SandboxSessionRegistry.BuildAuthPolicy(auth, options, "test-secret", readOnlyEgress: true);

        normal!.Single(rule => rule.Id == "ado").Methods.Should().Equal("*");
        normal!.Single(rule => rule.Id == "ado-and-docs").Methods.Should().Equal("*");
        readOnly!.Single(rule => rule.Id == "ado").Methods.Should().Equal("GET", "HEAD");
        readOnly!.Single(rule => rule.Id == "ado").AuthProvider.Should().BeNull();
        readOnly!.Single(rule => rule.Id == "ado-mixed").Methods.Should().Equal("GET");
        readOnly!.Should().NotContain(rule => rule.Id == "ado-writes", "a rule with no read method left is dropped");
        readOnly!.Single(rule => rule.Id == "ado-org").Methods.Should().Equal("GET");
        readOnly!.Single(rule => rule.Id == "ado-orgs").Methods.Should().Equal("HEAD");
        var mixed = readOnly!.Single(rule => rule.Id == "ado-and-docs");
        mixed.Methods.Should().Equal("GET", "HEAD");
        mixed.Hosts.Should().Equal("vssps.dev.azure.com", "example.org");
        SandboxEgressPolicyCompiler.ValidateEffectivePrecedence(readOnly!).Should().BeEmpty();
    }

    private static readonly string[] PrepareReads =
    [
        "/contoso/Weve%20DA/_git/Nova/git-upload-pack",
        "/Contoso/_apis/wit/workitemsbatch",
        "/Contoso/p-id/_apis/git/repositories/r-id/pullrequestquery",
    ];

    [Fact]
    public void ReadOnly_post_paths_add_one_exact_path_POST_rule_beside_the_ado_rule_with_its_credential()
    {
        var (auth, options) = HostLikePolicy();

        var (_, readOnly) = SandboxSessionRegistry.BuildAuthPolicy(
            auth,
            options,
            "test-secret",
            readOnlyEgress: true,
            readOnlyAdoPostPaths: PrepareReads
        );
        var (_, withoutPaths) = SandboxSessionRegistry.BuildAuthPolicy(
            auth,
            options,
            "test-secret",
            readOnlyEgress: true
        );

        var ado = readOnly!.Single(rule => rule.Id == "ado");
        var post = readOnly!.Single(rule => rule.Id == "ado-read-post");
        post.Methods.Should().Equal("POST");
        post.Paths.Should().Equal(PrepareReads);
        post.AuthProvider.Should().Be(ado.AuthProvider).And.Be("ado-auth");
        // Exact host, one priority ahead: the ado rule's wildcard hosts make it a broad allow that must come last.
        post.Hosts.Should().Equal("dev.azure.com");
        (post.Ports, post.RequiredScopes, post.Priority)
            .Should()
            .BeEquivalentTo((ado.Ports, ado.RequiredScopes, ado.Priority - 1));
        ado.Methods.Should().Equal("GET", "HEAD");
        // Every other rule is exactly the read-only policy without paths.
        readOnly!
            .Where(rule => rule.Id != "ado-read-post")
            .Select(rule => Shape(rule))
            .Should()
            .Equal(withoutPaths!.Select(rule => Shape(rule)));
        SandboxEgressPolicyCompiler.ValidateEffectivePrecedence(readOnly!).Should().BeEmpty();
    }

    [Theory]
    [InlineData("any-method", "dev.azure.com", "", "", "ado-auth", true)]
    [InlineData("explicit-post", "dev.azure.com", "POST", "", "ado-auth", true)]
    [InlineData("any-host", "", "", "", "ado-auth", true)]
    [InlineData("some-paths", "dev.azure.com", "", "/contoso/*", "ado-auth", false)]
    [InlineData("no-credential", "dev.azure.com", "", "", "", false)]
    [InlineData("not-dev-azure-com", "*.visualstudio.com", "", "", "ado-auth", false)]
    [InlineData("no-post", "dev.azure.com", "GET,PATCH", "", "ado-auth", false)]
    public void Only_an_ado_rule_with_a_credential_and_POST_on_every_dev_azure_com_path_gets_a_POST_sibling(
        string id,
        string hosts,
        string methods,
        string paths,
        string authProvider,
        bool sibling
    )
    {
        // Config rules can name only configured providers, so these credentialed rules are built directly: each
        // misses at most one of the sibling's conditions.
        static string[] List(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var rules = new List<SandboxNetworkRule>
        {
            new(
                id,
                "allow",
                List(hosts),
                [443],
                List(methods),
                List(paths),
                authProvider.Length > 0 ? authProvider : null,
                [],
                200
            ),
        };

        SandboxSessionRegistry.NarrowToReadOnly(rules, PrepareReads);

        output.WriteLine(
            "DEBUG narrowed {0}: "
                + string.Join(
                    "; ",
                    rules.Select(rule => $"{rule.Id} [{string.Join(",", rule.Methods)}] {string.Join(",", rule.Paths)}")
                ),
            id
        );
        string[] expected = sibling ? [id + "-read-post"] : [];
        rules.Where(rule => rule.Methods.Contains("POST")).Select(rule => rule.Id).Should().Equal(expected);
    }

    [Theory]
    [InlineData("POST", "/contoso/Weve%20DA/_git/Nova/git-upload-pack", "allow")]
    [InlineData("POST", "/Contoso/_apis/wit/workitemsbatch", "allow")]
    [InlineData("POST", "/Contoso/p-id/_apis/git/repositories/r-id/pullrequestquery", "allow")]
    [InlineData("GET", "/contoso/Weve%20DA/_git/Nova/info/refs", "allow")]
    [InlineData("POST", "/contoso/Weve%20DA/_git/Nova/git-receive-pack", "deny")]
    [InlineData("POST", "/contoso/Weve%20DA/_git/Other/git-upload-pack", "deny")]
    [InlineData("POST", "/Contoso/p-id/_apis/git/repositories/r-id/pullRequests/1/threads", "deny")]
    [InlineData("PATCH", "/contoso/Weve%20DA/_git/Nova/git-upload-pack", "deny")]
    [InlineData("POST", "/contoso/weve%20da/_git/nova/git-upload-pack", "deny")]
    public void A_read_only_sandbox_with_post_paths_may_fetch_and_query_but_never_push_or_post(
        string method,
        string path,
        string decision
    )
    {
        var (auth, options) = HostLikePolicy();
        var (_, readOnly) = SandboxSessionRegistry.BuildAuthPolicy(
            auth,
            options,
            "test-secret",
            readOnlyEgress: true,
            readOnlyAdoPostPaths: PrepareReads
        );

        FirstMatch(readOnly!, "dev.azure.com", 443, method, path).Should().Be(decision);
    }

    [Theory]
    [InlineData("/contoso/p/_git/r/git-receive-pack")]
    [InlineData("/contoso/p/_apis/git/repositories/r/pullRequests/1/threads")]
    [InlineData("/contoso/p/_git/r/*")]
    [InlineData("contoso/p/_git/r/git-upload-pack")]
    [InlineData("/contoso/p/_git/../r/git-upload-pack")]
    [InlineData("/contoso/p/r/git-upload-pack")]
    [InlineData("/contoso/_apis/wit/workitemsbatch?x=1")]
    public void Only_exact_read_endpoints_are_accepted_as_read_only_post_paths(string path)
    {
        var (auth, options) = HostLikePolicy();

        var build = () =>
            SandboxSessionRegistry.BuildAuthPolicy(
                auth,
                options,
                "test-secret",
                readOnlyEgress: true,
                readOnlyAdoPostPaths: [path]
            );

        build.Should().Throw<ArgumentException>().WithMessage("*is not an exact read-only ADO POST path*");
    }

    [Theory]
    [InlineData("github.com", "GET", "/org/store.git/info/refs", "allow", "allow")]
    [InlineData("github.com", "POST", "/org/store.git/git-receive-pack", "deny", "allow")]
    [InlineData("github.com", "POST", "/org/store/git-receive-pack", "deny", "allow")]
    [InlineData("github.com", "POST", "/org/store.git/git-upload-pack", "deny", "allow")]
    [InlineData("api.github.com", "GET", "/repos/org/store", "allow", "allow")]
    [InlineData("api.github.com", "POST", "/repos/org/store/git/refs", "deny", "allow")]
    [InlineData("api.github.com", "PATCH", "/repos/org/store/git/refs/heads/review", "deny", "allow")]
    [InlineData("dev.azure.com", "POST", "/contoso/Weve%20DA/_git/Nova/git-upload-pack", "allow", "allow")]
    [InlineData("dev.azure.com", "POST", "/contoso/Weve%20DA/_git/Nova/git-receive-pack", "deny", "deny")]
    public void A_read_only_github_sandbox_may_read_github_but_never_push_or_write(
        string host,
        string method,
        string path,
        string readOnlyGitHub,
        string readOnlyAdo
    )
    {
        // A shadow agent can edit the helper that commits its archive, so its session must not be able to push the
        // store branch, by git or through the API. The path grammar cannot name git-receive-pack on every repo, and a
        // fetch is a POST too, so GitHub keeps GET and HEAD only. ADO's exact fetch and query paths stay.
        var (auth, options) = HostLikePolicy();
        IReadOnlyList<SandboxNetworkRule> Build(bool github) =>
            SandboxSessionRegistry
                .BuildAuthPolicy(
                    auth,
                    options,
                    "test-secret",
                    readOnlyEgress: true,
                    readOnlyAdoPostPaths: PrepareReads,
                    readOnlyGitHub: github
                )
                .Network!;

        var github = Build(github: true);
        output.WriteLine("DEBUG read-only github rules: {0}", string.Join("; ", github.Select(rule => Shape(rule))));

        FirstMatch(github, host, 443, method, path).Should().Be(readOnlyGitHub);
        FirstMatch(Build(github: false), host, 443, method, path).Should().Be(readOnlyAdo);
    }

    [Fact]
    public void Read_only_github_needs_read_only_egress()
    {
        var (auth, options) = HostLikePolicy();

        var build = () => SandboxSessionRegistry.BuildAuthPolicy(auth, options, "test-secret", readOnlyGitHub: true);

        build.Should().Throw<ArgumentException>().WithMessage("Read-only GitHub only applies*");
    }

    [Fact]
    public void Post_paths_need_read_only_egress()
    {
        var (auth, options) = HostLikePolicy();

        var build = () =>
            SandboxSessionRegistry.BuildAuthPolicy(auth, options, "test-secret", readOnlyAdoPostPaths: PrepareReads);

        build.Should().Throw<ArgumentException>().WithMessage("Read-only ADO POST paths only apply*");
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
    private static string FirstMatch(
        IReadOnlyList<SandboxNetworkRule> rules,
        string host,
        int port,
        string method,
        string path = "/"
    ) =>
        rules
            .OrderBy(rule => rule.Priority)
            .FirstOrDefault(rule =>
                (rule.Hosts.Count == 0 || EgressHostMatcher.IsAllowed(rule.Hosts, host))
                && (rule.Ports.Count == 0 || rule.Ports.Contains(port))
                && (
                    rule.Methods.Count == 0
                    || rule.Methods.Any(m => m == "*" || string.Equals(m, method, StringComparison.OrdinalIgnoreCase))
                )
                && (rule.Paths.Count == 0 || rule.Paths.Any(p => PathMatches(p, path)))
            )
            ?.Action
        ?? "deny";

    /// <summary>The gateway path grammar: <c>*</c>, a trailing <c>/*</c> inclusive prefix, or an exact case-sensitive path.</summary>
    private static bool PathMatches(string pattern, string path) =>
        pattern == "*"
        || (
            pattern.EndsWith("/*", StringComparison.Ordinal)
                ? path == pattern[..^2] || path.StartsWith(pattern[..^1], StringComparison.Ordinal)
                : pattern == path
        );

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
