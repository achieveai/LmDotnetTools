using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// Per-agent skill tiers (gateway ADR 0041, v0.1.17 @5c4eeefc): the create <c>skills</c> block and its echo, the
/// <c>GET</c>/<c>PATCH …/skills</c> catalog, and the typed skill rejections. Wire JSON is the gateway's own:
/// Docs/tools-and-api.md examples, api/skills.rs error bodies, and a live 0.1.17 create echo.
/// </summary>
public class SandboxClientSkillTiersTests
{
    private const string SkillsPath = "/api/v1/sandboxes/sess-1/skills";

    // Docs/tools-and-api.md § GET …/skills example, plus a shadowed marketplace row and a plugin without version.
    private const string DocumentedCatalogJson = """
        {
          "session_id": "sess-1",
          "agent_id": "main",
          "default_tier": "available",
          "counts": { "included": 5, "available": 310, "unavailable": 1, "loaded": 2 },
          "marketplaces": [
            { "alias": "official",
              "plugins": [
                { "name": "document-skills", "version": "1.2.0",
                  "description": "Create and edit office documents",
                  "skills": [
                    { "name": "document-skills:pdf",
                      "path": "/marketplaces/official/document-skills/skills/pdf",
                      "description": "Work with PDF files", "tier": "available",
                      "tier_origin": "default", "loaded": true,
                      "loaded_at": "2026-10-11T10:02:11.123456Z" } ] },
                { "name": "greeter", "version": null, "description": "",
                  "skills": [
                    { "name": "greeter:greet", "path": "/marketplaces/official/greeter/skills/greet",
                      "description": "Marketplace greet", "tier": "unavailable", "tier_origin": "override",
                      "loaded": false, "loaded_at": null, "shadowed": true } ] } ] } ],
          "workspace": {
            "skills": [
              { "name": "greet", "path": "/workspace/.claude/skills/greet",
                "description": "Say hi", "tier": "included", "tier_origin": "included_list",
                "loaded": false, "loaded_at": null, "notified": true } ] }
        }
        """;

