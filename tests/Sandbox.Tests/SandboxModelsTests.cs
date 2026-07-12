using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

public class SandboxModelsTests
{
    [Fact]
    public void SandboxAuthProvider_ToString_NeverIncludesGatewayAuth()
    {
        var provider = new SandboxAuthProvider("github-auth", "webhook", "https://app/callback", "super-secret-value", 300);

        var rendered = provider.ToString();

        rendered.Should().NotContain("super-secret-value");
        rendered.Should().Contain("REDACTED");
    }

    [Fact]
    public void SandboxAuthProvider_RequiredScopes_IsDefensivelyCopied()
    {
        var scopes = new List<string> { "repo" };
        var provider = new SandboxAuthProvider("id", "webhook", "https://app", "secret", 300, scopes);

        scopes.Add("workflow");

        provider.RequiredScopes.Should().BeEquivalentTo(["repo"]);
    }

    [Theory]
    [InlineData("", "type")]
    [InlineData("id", "")]
    public void SandboxAuthProvider_BlankRequiredField_Throws(string id, string type)
    {
        var act = () => new SandboxAuthProvider(id, type, "https://app", "secret", 300);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SandboxDiscoverySettings_ToString_NeverIncludesWebhookAuth()
    {
        var settings = new SandboxDiscoverySettings("https://app/discovery", "shared-secret-value");

        var rendered = settings.ToString();

        rendered.Should().NotContain("shared-secret-value");
        rendered.Should().Contain("REDACTED");
    }

    [Fact]
    public void SandboxNetworkRule_CollectionArguments_AreDefensivelyCopied()
    {
        var hosts = new List<string> { "github.com" };
        var rule = new SandboxNetworkRule("github", "allow", hosts: hosts);

        hosts.Add("api.github.com");

        rule.Hosts.Should().BeEquivalentTo(["github.com"]);
    }

    [Fact]
    public void SandboxCreateRequest_CollectionArguments_AreDefensivelyCopied()
    {
        var marketplaces = new List<string> { "official" };
        var request = new SandboxCreateRequest("my-workspace", marketplaces);

        marketplaces.Add("claude_plugins");

        request.Marketplaces.Should().BeEquivalentTo(["official"]);
    }

    [Fact]
    public void SandboxCreateRequest_NullWorkspace_Throws()
    {
        var act = () => new SandboxCreateRequest(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SandboxCreateRequest_EmptyWorkspace_IsAllowed()
    {
        var request = new SandboxCreateRequest(string.Empty);

        request.Workspace.Should().BeEmpty();
    }

    [Fact]
    public void SandboxCreateRequest_OmittedCollections_AreEmptyNotNull()
    {
        var request = new SandboxCreateRequest("ws");

        request.Marketplaces.Should().BeEmpty();
        request.AuthProviders.Should().BeEmpty();
        request.NetworkRules.Should().BeEmpty();
        request.Discovery.Should().BeNull();
    }

    [Fact]
    public void SandboxInfo_BlankSessionId_Throws()
    {
        var act = () => new SandboxInfo("");

        act.Should().Throw<ArgumentException>();
    }
}
