using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

public sealed class SandboxMcpPolicyTests
{
    [Fact]
    public void BuildAuthPolicy_ProtectedGitHubApi_PreservesOtherGitHubCredentialHosts()
    {
        var auth = new AuthOptions();
        auth.Github.ClientId = "test-client";
        var options = OptionsFor("https://api.github.com/mcp");

        var (_, rules) = SandboxSessionRegistry.BuildAuthPolicy(auth, options, "test-secret");

        rules
            .Should()
            .ContainSingle(rule => rule.Id == "github")
            .Which.Hosts.Should()
            .Equal("github.com", "codeload.github.com");
        rules
            .Should()
            .ContainSingle(rule => rule.Id == "github-egress")
            .Which.Hosts.Should()
            .Equal("results-receiver.actions.githubusercontent.com", "*.blob.core.windows.net");
    }

    [Fact]
    public void BuildAuthPolicy_ManualKnownMcpProvider_PreservesItsPrefixAndCustomRuleId()
    {
        var options = OptionsFor("https://mcp.example/mcp");
        options.Mcp!.Egress = "manual";
        options.Mcp.Servers["stub"] = new() { Auth = "injected", Url = "https://mcp.example/mcp" };
        options.Network.Rules["manual-stub"] = new()
        {
            Hosts = "mcp.example",
            Ports = "443",
            Methods = "POST,GET,DELETE",
            Paths = "/mcp",
            AuthProvider = "mcp:stub",
        };

        var (_, rules) = SandboxSessionRegistry.BuildAuthPolicy(new AuthOptions(), options, "test-secret");

        rules.Should().ContainSingle().Which.AuthProvider.Should().Be("mcp:stub");
        rules![0].Id.Should().Be("manual-stub");
    }

    [Theory]
    [InlineData("url-query")]
    [InlineData("duplicate")]
    [InlineData("header")]
    [InlineData("ttl")]
    [InlineData("policy-url")]
    public void BuildAuthPolicy_UnsafeCredentialBinding_RejectsItWithoutEchoingValues(string invalid)
    {
        var options = OptionsFor("https://mcp.example/mcp");
        options.AppId = "app-a";
        var binding = options.Mcp!.CredentialBindings[0];
        switch (invalid)
        {
            case "url-query":
                binding.Url += "?token=secret-canary";
                break;
            case "duplicate":
                options.Mcp.CredentialBindings.Add(binding);
                break;
            case "header":
                binding.Headers = ["Host"];
                break;
            case "ttl":
                binding.CacheTtlSeconds = 0;
                break;
            case "policy-url":
                options.Mcp.Servers["stub"] = new() { Url = "https://other.example/secret-canary" };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalid));
        }

        var action = () => SandboxSessionRegistry.BuildAuthPolicy(new AuthOptions(), options, "test-secret");

        action.Should().Throw<ArgumentException>().Which.Message.Should().NotContain("secret-canary");
    }

    [Theory]
    [InlineData("no-mcp")]
    [InlineData("case")]
    [InlineData("disabled")]
    [InlineData("none")]
    public void BuildAuthPolicy_ManualUnknownOrDisabledMcpProvider_DoesNotBecomeAnAnonymousAllow(string invalid)
    {
        var options = OptionsFor("https://mcp.example/mcp");
        options.Mcp!.Egress = "manual";
        options.Mcp.Servers["stub"] = new()
        {
            Auth = invalid == "none" ? "none" : "injected",
            Enabled = invalid != "disabled",
        };
        options.Network.Rules["manual-stub"] = new()
        {
            Hosts = "mcp.example",
            Ports = "443",
            Methods = "POST",
            Paths = "/mcp",
            AuthProvider = invalid == "case" ? "mcp:Stub" : "mcp:stub",
        };
        if (invalid == "no-mcp")
        {
            options.Mcp = null;
        }

        var action = () => SandboxSessionRegistry.BuildAuthPolicy(new AuthOptions(), options, "test-secret");

        action.Should().Throw<ArgumentException>();
        SandboxEgressPolicyCompiler
            .CompileRules(options)
            .Should()
            .ContainSingle()
            .Which.AuthProvider.Should()
            .NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("https://dev.azure.com/mcp", "ado")]
    [InlineData("https://results-receiver.actions.githubusercontent.com/mcp", "github-egress")]
    public void BuildAuthPolicy_UnsubtractableGeneratedOverlap_RequiresAnExplicitOperatorChoice(
        string endpoint,
        string ruleId
    )
    {
        var auth = new AuthOptions();
        auth.Github.ClientId = "test-client";
        auth.Ado.ClientId = "test-client";
        var options = OptionsFor(endpoint);
        var action = () => SandboxSessionRegistry.BuildAuthPolicy(auth, options, "test-secret");

        action.Should().Throw<ArgumentException>().Which.Message.Should().Contain(ruleId);

        options.Network.Rules[ruleId] = new() { Enabled = false };
        action.Should().NotThrow();
    }

    private static SandboxGatewayOptions OptionsFor(string endpoint) =>
        new()
        {
            Mcp = new SandboxMcpOptions
            {
                Egress = "auto",
                CredentialBindings =
                [
                    new()
                    {
                        AppId = "app-a",
                        Server = "stub",
                        Url = endpoint,
                        ProviderId = "github",
                    },
                ],
            },
        };
}
