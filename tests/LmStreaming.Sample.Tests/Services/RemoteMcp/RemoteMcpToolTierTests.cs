using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.Sandbox;
using FluentAssertions.Execution;
using LmStreaming.Sample.Services.RemoteMcp;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services.RemoteMcp;

/// <summary>
/// A remote MCP tool the tiers hold as available (not in the tool list): <c>ExecuteTool</c> checks its arguments
/// against a schema never stricter than the one <c>SearchTool</c> shows. The contract comes through the real connect
/// and MCP extraction, which keeps only each property's type (an object's keys are gone) and maps a union type to
/// string, so the check may refuse only what the server itself would refuse.
/// </summary>
public sealed class RemoteMcpToolTierTests
{
    private const string Thread = "thread-remote-mcp-tiers";

    // filters: a free-form object whose keys are the server's; limit: an optional integer as a union with null;
    // query: a real string.
    private const string SearchItemsTool = """
        [{"name":"search_items","description":"Search the catalog items by field filters.",
          "inputSchema":{"type":"object","properties":{
            "filters":{"type":"object","description":"Field filters."},
            "limit":{"type":["integer","null"],"description":"The most items to return."},
            "query":{"type":"string","description":"Words to match."}},
          "required":["filters"]}}]
        """;

    // F-002/F-004. (a) and (b) red: the gate and the shown schema closed the object and typed the union as a string.
    // (c) red if the gate is removed: the call would run without its required argument. (d) red: the shown schema
    // drops a string's type with the gate's copy (the model loses real guidance).
    [Fact]
    public async Task ExecuteTool_RunsAnMcpObjectAndUnionArgument_AndRefusesAMissingRequiredOne_WithTheShownSchema()
    {
        var log = new CapturingLogger<RemoteMcpToolTierTests>();
        var connection = await ConnectAsync(new FakeRemoteMcpHandler(SearchItemsTool), log);
        try
        {
            connection.Tools.Should().ContainSingle(string.Join(" | ", log.Entries.Select(e => e.Message)));
            var tool = connection.Tools[0];
            using var tiers = await TiersAsync(tool.Contract);
            var execute = new FunctionRegistry()
                .AddProvider(tiers.RootProvider)
                .AddFunction(tool.Contract, tool.Handler)
                .GetProviders()
                .OfType<IDeferredToolProvider>()
                .Single();
            var text = await SearchAsync(tiers, """{"query":"search catalog items"}""");
            text.Should().Contain($"1. {tool.Contract.Name} [available]", "guard: an available tool's call is checked");

            // One scope: the red run reports (a), (b) and (c) together.
            using var scope = new AssertionScope();
            var run = execute.TryUnwrap(
                CapabilityTiers.ExecuteToolToolName,
                Call(tool.Contract.Name, """{"filters":{"k":"v"},"limit":5}""")
            )!;
            run.ErrorJson.Should().BeNull("(a) the server accepts these arguments");
            run.FunctionName.Should().Be(tool.Contract.Name);
            run.ArgumentsJson.Should().Be("""{"filters":{"k":"v"},"limit":5}""", "(a) an integer for the union runs");

            var args = ArgsOf(text);
            args["properties"]!.AsObject().Select(p => p.Key).Should().Equal("filters", "limit", "query");
            (args["properties"]!["filters"]!["additionalProperties"]?.GetValue<bool>() ?? true)
                .Should()
                .BeTrue("(b) the object's keys are the server's, so it is shown open");
            args["required"]!.AsArray().Select(r => r!.GetValue<string>()).Should().Equal("filters");
            // Parenthesized: a bare ?. chain would skip the assertion when the type is missing.
            (args["properties"]!["query"]!["type"]?.GetValue<string>())
                .Should()
                .Be("string", "(d) the model is shown a real string parameter's type");

            var refusal = execute.TryUnwrap(
                CapabilityTiers.ExecuteToolToolName,
                Call(tool.Contract.Name, """{"limit":5}""")
            )!;
            refusal.ErrorCode.Should().Be("invalid_args", "(c) a missing required argument is still refused");
            var error = JsonNode.Parse(refusal.ErrorJson!)!;
            error["errors"]!.AsArray().Select(e => e!.GetValue<string>()).Should().Contain(e => e.Contains("filters"));
            JsonNode
                .DeepEquals(error["schema"], args)
                .Should()
                .BeTrue("the refusal shows the schema SearchTool showed, the one the call was checked against");
        }
        finally
        {
            foreach (var resource in connection.Resources)
            {
                await resource.DisposeAsync();
            }
        }
    }

