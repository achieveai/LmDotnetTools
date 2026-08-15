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
    public void SandboxNetworkRule_OmittedAuthProvider_IsNullNotEmpty()
    {
        var rule = new SandboxNetworkRule("github", "allow");

        rule.AuthProvider.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SandboxNetworkRule_BlankAuthProvider_Throws(string authProvider)
    {
        var act = () => new SandboxNetworkRule("github", "allow", authProvider: authProvider);

        act.Should().Throw<ArgumentException>();
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

    [Fact]
    public void SandboxDiscoveredItem_NullName_IsAllowed()
    {
        // Pinned against the gateway's DiscoveredFile wire contract
        // (crates/mcp-gateway/src/api/sandboxes.rs, SandboxedOstoolsMcpServer@c0dc9cfe...): "name" is
        // `Option<String>` and is omitted entirely for a "context_file" item. The model must not
        // require it.
        var item = new SandboxDiscoveredItem("context_file", name: null, description: null, path: "/workspace/CLAUDE.md");

        item.Name.Should().BeNull();
        item.Kind.Should().Be("context_file");
        item.Path.Should().Be("/workspace/CLAUDE.md");
    }

    [Fact]
    public void SandboxDiscoveredItem_UnrecognizedKind_IsAllowed()
    {
        // The model does not hard-code per-kind requirements, so a discriminator this SDK does not
        // yet recognize (a future gateway addition) is tolerated rather than rejected.
        var item = new SandboxDiscoveredItem("future_kind_v2", name: null, description: null, path: "/workspace/whatever");

        item.Kind.Should().Be("future_kind_v2");
        item.Name.Should().BeNull();
    }

    [Theory]
    [InlineData("", "/workspace/x")]
    [InlineData("   ", "/workspace/x")]
    [InlineData("context_file", "")]
    [InlineData("context_file", "   ")]
    public void SandboxDiscoveredItem_BlankKindOrPath_Throws(string kind, string path)
    {
        // Unlike "name", "kind" and "path" ARE required by the gateway's wire contract for every
        // kind, so validation for those two fields must be preserved.
        var act = () => new SandboxDiscoveredItem(kind, name: null, description: null, path: path);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("", "plugin")]
    [InlineData("marketplace", "")]
    public void SandboxPluginRef_BlankRequiredField_Throws(string marketplace, string plugin)
    {
        var act = () => new SandboxPluginRef(marketplace, plugin);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SandboxPluginRef_ValidFields_ExposesThem()
    {
        var pluginRef = new SandboxPluginRef("official", "code-review");

        pluginRef.Marketplace.Should().Be("official");
        pluginRef.Plugin.Should().Be("code-review");
    }

    [Fact]
    public void SandboxPluginRef_NullMarketplace_Throws()
    {
        var act = () => new SandboxPluginRef(null!, "code-review");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SandboxCreateRequest_OmittedPluginSelection_IsNullNotEmpty()
    {
        var request = new SandboxCreateRequest("ws");

        request.PluginSelection.Should().BeNull();
    }

    [Fact]
    public void SandboxCreateRequest_ExplicitEmptyPluginSelection_StaysEmpty_NotNull()
    {
        var request = new SandboxCreateRequest("ws", pluginSelection: []);

        request.PluginSelection.Should().NotBeNull();
        request.PluginSelection.Should().BeEmpty();
    }

    [Fact]
    public void SandboxCreateRequest_PluginSelection_IsDefensivelyCopied()
    {
        var refs = new List<SandboxPluginRef> { new("official", "code-review") };
        var request = new SandboxCreateRequest("ws", pluginSelection: refs);

        refs.Add(new SandboxPluginRef("official", "other"));

        request.PluginSelection.Should().HaveCount(1);
    }

    [Fact]
    public void SandboxInfo_OmittedPluginResolution_IsNull()
    {
        var info = new SandboxInfo("sess-1");

        info.PluginResolution.Should().BeNull();
    }

    [Fact]
    public void SandboxPluginResolution_ExposesFields()
    {
        var resolution = new SandboxPluginResolution(
            supported: true,
            requested: [new SandboxPluginRef("official", "code-review")],
            effective: [new SandboxPluginRef("official", "code-review")],
            failed: []
        );

        resolution.Supported.Should().BeTrue();
        resolution.Requested.Should().ContainSingle();
        resolution.Failed.Should().BeEmpty();
    }

    [Fact]
    public void SandboxPluginResolution_NullRequested_StaysNull_NotEmpty()
    {
        var resolution = new SandboxPluginResolution(
            supported: true,
            requested: null,
            effective: [],
            failed: []
        );

        resolution.Requested.Should().BeNull();
    }
}