    [Fact]
    public async Task CreateAsync_WithSkillsPolicy_SendsTheGatewaySkillsBlock()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", """{"session_id":"sess-1"}""");

        _ = await client.CreateAsync(
            new SandboxCreateRequest(
                "ws",
                skills: new SandboxSkillsPolicy(
                    SandboxSkillTier.Available,
                    ["toolkit:lint", "/workspace/.claude/skills/greet"],
                    unlistedHint: ""
                )
            )
        );

        var body = JsonNode.Parse(handler.Requests.Single().Body!)!;
        body["skills"]!
            .ToJsonString()
            .Should()
            .Be(
                """{"default_tier":"available","included":["toolkit:lint","/workspace/.claude/skills/greet"],"unlisted_hint":""}"""
            );
    }

    [Fact]
    public async Task CreateAsync_SkillsPolicyWithOnlyDefaults_OmitsUnsetFields()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", """{"session_id":"sess-1"}""");

        _ = await client.CreateAsync(new SandboxCreateRequest("ws", skills: new SandboxSkillsPolicy()));

        // null hint = the gateway's default trailer, so it must be absent, never "" (which suppresses it).
        JsonNode.Parse(handler.Requests.Single().Body!)!["skills"]!
            .ToJsonString()
            .Should()
            .Be("{}");
    }

    [Fact]
    public async Task CreateAsync_WithoutSkillsPolicy_SendsTheLegacyBodyUnchanged()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", """{"session_id":"sess-1"}""");

        var info = await client.CreateAsync(new SandboxCreateRequest("ws"));

        handler.Requests.Single().Body.Should().Be("""{"app":{"id":"app-1"},"workspace":"ws"}""");
        info.Skills.Should().BeNull("a gateway that echoes no policy reports none");
    }

    [Fact]
    public async Task CreateAsync_SurfacesTheEchoedSkillsPolicy()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        // Observed from gateway 0.1.17 for skills {default_tier: available, included: ["toolkit:lint"]}.
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """
            {"session_id":"sess-1","skills":{"default_tier":"available","included":["/marketplaces/demo/toolkit/skills/lint"],"unlisted_hint":"Search first."}}
            """
        );

        var info = await client.CreateAsync(
            new SandboxCreateRequest(
                "ws",
                skills: new SandboxSkillsPolicy(SandboxSkillTier.Available, ["toolkit:lint"])
            )
        );

        info.Skills.Should().NotBeNull();
        info.Skills!.DefaultTier.Should().Be(SandboxSkillTier.Available);
        info.Skills.Included.Should().Equal("/marketplaces/demo/toolkit/skills/lint");
        info.Skills.UnlistedHint.Should().Be("Search first.");
    }

    [Theory]
    [InlineData("""{"session_id":"sess-1","skills":{"included":[]}}""")]
    [InlineData("""{"session_id":"sess-1","skills":{"default_tier":"unavailable"}}""")]
    [InlineData("""{"session_id":"sess-1","skills":{"default_tier":"sometimes"}}""")]
    public async Task CreateAsync_InvalidEchoedSkillsPolicy_IsProtocol(string json)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", json);

        var ex = await Assert.ThrowsAsync<SandboxException>(() => client.CreateAsync(new SandboxCreateRequest("ws")));

        ex.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task GetSkillCatalogAsync_ParsesTheDocumentedCatalog()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, SkillsPath, DocumentedCatalogJson);

        var catalog = await client.GetSkillCatalogAsync("sess-1", "main");

        catalog.SessionId.Should().Be("sess-1");
        catalog.AgentId.Should().Be("main");
        catalog.DefaultTier.Should().Be(SandboxSkillTier.Available);
        catalog.Counts.Should().Be(new SandboxSkillCounts(5, 310, 1, 2));
        catalog.ToolsListChanged.Should().BeFalse();

        var marketplace = catalog.Marketplaces.Single();
        marketplace.Alias.Should().Be("official");
        marketplace
            .Plugins.Select(p => (p.Name, p.Version, p.Description))
            .Should()
            .Equal(("document-skills", "1.2.0", "Create and edit office documents"), ("greeter", null, ""));
        marketplace
            .Plugins[0]
            .Skills.Single()
            .Should()
            .Be(
                new SandboxSkill(
                    "document-skills:pdf",
                    "/marketplaces/official/document-skills/skills/pdf",
                    "Work with PDF files",
                    SandboxSkillTier.Available,
                    SandboxSkillTierOrigin.Default,
                    Loaded: true,
                    LoadedAt: new DateTimeOffset(2026, 10, 11, 10, 2, 11, TimeSpan.Zero).AddTicks(1234560),
                    Notified: null,
                    Shadowed: false
                )
            );
        var shadowed = marketplace.Plugins[1].Skills.Single();
        shadowed.Tier.Should().Be(SandboxSkillTier.Unavailable);
        shadowed.TierOrigin.Should().Be(SandboxSkillTierOrigin.Override);
        shadowed.Shadowed.Should().BeTrue();
        shadowed.Loaded.Should().BeFalse();
        shadowed.LoadedAt.Should().BeNull();

        var workspace = catalog.WorkspaceSkills.Single();
        workspace.Name.Should().Be("greet");
        workspace.Path.Should().Be("/workspace/.claude/skills/greet");
        workspace.Tier.Should().Be(SandboxSkillTier.Included);
        workspace.TierOrigin.Should().Be(SandboxSkillTierOrigin.IncludedList);
        workspace.Notified.Should().BeTrue();
        workspace.Shadowed.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null, null, "")]
    [InlineData("", null, null, "")]
    [InlineData("probe-child", null, null, "?agent_id=probe-child")]
    [InlineData("", SandboxSkillTier.Included, null, "?tier=included")]
    [InlineData(
        "probe-child",
        SandboxSkillTier.Unavailable,
        false,
        "?agent_id=probe-child&tier=unavailable&loaded=false"
    )]
    [InlineData(null, SandboxSkillTier.Available, true, "?tier=available&loaded=true")]
    public async Task GetSkillCatalogAsync_SendsTheAgentAndFiltersAsQuery(
        string? agentId,
        SandboxSkillTier? tier,
        bool? loaded,
        string expectedQuery
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, SkillsPath, EmptyCatalogJson(agentId ?? ""));

        var catalog = await client.GetSkillCatalogAsync("sess-1", agentId, tier, loaded);

        var request = handler.Requests.Single();
        request.Uri.AbsolutePath.Should().Be(SkillsPath);
        request.Uri.Query.Should().Be(expectedQuery);
        request.SessionId.Should().BeNull("the skills routes are control-plane, not session-scoped direct calls");
        catalog.AgentId.Should().Be(agentId ?? "");
        catalog.Marketplaces.Should().BeEmpty();
        catalog.WorkspaceSkills.Should().BeEmpty();
    }

    [Fact]
    public async Task GetSkillCatalogAsync_CatalogForAnotherAgent_IsProtocol()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        // The default agent's catalog answering a probe-child request: the agent selector was lost.
        handler.OnJson(HttpMethod.Get, SkillsPath, EmptyCatalogJson(""));

        var ex = await Assert.ThrowsAsync<SandboxException>(() => client.GetSkillCatalogAsync("sess-1", "probe-child"));

        ex.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Theory]
    [InlineData(
        """{"session_id":"sess-1","agent_id":"","default_tier":"available","marketplaces":[],"workspace":{"skills":[]}}"""
    )]
    [InlineData(
        """{"session_id":"sess-1","agent_id":"","default_tier":"available","counts":{"included":0,"available":0,"unavailable":0,"loaded":0},"marketplaces":[]}"""
    )]
    [InlineData(
        """{"session_id":"sess-1","agent_id":"","default_tier":"hidden","counts":{"included":0,"available":0,"unavailable":0,"loaded":0},"marketplaces":[],"workspace":{"skills":[]}}"""
    )]
    [InlineData(
        """{"session_id":"sess-1","agent_id":"","default_tier":"available","counts":{"included":0,"available":0,"unavailable":0,"loaded":0},"marketplaces":[null],"workspace":{"skills":[]}}"""
    )]
    [InlineData(
        """{"session_id":"sess-1","agent_id":"","default_tier":"available","counts":{"included":0,"available":0,"unavailable":0,"loaded":0},"marketplaces":[],"workspace":{"skills":[{"name":"g","path":"/w/g","description":"","tier":"included","tier_origin":"pinned","loaded":false}]}}"""
    )]
    [InlineData(
        """{"session_id":"sess-1","agent_id":"","default_tier":"available","counts":{"included":0,"available":0,"unavailable":0,"loaded":0},"marketplaces":[],"workspace":{"skills":[{"name":"g","path":"/w/g","description":"","tier":"included","tier_origin":"default"}]}}"""
    )]
    [InlineData("not json")]
    public async Task GetSkillCatalogAsync_MalformedCatalog_IsProtocol(string json)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, SkillsPath, json);

        var ex = await Assert.ThrowsAsync<SandboxException>(() => client.GetSkillCatalogAsync("sess-1"));

        ex.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task PatchSkillTiersAsync_SendsSetAndReset_AndReturnsTheCatalogWithToolsListChanged()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Patch, SkillsPath, EmptyCatalogJson("probe-child", toolsListChanged: true));

        var catalog = await client.PatchSkillTiersAsync(
            "sess-1",
            "probe-child",
            new Dictionary<string, SandboxSkillTier>
            {
                ["document-skills:pdf"] = SandboxSkillTier.Included,
                ["/workspace/.claude/skills/greet"] = SandboxSkillTier.Available,
                ["deploy-prod"] = SandboxSkillTier.Unavailable,
            },
            reset: true
        );

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Patch);
        request.Uri.PathAndQuery.Should().Be(SkillsPath + "?agent_id=probe-child");
        // The docs' PATCH example, with reset on.
        JsonNode
            .DeepEquals(
                JsonNode.Parse(request.Body!),
                JsonNode.Parse(
                    """
                    { "set": { "document-skills:pdf": "included",
                               "/workspace/.claude/skills/greet": "available",
                               "deploy-prod": "unavailable" },
                      "reset": true }
                    """
                )
            )
            .Should()
            .BeTrue(request.Body);
        catalog.ToolsListChanged.Should().BeTrue();
        catalog.AgentId.Should().Be("probe-child");
    }

    [Fact]
    public async Task PatchSkillTiersAsync_DefaultAgent_SendsNoAgentQuery_AndResetFalse()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Patch, SkillsPath, EmptyCatalogJson("", toolsListChanged: true));

        _ = await client.PatchSkillTiersAsync("sess-1", "", new Dictionary<string, SandboxSkillTier>());

        var request = handler.Requests.Single();
        request.Uri.PathAndQuery.Should().Be(SkillsPath);
        request.Body.Should().Be("""{"set":{},"reset":false}""");
    }

    [Fact]
    public async Task PatchSkillTiersAsync_ResponseWithoutToolsListChanged_IsProtocol()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Patch, SkillsPath, EmptyCatalogJson(""));

        var ex = await Assert.ThrowsAsync<SandboxException>(() =>
            client.PatchSkillTiersAsync(
                "sess-1",
                null,
                new Dictionary<string, SandboxSkillTier> { ["a:b"] = SandboxSkillTier.Included }
            )
        );

        ex.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    // Error bodies exactly as api/skills.rs builds them (ref_error_response / invalid_skills).
    [Theory]
    [InlineData(
        """{"error":"Skill not found: nope","code":400,"error_code":"skill_not_found","ref":"nope"}""",
        "skill_not_found",
        "nope",
        null
    )]
    [InlineData(
        """{"error":"Skill name 'pdf' is ambiguous; use one of: a:pdf, b:pdf","code":400,"error_code":"skill_ambiguous","ref":"pdf","candidates":["a:pdf","b:pdf"]}""",
        "skill_ambiguous",
        "pdf",
        "a:pdf,b:pdf"
    )]
    [InlineData(
        """{"error":"conflicting tiers for skill /m/p/skills/x: 'included' and 'available'","code":400,"error_code":"invalid_skills"}""",
        "invalid_skills",
        null,
        null
    )]
    public async Task PatchSkillTiersAsync_GatewayRejection_IsTypedSkillRejected(
        string body,
        string errorCode,
        string? skillRef,
        string? candidates
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Patch, SkillsPath, body, HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<SandboxException>(() =>
            client.PatchSkillTiersAsync(
                "sess-1",
                "probe-child",
                new Dictionary<string, SandboxSkillTier> { ["pdf"] = SandboxSkillTier.Included }
            )
        );

        ex.Kind.Should().Be(SandboxErrorKind.SkillRejected);
        ex.StatusCode.Should().Be(400);
        ex.ErrorCode.Should().Be(errorCode);
        ex.SkillRef.Should().Be(skillRef);
        if (candidates is null)
        {
            ex.SkillCandidates.Should().BeNull();
        }
        else
        {
            ex.SkillCandidates.Should().Equal(candidates.Split(','));
        }

        // The gateway's free-text error is never copied into the exception.
        ex.Message.Should().NotContain(JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreateAsync_UnresolvableIncludedSkill_IsSkillRejected()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Post,
            "/api/v1/sandboxes",
            """{"error":"Skill name 'pdf' is ambiguous; use one of: a:pdf, b:pdf","code":400,"error_code":"skill_ambiguous","ref":"pdf","candidates":["a:pdf","b:pdf"]}""",
            HttpStatusCode.BadRequest
        );

        var ex = await Assert.ThrowsAsync<SandboxException>(() =>
            client.CreateAsync(new SandboxCreateRequest("ws", skills: new SandboxSkillsPolicy(included: ["pdf"])))
        );

        ex.Kind.Should().Be(SandboxErrorKind.SkillRejected);
        ex.SkillCandidates.Should().Equal("a:pdf", "b:pdf");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("a/b")]
    public async Task SkillCalls_InvalidAgentId_ThrowBeforeSending(string agentId)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetSkillCatalogAsync("sess-1", agentId));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.PatchSkillTiersAsync("sess-1", agentId, new Dictionary<string, SandboxSkillTier>())
        );

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void SandboxSkillsPolicy_RejectsAnUnavailableDefaultTier() =>
        FluentActions
            .Invoking(() => new SandboxSkillsPolicy(SandboxSkillTier.Unavailable))
            .Should()
            .Throw<ArgumentException>();

    private static string EmptyCatalogJson(string agentId, bool toolsListChanged = false)
    {
        var changed = toolsListChanged ? ",\"tools_list_changed\":true" : string.Empty;
        return $$"""
            {"session_id":"sess-1","agent_id":"{{agentId}}","default_tier":"included",
             "counts":{"included":0,"available":0,"unavailable":0,"loaded":0},
             "marketplaces":[],"workspace":{"skills":[]}{{changed}}}
            """;
    }
}