    private static string Call(string tool, string arguments) => $$"""{"tool":"{{tool}}","arguments":{{arguments}}}""";

    private static JsonNode ArgsOf(string searchText) =>
        JsonNode.Parse(
            searchText.Split('\n').Single(l => l.StartsWith("   Args: ", StringComparison.Ordinal))[
                "   Args: ".Length..
            ]
        )!;

    /// <summary>One connect of a single plugin http server with no egress key, through <paramref name="server"/>.</summary>
    private static Task<RemoteMcpConnection> ConnectAsync(FakeRemoteMcpHandler server, ILogger logger) =>
        RemoteMcpConnector.ConnectAsync(
            _ =>
                Task.FromResult<IReadOnlyList<SandboxMcpServer>>([
                    new SandboxMcpServer
                    {
                        Name = "api",
                        Plugin = "acme",
                        Marketplace = "mk",
                        Type = "http",
                        Supported = true,
                        Url = "https://acme.test/mcp",
                    },
                ]),
            new PredefinedKeyRegistry(
                Path.Combine(Path.GetTempPath(), "remote-mcp-no-keys-" + Guid.NewGuid().ToString("N")),
                new NoTokenStore(),
                new HttpClient(),
                NullLoggerFactory.Instance
            ),
            allowLoopbackHttp: false,
            server,
            logger,
            "sess-1",
            CancellationToken.None,
            ["https://acme.test"]
        );

    /// <summary>One host build with <paramref name="tools"/> tiered: stage, attach, commit. Nothing is loaded.</summary>
    private static async Task<CapabilityTiers> TiersAsync(params FunctionContract[] tools)
    {
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(Thread, new ThreadMetadata { ThreadId = Thread, LastUpdated = 1 });
        var tiers = new CapabilityTiers(Thread, store, NullLogger.Instance);
        var gateway = new SkillTierGateway(
            _ =>
                Task.FromResult(
                    new SandboxSkillCatalog(
                        "sess-1",
                        "",
                        SandboxSkillTier.Included,
                        new SandboxSkillCounts(0, 0, 0, 0),
                        [],
                        [],
                        false
                    )
                ),
            (_, _, _) => Task.CompletedTask,
            _ => Task.FromResult<FunctionContract?>(null)
        );
        using (
            var stage = await tiers.StageAsync(
                new TierSettings([], [], [], [], 10, null, 5),
                gateway,
                CancellationToken.None
            )
        )
        {
            stage.AttachTools(tools);
            await stage.CommitAsync(CancellationToken.None);
        }

        tiers.IsActive.Should().BeTrue("guard: the build applied");
        return tiers;
    }

    private static async Task<string> SearchAsync(CapabilityTiers tiers, string args)
    {
        var search = tiers
            .RootProvider.GetFunctions()
            .Single(f => f.Contract.Name == CapabilityTiers.SearchToolToolName);
        var result = await search.Handler(args, new ToolCallContext(), CancellationToken.None);
        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload.Text;
    }

    private sealed class NoTokenStore : IOAuthTokenStore
    {
        public Task<OAuthTokenRecord?> GetAsync(string provider, CancellationToken ct = default) =>
            Task.FromResult<OAuthTokenRecord?>(null);

        public Task SaveAsync(OAuthTokenRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string provider, CancellationToken ct = default) => Task.CompletedTask;
    }
}
