using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Skill tiers (gateway ADR 0041, <c>Docs/tools-and-api.md</c> "GET/PATCH …/skills"): the create block and its
/// resolved echo, the per-agent catalog read and update, and <c>included_skills</c> on the agent list.
/// </summary>
public sealed class SandboxClientSkillTiersTests
{
    // The documented example catalog, trimmed to one marketplace skill and one workspace skill.
    private const string CatalogJson = """
        {"session_id":"s-1","agent_id":"main","default_tier":"available",
         "counts":{"included":5,"available":310,"unavailable":1,"loaded":2},
         "marketplaces":[{"alias":"official","plugins":[{"name":"document-skills","version":"1.2.0",
           "description":"Create and edit office documents","skills":[{"name":"document-skills:pdf",
           "path":"/marketplaces/official/document-skills/skills/pdf","description":"Work with PDF files",
           "tier":"available","tier_origin":"default","loaded":true,"loaded_at":"2026-10-11T10:02:11.123456Z"}]}]}],
         "workspace":{"skills":[{"name":"greet","path":"/workspace/.claude/skills/greet","description":"Say hi",
           "tier":"included","tier_origin":"override","loaded":false,"loaded_at":null,"notified":true}]}}
        """;

    [Fact]
    public async Task CreateAsync_SearchToolFlags_AreSentWhenSet_AndEchoed()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """
            {"session_id":"s-1","properties":{},
             "skills":{"default_tier":"included","included":[],"search_tool":true,"agents_search_tool":false}}
            """,
            HttpStatusCode.Created
        );

        var info = await client.CreateAsync(
            new SandboxCreateRequest("ws", skills: new SandboxSkillsPolicy(searchTool: true, agentsSearchTool: false))
        );

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        var skills = sent.RootElement.GetProperty("skills");
        skills.GetProperty("search_tool").GetBoolean().Should().BeTrue();
        skills.GetProperty("agents_search_tool").GetBoolean().Should().BeFalse();
        info.SkillsPolicy!.SearchTool.Should().BeTrue();
        info.SkillsPolicy.AgentsSearchTool.Should().BeFalse();
    }

    // Gateway 0.1.20 and older reject a skills block with a field they do not know (deny_unknown_fields).
    /// <summary>
    /// The gateway leaves a false flag off its response (seen on 0.1.21 and 0.1.22). A null here would, on reuse, omit
    /// the field and hand the choice to the gateway's env default, re-enabling the tool the caller turned off.
    /// </summary>
    [Fact]
    public async Task CreateAsync_FlagSentAndFalseOmitted_ResolvesToFalse_AndAReuseKeepsItOff()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """
            {"session_id":"s-1","properties":{},"skills":{"default_tier":"included","included":[],"search_tool":true}}
            """,
            HttpStatusCode.Created
        );

        var info = await client.CreateAsync(
            new SandboxCreateRequest("ws", skills: new SandboxSkillsPolicy(searchTool: true, agentsSearchTool: false))
        );

        info.SkillsPolicy!.SearchTool.Should().BeTrue("an explicit response value is kept as it is");
        info.SkillsPolicy.AgentsSearchTool.Should().BeFalse("this gateway took the flag, so absent means off");

        _ = await client.CreateAsync(new SandboxCreateRequest("ws", skills: info.SkillsPolicy));

        using var reused = JsonDocument.Parse(handler.Requests[1].Body!);
        var skills = reused.RootElement.GetProperty("skills");
        skills.GetProperty("search_tool").GetBoolean().Should().BeTrue();
        skills.GetProperty("agents_search_tool").GetBoolean().Should().BeFalse("a reuse must stay off");
    }

    /// <summary>A create that sent one flag settles both: the gateway accepted the two fields together.</summary>
    [Fact]
    public async Task CreateAsync_OneFlagSent_AnAbsentOtherFlagIsAlsoFalse()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """{"session_id":"s-1","properties":{},"skills":{"default_tier":"included","included":[]}}""",
            HttpStatusCode.Created
        );

        var info = await client.CreateAsync(
            new SandboxCreateRequest("ws", skills: new SandboxSkillsPolicy(searchTool: true))
        );

        info.SkillsPolicy!.SearchTool.Should().BeFalse("the gateway answered without it, so it is not on");
        info.SkillsPolicy.AgentsSearchTool.Should().BeFalse();
    }

    /// <summary>The agents flag alone also makes the create flag-bearing, so an omitted pair still settles as off.</summary>
    [Fact]
    public async Task CreateAsync_OnlyAgentsFlagSent_BothOmittedResponseFlagsResolveToFalse()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """{"session_id":"s-1","properties":{},"skills":{"default_tier":"included","included":[]}}""",
            HttpStatusCode.Created
        );

        var info = await client.CreateAsync(
            new SandboxCreateRequest("ws", skills: new SandboxSkillsPolicy(agentsSearchTool: false))
        );

        info.SkillsPolicy!.SearchTool.Should().BeFalse("a gateway that took the agents flag omits only a false flag");
        info.SkillsPolicy.AgentsSearchTool.Should().BeFalse();
    }

    /// <summary>With no flag sent, absent cannot be told from a gateway older than the fields, so nothing is invented.</summary>
    [Theory]
    [InlineData("", null, null)]
    [InlineData(""","search_tool":true,"agents_search_tool":false""", true, false)]
    public async Task CreateAsync_NoFlagSent_LeavesAbsentFlagsUnknown_AndKeepsExplicitOnes(
        string flags,
        bool? searchTool,
        bool? agentsSearchTool
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            $$$"""{"session_id":"s-1","properties":{},"skills":{"default_tier":"available","included":[]{{{flags}}}}}""",
            HttpStatusCode.Created
        );

        var info = await client.CreateAsync(
            new SandboxCreateRequest("ws", skills: new SandboxSkillsPolicy(SandboxSkillTier.Available))
        );

        info.SkillsPolicy!.SearchTool.Should().Be(searchTool);
        info.SkillsPolicy.AgentsSearchTool.Should().Be(agentsSearchTool);
    }

    [Fact]
    public async Task CreateAsync_FlagSentAndNoSkillsBlockAnswered_HasNoPolicy()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """{"session_id":"s-1","properties":{}}""",
            HttpStatusCode.Created
        );

        var info = await client.CreateAsync(
            new SandboxCreateRequest("ws", skills: new SandboxSkillsPolicy(searchTool: false))
        );

        info.SkillsPolicy.Should().BeNull("a missing block is not a policy with both tools off");
    }

    /// <summary>
    /// The CLR binds an already-built caller by exact signature, so the shape before the search flags must stay. The
    /// three calls below only compile while overload resolution stays unambiguous.
    /// </summary>
    [Fact]
    public void SandboxSkillsPolicy_ThreeArgumentConstructor_StaysForCallersBuiltBeforeTheSearchFlags()
    {
        var constructor = typeof(SandboxSkillsPolicy).GetConstructor([
            typeof(SandboxSkillTier?),
            typeof(IReadOnlyList<string>),
            typeof(string),
        ]);

        constructor.Should().NotBeNull();
        var policy = (SandboxSkillsPolicy)constructor!.Invoke([SandboxSkillTier.Available, new[] { "a" }, "hint"]);
        policy.DefaultTier.Should().Be(SandboxSkillTier.Available);
        policy.Included.Should().Equal("a");
        policy.UnlistedHint.Should().Be("hint");
        policy.SearchTool.Should().BeNull();
        new SandboxSkillsPolicy().DefaultTier.Should().BeNull();
        new SandboxSkillsPolicy(null, null, null).SearchTool.Should().BeNull();
        new SandboxSkillsPolicy(SandboxSkillTier.Included).DefaultTier.Should().Be(SandboxSkillTier.Included);
    }

    [Fact]
    public async Task CreateAsync_SearchToolFlagsUnset_AreNotOnTheWire()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", """{"session_id":"s-1"}""", HttpStatusCode.Created);

        _ = await client.CreateAsync(
            new SandboxCreateRequest("ws", skills: new SandboxSkillsPolicy(SandboxSkillTier.Available))
        );

        var body = handler.Requests.Single().Body!;
        body.Should().NotContain("search_tool");
    }

    [Fact]
    public void WithSkills_ReplacesOnlyTheSkills_AndLeavesTheOriginalAlone()
    {
        var original = new SandboxCreateRequest("ws", marketplaces: ["m"], image: "img");

        var copy = original.WithSkills(new SandboxSkillsPolicy(searchTool: true));

        original.Skills.Should().BeNull();
        copy.Skills!.SearchTool.Should().BeTrue();
        copy.Workspace.Should().Be("ws");
        copy.Marketplaces.Should().Equal("m");
        copy.Image.Should().Be("img");
    }

    [Theory]
    [InlineData("""{"version":"0.1.21"}""", "0.1.21", true)]
    [InlineData("""{"version":"0.1.22"}""", "0.1.22", true)]
    [InlineData("""{"version":"0.2.0"}""", "0.2.0", true)]
    [InlineData("""{"version":"0.1.20-fsfix"}""", "0.1.20", false)]
    [InlineData("""{"version":"0.1.21+build7"}""", "0.1.21", true)]
    [InlineData("""{"version":"dev"}""", null, false)]
    [InlineData("""{"version":7}""", null, false)]
    [InlineData("""{"name":"gateway"}""", null, false)]
    [InlineData("[]", null, false)]
    [InlineData("not json", null, false)]
    public async Task GetGatewayVersionAsync_ReadsTheVersion_AndUnknownCountsAsOlder(
        string body,
        string? expected,
        bool supported
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/", body);

        var version = await client.GetGatewayVersionAsync();

        version.Should().Be(expected is null ? null : Version.Parse(expected));
        SandboxClient.SupportsSearchTools(version).Should().Be(supported);
    }

    [Fact]
    public async Task GetGatewayVersionAsync_ErrorStatus_IsUnknown()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnStatus(HttpMethod.Get, "/", HttpStatusCode.InternalServerError);

        (await client.GetGatewayVersionAsync()).Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_SkillsPolicy_IsSentAndTheResolvedPolicyIsSurfaced()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """
            {"session_id":"s-1","properties":{},
             "skills":{"default_tier":"available","included":["official/document-skills/pdf"],"unlisted_hint":""}}
            """,
            HttpStatusCode.Created
        );

        var info = await client.CreateAsync(
            new SandboxCreateRequest(
                "ws",
                skills: new SandboxSkillsPolicy(SandboxSkillTier.Available, ["document-skills:pdf"], unlistedHint: "")
            )
        );

        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        var skills = sent.RootElement.GetProperty("skills");
        skills.GetProperty("default_tier").GetString().Should().Be("available");
        skills
            .GetProperty("included")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .Equal("document-skills:pdf");
        skills.GetProperty("unlisted_hint").GetString().Should().Be("", "an empty hint removes the trailer");
        info.SkillsPolicy.Should().NotBeNull();
        info.SkillsPolicy!.DefaultTier.Should().Be(SandboxSkillTier.Available);
        info.SkillsPolicy.Included.Should().Equal("official/document-skills/pdf");
        info.SkillsPolicy.UnlistedHint.Should().Be("");
    }

    [Fact]
    public void SandboxSkillsPolicy_UnavailableDefaultTier_IsRejectedBeforeAnyCall()
    {
        var act = () => new SandboxSkillsPolicy(SandboxSkillTier.Unavailable);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetSkillsAsync_SendsTheFilters_AndMapsTheCatalog()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s-1/skills", CatalogJson);

        var catalog = await client.GetSkillsAsync(
            "s-1",
            agentId: "main",
            tier: SandboxSkillTier.Available,
            loaded: true
        );

        handler.Requests.Single().Uri.Query.Should().Be("?agent_id=main&tier=available&loaded=true");
        catalog.AgentId.Should().Be("main");
        catalog.DefaultTier.Should().Be(SandboxSkillTier.Available);
        catalog.Counts.Should().Be(new SandboxSkillCounts(5, 310, 1, 2));
        catalog.ToolsListChanged.Should().BeFalse();
        var plugin = catalog.Marketplaces.Should().ContainSingle().Which.Plugins.Should().ContainSingle().Subject;
        plugin.Version.Should().Be("1.2.0");
        var pdf = plugin.Skills.Should().ContainSingle().Subject;
        pdf.Name.Should().Be("document-skills:pdf");
        pdf.TierOrigin.Should().Be(SandboxSkillTierOrigin.Default);
        pdf.Loaded.Should().BeTrue();
        pdf.LoadedAt.Should().NotBeNull();
        pdf.Notified.Should().BeNull("marketplace rows carry no notified flag");
        var greet = catalog.WorkspaceSkills.Should().ContainSingle().Subject;
        greet.Tier.Should().Be(SandboxSkillTier.Included);
        greet.TierOrigin.Should().Be(SandboxSkillTierOrigin.Override);
        greet.Notified.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateSkillTiersAsync_SendsSetAndReset_AndReportsThatToolsMustBeReListed()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Patch,
            "/api/v1/sandboxes/s-1/skills",
            CatalogJson.Replace("\"session_id\":\"s-1\",", "\"session_id\":\"s-1\",\"tools_list_changed\":true,")
        );

        var catalog = await client.UpdateSkillTiersAsync(
            "s-1",
            new Dictionary<string, SandboxSkillTier>
            {
                ["document-skills:pdf"] = SandboxSkillTier.Included,
                ["deploy-prod"] = SandboxSkillTier.Unavailable,
            },
            reset: true,
            agentId: "reviewer-1"
        );

        var request = handler.Requests.Single();
        request.Uri.Query.Should().Be("?agent_id=reviewer-1");
        var body = JsonDocument.Parse(request.Body!).RootElement;
        body.GetProperty("set").GetProperty("deploy-prod").GetString().Should().Be("unavailable");
        body.GetProperty("reset").GetBoolean().Should().BeTrue();
        catalog.ToolsListChanged.Should().BeTrue();
    }

    [Fact]
    public async Task GetSkillsAsync_DefaultAgent_SendsNoAgentId()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s-1/skills", CatalogJson.Replace("\"main\"", "\"\""));

        var catalog = await client.GetSkillsAsync("s-1");

        handler.Requests.Single().Uri.Query.Should().BeEmpty();
        catalog.AgentId.Should().BeEmpty();
    }

    [Theory]
    [InlineData("\"tier\":\"included\"", "\"tier\":\"sometimes\"")]
    [InlineData("\"tier_origin\":\"override\"", "\"tier_origin\":\"guess\"")]
    [InlineData("\"counts\":{\"included\":5,", "\"counts\":{")]
    public async Task GetSkillsAsync_MalformedCatalog_IsProtocol(string from, string to)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/s-1/skills", CatalogJson.Replace(from, to));

        var act = () => client.GetSkillsAsync("s-1");

        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task UpdateSkillTiersAsync_UnknownRef_IsProtocolNamingTheCode()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Patch,
            "/api/v1/sandboxes/s-1/skills",
            """{"error":"no skill matches 'nope'","code":400,"error_code":"skill_not_found","ref":"nope"}""",
            HttpStatusCode.BadRequest
        );

        var act = () =>
            client.UpdateSkillTiersAsync(
                "s-1",
                new Dictionary<string, SandboxSkillTier> { ["nope"] = SandboxSkillTier.Included }
            );

        var thrown = (await act.Should().ThrowAsync<SandboxException>()).Which;
        thrown.Kind.Should().Be(SandboxErrorKind.Protocol);
        thrown.ErrorCode.Should().Be("skill_not_found");
    }

    [Theory]
    [InlineData(",\"included_skills\":12", 12)]
    [InlineData("", null)] // a gateway before skill tiers
    public async Task ListAgentsAsync_ReportsIncludedSkills_WhenTheGatewaySendsThem(string field, int? expected)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/s-1/agents",
            "{\"session_id\":\"s-1\",\"agents\":[{\"agent_id\":\"\",\"created_at\":\"2026-10-11T10:00:00Z\","
                + "\"last_used_at\":\"2026-10-11T10:05:00Z\",\"open_tasks\":0,\"loaded_skills\":1"
                + field
                + "}]}"
        );

        var agents = await client.ListAgentsAsync("s-1");

        agents.Should().ContainSingle().Which.IncludedSkills.Should().Be(expected);
    }
}
