using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// Integration: a real v0.1.18 gateway materializes policy into a Docker sandbox and its installed helper.
/// No upstream traffic or credentials are needed; the committed marketplace contains a public HTTP declaration.
/// </summary>
[Collection(LiveGatewayCollection.Name)]
public sealed class SandboxMcpPolicyLiveContractTests(LiveGatewayFixture fixture)
{
    private const string Server = "sdk-contract-public";

    [SkippableFact]
    public async Task McpPolicy_AutoNone_MaterializesTheManifestAndInstalledHelperReadsIt()
    {
        Skip.If(fixture.SkipReason is not null, fixture.SkipReason);
        var client = fixture.Client;
        var preview = await client.PreviewMarketplacesAsync();
        var marketplace = preview.Marketplaces.FirstOrDefault(m => m.Plugins.Any(p => p.Name == "skilltiers"));
        if (marketplace is null)
        {
            const string reason =
                "Mount the committed SDK fixture marketplace containing skilltiers and its .mcp.json.";
            Skip.IfNot(fixture.ContractRequired, reason);
            throw new InvalidOperationException(reason + " SANDBOX_CONTRACT_REQUIRED forbids skipping.");
        }

        var session = await client.CreateAsync(
            new SandboxCreateRequest(
                "mcp-policy-" + Guid.NewGuid().ToString("N"),
                marketplaces: [marketplace.Alias],
                pluginSelection: [new SandboxPluginRef(marketplace.Alias, "skilltiers")]
            )
            {
                Mcp = new SandboxMcpPolicy(
                    SandboxMcpEgress.Auto,
                    new Dictionary<string, SandboxMcpServerPolicy>
                    {
                        [Server] = new(auth: SandboxMcpAuthMode.None, url: "https://mcp.example.invalid/mcp"),
                    }
                ),
            }
        );
        try
        {
            // Integration: typed reads must agree with the committed declaration and installed file.
            session.Mcp.Should().NotBeNull();
            session.Mcp!.Egress.Should().Be(SandboxMcpEgress.Auto);
            var createdBinding = session.Mcp.Servers.Single(row => row.Name == Server);
            var current = await client.GetAsync(session.SessionId);
            var currentBinding = current.Mcp!.Servers.Single(row => row.Name == Server);
            currentBinding.RuleId.Should().Be(createdBinding.RuleId).And.NotBeNullOrEmpty();
            currentBinding.Url.Should().Be("https://mcp.example.invalid/mcp");
            var typedManifest = await client.GetMcpManifestAsync(session.SessionId);
            typedManifest.Schema.Should().Be("sbx.mcp-servers/1");
            typedManifest.SessionId.Should().Be(session.SessionId);
            var typedServer = typedManifest.Servers.Single(row => row.Name == Server);
            (typedServer.Plugin, typedServer.Marketplace, typedServer.Transport)
                .Should()
                .Be(("skilltiers", marketplace.Alias, "http"));
            typedServer.Supported.Should().BeTrue();
            typedServer.Auth.Mode.Should().Be(SandboxMcpAuthMode.None);
            typedServer.Auth.Provider.Should().BeNull();
            typedServer.Egress.Should().Be(SandboxMcpManifestEgress.Auto);
            typedServer.Headers.Should().BeEmpty();
            typedServer.UnresolvedVars.Should().BeEmpty();

            var result = await client.ExecuteAsync(
                session.SessionId,
                new SandboxCommand(["sh", "-c", "cat \"$SBX_MCP_MANIFEST\""])
            );
            result.ExitCode.Should().Be(0);
            using var manifest = JsonDocument.Parse(result.StandardOutput);
            manifest.RootElement.GetProperty("schema").GetString().Should().Be("sbx.mcp-servers/1");
            var server = manifest
                .RootElement.GetProperty("servers")
                .EnumerateArray()
                .Single(row => row.GetProperty("name").GetString() == Server);
            server
                .GetProperty("egress")
                .GetString()
                .Should()
                .Be("auto", "dropping the SDK mcp block would leave manual egress");
            server.GetProperty("auth").GetProperty("mode").GetString().Should().Be("none");
            server.GetProperty("headers").EnumerateObject().Should().BeEmpty();
            server.GetProperty("url").GetString().Should().Be("https://mcp.example.invalid/mcp");

            var listed = await client.ExecuteAsync(session.SessionId, new SandboxCommand(["sbx-mcp", "list"]));
            listed.ExitCode.Should().Be(0);
            listed.StandardOutput.Should().Contain(Server);
        }
        finally
        {
            await client.DeleteAsync(session.SessionId);
        }
    }
}
