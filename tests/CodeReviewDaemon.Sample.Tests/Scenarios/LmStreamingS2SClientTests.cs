using System.Net;
using System.Net.Sockets;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Unit tests for <see cref="LmStreamingS2SClient"/> over a scripted <see cref="FakeHttpMessageHandler"/>
/// (no network). They pin the wire contract the review host expects: provision sends
/// <c>ModeId="workspace-agent"</c> and attaches the S2S auth headers (<c>X-S2S-Auth</c> +
/// the <c>X-Sbx-App-*</c> gateway-credential passthrough) on every request; the workspace and
/// message/status round-trips parse the server's camelCase JSON; the retention delete is 404-tolerant so an
/// already-gone conversation is not retried forever; and a connection-refused socket error surfaces as the
/// actionable <see cref="S2SConnectionException"/> rather than a raw transport failure.
/// </summary>
public sealed class LmStreamingS2SClientTests
{
    private static HttpClient NewHttp(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://localhost:5051/") };

    [Fact]
    public async Task ProvisionAsync_sends_workspace_agent_mode_and_attaches_the_auth_headers()
    {
        string? capturedS2SAuth = null;
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.Method == HttpMethod.Post
                    && req.RequestUri!.ToString().Contains("api/conversations", StringComparison.Ordinal),
                req =>
                {
                    capturedS2SAuth = req.Headers.TryGetValues("X-S2S-Auth", out var values)
                        ? values.FirstOrDefault()
                        : null;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"threadId\":\"thread-abc123\"}"),
                    };
                });
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(
            http, s2sSecret: "s2s-secret", sandboxAppId: "codereview-daemon", sandboxAppKey: "sbx-key");

        var threadId = await client.ProvisionAsync(
            "ws-1", "openai", "workspace-agent", "REVIEW METHODOLOGY", "gpt-5.6-sol", CancellationToken.None);

        threadId.Should().Be("thread-abc123");
        var recorded = handler.Requests.Should().ContainSingle().Subject;
        recorded.Body.Should().Contain("\"workspaceId\":\"ws-1\"")
            .And.Contain("\"providerId\":\"openai\"")
            .And.Contain("\"modeId\":\"workspace-agent\"")
            // The review profile's system prompt is the only channel that COULD carry the daemon's
            // methodology, sub-agent dispatch instruction and output contract — provision carries no
            // per-turn model or tool overrides. This asserts the daemon SENDS it. It does not assert the
            // host applies it, and today the host does not: the value is stored in thread metadata and
            // never read back (#49). Do not read a green here as "the methodology reached the agent".
            .And.Contain("\"systemPromptAppendix\":\"REVIEW METHODOLOGY\"")
            // The configured sub-agent model rides the same call. Provision is the only moment it can be
            // set: the host builds a thread's sub-agent options once, when it creates the agent.
            .And.Contain("\"subAgentModelId\":\"gpt-5.6-sol\"");
        // The sandbox binds to whatever app id the daemon forwards — both passthrough headers must ride the call.
        recorded.SbxAppId.Should().Be("codereview-daemon");
        recorded.SbxAppKey.Should().Be("sbx-key");
        capturedS2SAuth.Should().Be("s2s-secret");
    }

    [Fact]
    public async Task ProvisionAsync_omits_the_auth_headers_when_no_secret_or_app_credentials_are_configured()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-x\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, s2sSecret: null, sandboxAppId: null, sandboxAppKey: null);

        _ = await client.ProvisionAsync(
            "ws-1", "openai", "workspace-agent",
            systemPromptAppendix: null, subAgentModelId: null, CancellationToken.None);

        var recorded = handler.Requests.Should().ContainSingle().Subject;
        recorded.SbxAppId.Should().BeNull();
        recorded.SbxAppKey.Should().BeNull();
        // A caller with no instructions sends an explicit null, which the host treats as absent.
        recorded.Body.Should().Contain("\"systemPromptAppendix\":null");
        // Same for an unconfigured sub-agent model: an explicit null, never an empty string. A host that
        // stored "" would then hand every spawn a blank model id instead of leaving it to inherit.
        recorded.Body.Should().Contain("\"subAgentModelId\":null");
    }

    [Fact]
    public async Task ProvisionAsync_sends_a_blank_sub_agent_model_as_null_rather_than_an_empty_string()
    {
        // CodeReviewDaemonOptions.SubAgentModelId defaults to "" — not null — so the unconfigured daemon
        // hits this path on every provision, and it is the path that must not put "" on the wire.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-y\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, s2sSecret: null, sandboxAppId: null, sandboxAppKey: null);

        _ = await client.ProvisionAsync(
            "ws-1", "openai", "workspace-agent",
            systemPromptAppendix: null, subAgentModelId: "   ", CancellationToken.None);

        handler.Requests.Should().ContainSingle().Subject.Body
            .Should().Contain("\"subAgentModelId\":null");
    }

    [Fact]
    public async Task ListWorkspacesAsync_and_CreateWorkspaceAsync_round_trip_the_workspace_json()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "api/workspaces",
                "[{\"id\":\"ws-1\",\"name\":\"Review PR #118\",\"directoryRelPath\":\"review-pr-118\","
                    + "\"marketplaces\":[\"code-reviewer\"]}]")
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"ws-2\",\"name\":\"Review PR #200\",\"directoryRelPath\":\"review-pr-200\","
                    + "\"marketplaces\":[\"code-reviewer\"]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var listed = await client.ListWorkspacesAsync(CancellationToken.None);
        var existing = listed.Should().ContainSingle().Subject;
        existing.Id.Should().Be("ws-1");
        existing.DirectoryRelPath.Should().Be("review-pr-118");
        existing.Marketplaces.Should().ContainSingle().Which.Should().Be("code-reviewer");

        var created = await client.CreateWorkspaceAsync(
            "Review PR #200", "review-pr-200", ["code-reviewer"], CancellationToken.None);
        created.Id.Should().Be("ws-2");
        created.DirectoryRelPath.Should().Be("review-pr-200");

        var postBody = handler.Requests.Single(r => r.Method == HttpMethod.Post).Body;
        postBody.Should().Contain("\"name\":\"Review PR #200\"")
            .And.Contain("\"directoryRelPath\":\"review-pr-200\"")
            .And.Contain("\"marketplaces\":[\"code-reviewer\"]");
    }

    [Fact]
    public async Task ListWorkspacesAsync_reads_the_gateway_catalog_envelope()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "api/workspaces",
                "{\"gateway\":{\"canonicalBaseUrl\":\"http://gateway\",\"appId\":\"review\","
                    + "\"available\":true,\"error\":null},\"workspaces\":[{\"id\":\"ws-1\","
                    + "\"name\":\"Review\",\"directoryRelPath\":\"review-slot-0\","
                    + "\"marketplaces\":[\"gb-plugins\"],\"isSystemDefined\":false,"
                    + "\"createdAt\":1,\"updatedAt\":1,\"compatibility\":\"compatible\","
                    + "\"unsupportedMarketplaces\":[]}]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var listed = await client.ListWorkspacesAsync(CancellationToken.None);

        listed.Should().ContainSingle().Which.DirectoryRelPath.Should().Be("review-slot-0");
    }

    [Fact]
    public async Task SendMessageAsync_then_GetStatusByInputIdAsync_round_trip_the_run_status()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}")
            .OnJson(
                HttpMethod.Get,
                "/status",
                "{\"status\":\"Completed\",\"runId\":\"run-9\",\"response\":{\"text\":\"LGTM, ship it.\"}}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var inputId = await client.SendMessageAsync(
            "thread-1", "review this PR", suppressSubAgentSpawning: false, idempotencyKey: null,
            CancellationToken.None);
        inputId.Should().Be("input-1");

        var status = await client.GetStatusByInputIdAsync("thread-1", "input-1", CancellationToken.None);
        status.Status.Should().Be("Completed");
        status.RunId.Should().Be("run-9");
        status.ResponseText.Should().Be("LGTM, ship it.");
    }

    /// <summary>
    /// The suppression request must reach the host on the wire — a caller that only asserted on the response
    /// would pass against a client that never sent it.
    /// </summary>
    [Fact]
    public async Task SendMessageAsync_puts_the_suppression_flag_on_the_wire_and_accepts_the_hosts_acknowledgement()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\",\"spawningSuppressed\":true}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var inputId = await client.SendMessageAsync(
            "thread-1", "synthesize", suppressSubAgentSpawning: true, idempotencyKey: null,
            CancellationToken.None);

        inputId.Should().Be("input-1");
        handler.Requests.Single().Body.Should().Contain("\"suppressSubAgentSpawning\":true");
    }

    /// <summary>
    /// Version safety: a host that predates the field ignores the unknown request property and returns a
    /// perfectly normal 202. Without the acknowledgement check the daemon would run its synthesis turn
    /// believing in a guarantee that was never made, so a missing echo has to fail the send.
    /// </summary>
    [Fact]
    public async Task SendMessageAsync_fails_closed_when_the_host_does_not_acknowledge_the_suppression()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.SendMessageAsync(
            "thread-1", "synthesize", suppressSubAgentSpawning: true, idempotencyKey: null,
            CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*spawningSuppressed*");
    }

    /// <summary>The key has to reach the host, and the host's echo is what proves the send is REPEATABLE:
    /// only a host that adopted the key can hand the same input back after a lost response.</summary>
    [Fact]
    public async Task SendMessageAsync_puts_the_idempotency_key_on_the_wire_and_accepts_the_hosts_acknowledgement()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post, "/messages", "{\"inputId\":\"turn-key-1\",\"idempotencyKeyHonored\":true}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var inputId = await client.SendMessageAsync(
            "thread-1", "synthesize", suppressSubAgentSpawning: false, idempotencyKey: "turn-key-1",
            CancellationToken.None);

        inputId.Should().Be("turn-key-1");
        handler.Requests.Single().Body.Should().Contain("\"idempotencyKey\":\"turn-key-1\"");
    }

    /// <summary>
    /// The same version trap as the suppression flag, with a worse failure: a host predating the field mints
    /// its own input id and returns a normal 202, so the daemon would believe the send was safe to repeat when
    /// repeating it queues a second minutes-long, sub-agent-fanning review turn.
    /// </summary>
    [Fact]
    public async Task SendMessageAsync_fails_closed_when_the_host_does_not_acknowledge_the_idempotency_key()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"host-minted-id\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.SendMessageAsync(
            "thread-1", "synthesize", suppressSubAgentSpawning: false, idempotencyKey: "turn-key-1",
            CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*idempotencyKeyHonored*");
    }

    /// <summary>An ordinary send asks for nothing, so an un-acknowledging host is fine.</summary>
    [Fact]
    public async Task SendMessageAsync_does_not_require_an_acknowledgement_when_suppression_was_not_requested()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var inputId = await client.SendMessageAsync(
            "thread-1", "review this PR", suppressSubAgentSpawning: false, idempotencyKey: null,
            CancellationToken.None);

        inputId.Should().Be("input-1");
        handler.Requests.Single().Body.Should().Contain("\"suppressSubAgentSpawning\":false");
    }

    [Fact]
    public async Task GetStatusByInputIdAsync_returns_null_response_text_while_the_run_is_still_in_progress()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/status", "{\"status\":\"InProgress\",\"runId\":\"run-9\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var status = await client.GetStatusByInputIdAsync("thread-1", "input-1", CancellationToken.None);

        status.Status.Should().Be("InProgress");
        status.ResponseText.Should().BeNull("the final assistant text is absent until the run is terminal");
    }

    [Fact]
    public async Task DeleteConversationAsync_discards_the_conversation_and_carries_the_auth_headers()
    {
        string? capturedS2SAuth = null;
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.Method == HttpMethod.Delete,
                req =>
                {
                    capturedS2SAuth = req.Headers.TryGetValues("X-S2S-Auth", out var values)
                        ? values.FirstOrDefault()
                        : null;
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                });
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(
            http, s2sSecret: "s2s-secret", sandboxAppId: "codereview-daemon", sandboxAppKey: "sbx-key");

        var deleted = await client.DeleteConversationAsync("thread-abc123", CancellationToken.None);

        deleted.Should().BeTrue();
        var recorded = handler.Requests.Should().ContainSingle().Subject;
        recorded.Uri.ToString().Should().Be("http://localhost:5051/api/conversations/thread-abc123");
        // The delete route is [InboundS2SAuth]-guarded like every other S2S route, so the same three headers
        // must ride it — a delete that 401s would look to the sweeper like a transient failure forever.
        capturedS2SAuth.Should().Be("s2s-secret");
        recorded.SbxAppId.Should().Be("codereview-daemon");
        recorded.SbxAppKey.Should().Be("sbx-key");
    }

    [Fact]
    public async Task DeleteConversationAsync_reports_an_already_gone_conversation_rather_than_throwing()
    {
        // The review host 404s a thread it no longer has (deleted by hand, host storage reset). That is the
        // state the retention sweep wanted, reached by another route — not an error to retry forever.
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Delete, "api/conversations", "{}", HttpStatusCode.NotFound);
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var deleted = await client.DeleteConversationAsync("thread-gone", CancellationToken.None);

        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteConversationAsync_throws_on_a_server_error_so_the_caller_can_retry()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Delete, "api/conversations", "boom", HttpStatusCode.InternalServerError);
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.DeleteConversationAsync("thread-1", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task A_connection_refused_socket_error_surfaces_as_S2SConnectionException()
    {
        // The review host isn't listening: the handler throws a bare ConnectionRefused SocketException, which
        // HttpClient propagates unwrapped. The client must translate that into the actionable "start it" error.
        var handler = new FakeHttpMessageHandler()
            .On(_ => true, _ => throw new SocketException((int)SocketError.ConnectionRefused));
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.ListWorkspacesAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<S2SConnectionException>())
            .Which.Message.Should().Contain("http://localhost:5051");
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_MapsEveryFieldAndAttachesTheAuthHeaders_ForASchemaV1Graph()
    {
        string? capturedS2SAuth = null;
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.Method == HttpMethod.Get
                    && req.RequestUri!.ToString().Contains("subagents?recursive=true", StringComparison.Ordinal),
                req =>
                {
                    capturedS2SAuth = req.Headers.TryGetValues("X-S2S-Auth", out var values)
                        ? values.FirstOrDefault()
                        : null;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"name\":\"reviewer-a\","
                                + "\"template\":\"reviewer\",\"task\":\"review file X\",\"status\":\"completed\","
                                + "\"threadId\":\"thread-a1\",\"lastActivityUtc\":\"2026-01-01T00:00:00Z\","
                                + "\"parentThreadId\":\"thread-root\",\"depth\":1,"
                                + "\"terminalAtUtc\":\"2026-01-01T00:05:00Z\",\"failureCode\":null}]}"),
                    };
                });
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(
            http, s2sSecret: "s2s-secret", sandboxAppId: "codereview-daemon", sandboxAppKey: "sbx-key");

        var snapshot = await client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        var node = snapshot.Nodes.Should().ContainSingle().Subject;
        node.AgentId.Should().Be("a1");
        node.Name.Should().Be("reviewer-a");
        node.Template.Should().Be("reviewer");
        node.ThreadId.Should().Be("thread-a1");
        node.ParentThreadId.Should().Be("thread-root");
        node.Depth.Should().Be(1);
        node.Status.Should().Be(ReviewSubAgentStatus.Completed);
        node.TerminalAtUtc.Should().NotBeNull();
        node.FailureCode.Should().BeNull();

        var recorded = handler.Requests.Should().ContainSingle().Subject;
        recorded.Uri.ToString().Should().Contain("api/conversations/thread-root/subagents?recursive=true");
        // Same S2S/app auth headers as every other request on this client — never logged, only in headers.
        capturedS2SAuth.Should().Be("s2s-secret");
        recorded.SbxAppId.Should().Be("codereview-daemon");
        recorded.SbxAppKey.Should().Be("sbx-key");
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_ReadsTheModelRoutingFieldsWhenTheHostReportsThem()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "subagents",
                "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                    + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                    + "\"status\":\"completed\",\"effectiveModelId\":\"gpt-5.6-sol\","
                    + "\"effectiveModelIntelligence\":3,\"modelSelectionSource\":\"template-tier\"}]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var snapshot = await client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        var node = snapshot.Nodes.Should().ContainSingle().Subject;
        node.EffectiveModelId.Should().Be("gpt-5.6-sol");
        node.EffectiveModelIntelligence.Should().Be(3);
        node.ModelSelectionSource.Should().Be("template-tier");
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_ParsesANodeFromAHostThatPredatesTheModelFields()
    {
        // The compatibility case, and the reason those three are read with OptionalString/OptionalInt rather
        // than the Require* helpers beside them. The daemon and the S2S host deploy independently, so a new
        // daemon polls an old host that omits all three. Reading them as required would turn that omission
        // into a throw at the settlement barrier and fail the whole review over presentation fields — and
        // bumping schemaVersion instead would fail EVERY review against EVERY not-yet-updated host.
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "subagents",
                "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                    + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                    + "\"status\":\"completed\"}]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var snapshot = await client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        var node = snapshot.Nodes.Should().ContainSingle().Subject;
        node.AgentId.Should().Be("a1", "the node still parses in full — nothing else degrades");
        node.Status.Should().Be(ReviewSubAgentStatus.Completed);
        node.EffectiveModelId.Should().BeNull("absent is not a model, and must not become one downstream");
        node.EffectiveModelIntelligence.Should().BeNull();
        node.ModelSelectionSource.Should().BeNull();
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_TreatsAnUnreadableModelTierAsAbsentRatherThanFailingTheNode()
    {
        // A tier that arrives as a string (or anything non-integral) is a host that changed its mind about
        // the shape. Losing the roster over it would cost the review; losing the tier costs a table cell.
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "subagents",
                "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                    + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                    + "\"status\":\"completed\",\"effectiveModelId\":\"gpt-5.6-luna\","
                    + "\"effectiveModelIntelligence\":\"three\"}]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var snapshot = await client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        var node = snapshot.Nodes.Should().ContainSingle().Subject;
        node.EffectiveModelId.Should().Be("gpt-5.6-luna", "the readable half is still evidence");
        node.EffectiveModelIntelligence.Should().BeNull();
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_FailsClosed_OnTheOldFlatPreVersionedArrayShape()
    {
        // The non-recursive (pre-Task-2) endpoint returns a bare SubAgentSummary[] with no envelope at
        // all — a daemon polling a not-yet-upgraded host must not silently misread that as an empty tree.
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "subagents",
                "[{\"agentId\":\"a1\",\"template\":\"reviewer\",\"task\":\"t\",\"status\":\"completed\","
                    + "\"threadId\":\"thread-a1\"}]");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_FailsClosed_WhenSchemaVersionIsAbsent()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "subagents", "{\"nodes\":[]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_FailsClosed_WhenSchemaVersionIsUnsupported()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "subagents", "{\"schemaVersion\":2,\"nodes\":[]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_FailsClosed_WhenANodeIsMissingARequiredRelationshipField()
    {
        // "parentThreadId" is missing — a required relationship field on the recursive contract (unlike
        // the flat, non-recursive SubAgentSummary shape, where it is optional).
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "subagents",
                "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\",\"depth\":1,"
                    + "\"template\":\"reviewer\",\"status\":\"completed\"}]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_MapsAnUnrecognizedStatusStringToUnknown_WithoutThrowing()
    {
        // A malformed/new wire status must fail closed onto the NONTERMINAL Unknown status (which keeps
        // the barrier waiting) rather than throwing a JSON enum error or silently defaulting to terminal.
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "subagents",
                "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                    + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                    + "\"status\":\"Paused\"}]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var snapshot = await client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        snapshot.Nodes.Should().ContainSingle().Which.Status.Should().Be(ReviewSubAgentStatus.Unknown);
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_MapsARecognizedStatusStringCaseInsensitively()
    {
        // ParseNodeStatus's doc comment promises case-insensitive matching for recognized values, not just
        // the always-lowercase wire convention — pin a PascalCase status so a regression that drops the
        // ToLowerInvariant() normalization (and would otherwise fail closed to Unknown) is caught here.
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "subagents",
                "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                    + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                    + "\"status\":\"Completed\"}]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var snapshot = await client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        snapshot.Nodes.Should().ContainSingle().Which.Status.Should().Be(ReviewSubAgentStatus.Completed);
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_FailsClosed_WhenARequiredRelationshipFieldIsAnEmptyString()
    {
        // An empty "agentId" is present in the JSON shape but semantically absent — the same fail-closed
        // guarantee that applies to a missing relationship field must apply here too, matching the stricter
        // empty-string rejection this file already applies via ReadStringProperty for other endpoints.
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "subagents",
                "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"\",\"threadId\":\"thread-a1\","
                    + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                    + "\"status\":\"completed\"}]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// The review host must run with <c>AgentCollaboration__Enabled=true</c>, and the LmStreaming sample's
    /// shipped DEFAULT is off — so the misconfiguration is the easy one to fall into and it is completely
    /// silent. With collaboration off, the agent-transcript route 404s and
    /// <c>ReviewNotesArtifactBuilder</c> writes every delegate's note as a ~750-byte stub reading "The daemon
    /// could not read this transcript from the review host". Observed live on PRs 5501480 and 5501629: the
    /// lead-reviewer note was full (the daemon owns that conversation directly) while all five delegate notes
    /// were stubs. It is not retroactive either — the hierarchy rows a collaboration-off host persisted carry
    /// no node record, so those PRs stay stubbed forever. Reviews complete, notes are written, everything
    /// reads as success, and the sub-agents' reasoning is simply gone.
    /// </summary>
    [Fact]
    public async Task EnsureAgentCollaborationAsync_throws_when_the_host_reports_collaboration_unavailable()
    {
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.RequestUri!.ToString().Contains("/transcript", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        "{\"error\":\"Agent collaboration is disabled.\",\"code\":\"collaboration_unavailable\"}"),
                });
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "secret", "app-id", "app-key");

        var act = () => client.EnsureAgentCollaborationAsync(CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<ReviewHostContractException>()).Which;
        thrown.Message.Should().Contain(
            "AgentCollaboration",
            "someone hitting this at 2am must be told the SETTING, not just that something is unavailable");
        thrown.Message.Should().Contain(
            "transcript",
            "and the route, so the claim is checkable against the host without reading this source");
        thrown.Message.Should().MatchRegex(
            "(?i)stub|could not read|sub-agent",
            "and the consequence — silently stubbed sub-agent notes is why this is worth failing startup over");
    }

    /// <summary>
    /// The discriminator. <c>api/conversations/capabilities</c> cannot answer this question — probed against
    /// the live host with collaboration ON it returns only schemaVersion/messageIdempotency/spawnSuppression
    /// and says nothing about collaboration — so the transcript route itself is the probe, and the error CODE
    /// is what separates the two 404s. <c>unknown_thread</c> means the route is live and merely does not know
    /// this conversation, which is the expected answer for a thread id that was never real: collaboration is
    /// on. Requiring no real conversation or agent is exactly what makes this usable at startup.
    /// </summary>
    [Fact]
    public async Task EnsureAgentCollaborationAsync_passes_when_the_route_reports_an_unknown_thread()
    {
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.RequestUri!.ToString().Contains("/transcript", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        "{\"error\":\"Conversation 'thread-doesnotexist000' not found.\",\"code\":\"unknown_thread\"}"),
                });
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "secret", "app-id", "app-key");

        var act = () => client.EnsureAgentCollaborationAsync(CancellationToken.None);

        await act.Should().NotThrowAsync(
            "the route answered, so collaboration is on — a 404 for a thread that never existed is the "
                + "CORRECT response and must not be read as the feature being off");
    }

    /// <summary>
    /// Fail-open on everything else, and this is the load-bearing half. A startup assertion that trips on an
    /// unrelated blip is worse than no assertion: it turns a transient hiccup into a daemon that will not
    /// boot, and the first thing anyone does with an alarm that cries wolf is remove it. Only the specific
    /// <c>collaboration_unavailable</c> code proves the feature is off; a 500, a timeout, an empty body or an
    /// error envelope this daemon does not recognise prove nothing at all.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{\"code\":\"something_else\"}")]
    [InlineData(HttpStatusCode.BadGateway, "")]
    [InlineData(HttpStatusCode.NotFound, "not json at all")]
    [InlineData(HttpStatusCode.OK, "[]")]
    public async Task EnsureAgentCollaborationAsync_passes_on_anything_that_is_not_that_exact_code(
        HttpStatusCode status, string body)
    {
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.RequestUri!.ToString().Contains("/transcript", StringComparison.Ordinal),
                _ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "secret", "app-id", "app-key");

        var act = () => client.EnsureAgentCollaborationAsync(CancellationToken.None);

        await act.Should().NotThrowAsync(
            "none of these establishes that collaboration is off, and refusing to start the daemon over an "
                + "ambiguous answer trades a silent defect for a loud false one");
    }

    /// <summary>
    /// The timeout the theory above only CLAIMED to cover. Its remark lists "a timeout" among the answers that
    /// prove nothing, but every case it actually runs is an HTTP status — so the one transport failure that
    /// reaches this code as an exception rather than a response was never exercised, and it was the one that
    /// broke.
    /// <para>
    /// <see cref="HttpClient"/> reports its own <see cref="HttpClient.Timeout"/> as a
    /// <see cref="TaskCanceledException"/> — which derives from <see cref="OperationCanceledException"/> — with
    /// an inner <see cref="TimeoutException"/> and the CALLER's token untouched. A fail-open filter written as
    /// <c>when (ex is not OperationCanceledException)</c> therefore does not catch it, and a review host that
    /// hangs rather than refusing the connection takes the whole daemon down at startup: 100 seconds of boot
    /// latency followed by a raw cancellation stack that names neither the host nor the setting. Measured — 13
    /// tests across the suite failed this way, every one of them a test that merely started the host.
    /// </para>
    /// <para>
    /// A hung host is a real problem and it is not THIS problem. The whole point of the probe is that only
    /// <c>collaboration_unavailable</c> is evidence.
    /// </para>
    /// </summary>
    [Fact]
    public async Task EnsureAgentCollaborationAsync_passes_when_the_request_times_out()
    {
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.RequestUri!.ToString().Contains("/transcript", StringComparison.Ordinal),
                _ => throw new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
                    new TimeoutException("A task was canceled.")));
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "secret", "app-id", "app-key");

        var act = () => client.EnsureAgentCollaborationAsync(CancellationToken.None);

        await act.Should().NotThrowAsync(
            "a host that hangs says nothing about whether collaboration is enabled, and a startup assertion "
                + "that cannot tell a slow host from a misconfigured one is an assertion nobody will keep");
    }

    /// <summary>
    /// The other half of the same filter, and the reason it cannot simply swallow every
    /// <see cref="OperationCanceledException"/>: when the daemon is genuinely shutting down, the probe must
    /// abandon rather than report a verdict it never obtained. The discriminator is the caller's token, not
    /// the exception type — those are the same type and opposite situations.
    /// </summary>
    [Fact]
    public async Task EnsureAgentCollaborationAsync_still_propagates_a_real_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.RequestUri!.ToString().Contains("/transcript", StringComparison.Ordinal),
                _ => throw new TaskCanceledException("cancelled"));
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "secret", "app-id", "app-key");

        var act = () => client.EnsureAgentCollaborationAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "shutdown is not a passing preflight — swallowing it would report the host as verified when the "
                + "probe never finished");
    }

    /// <summary>
    /// The startup assertion cannot be the only detection point, because it deliberately fails OPEN: it gives
    /// the host ten seconds and passes on anything it cannot establish. A daemon started alongside a review
    /// host that is still coming up therefore boots with the setting unverified — and a host restarted later
    /// is never re-checked at all. In both windows the regression is silent again.
    /// <para>
    /// This is the second net, and it runs when the host is definitely up: the live transcript read. Before
    /// this, that path went through <c>EnsureSuccessStatusCode</c>, which reports
    /// <c>404 (Not Found)</c> and DISCARDS the body — so the one field naming the cause never reached the log
    /// or the notes artifact, and the operator saw only an opaque stub. That is exactly the symptom the
    /// config comment described.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetAgentTranscriptAsync_names_the_setting_when_collaboration_is_disabled()
    {
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.RequestUri!.ToString().Contains("/transcript", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        "{\"error\":\"Agent collaboration is disabled.\",\"code\":\"collaboration_unavailable\"}"),
                });
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "secret", "app-id", "app-key");

        var act = () => client.GetAgentTranscriptAsync("thread-root", "agent-1", CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<ReviewHostContractException>()).Which;
        thrown.Message.Should().Contain(
            "AgentCollaboration",
            "the note this stub replaces is unreadable unless the message names the setting to change");
        thrown.Message.Should().MatchRegex(
            "(?i)startup",
            "and it must say the startup check let this through, or the operator re-runs the check that "
                + "already passed instead of looking at the host");
    }

    /// <summary>
    /// And the ordinary failure keeps its ordinary shape. A transcript that 404s for any other reason — an
    /// agent id the host does not know, a thread that has aged out — is not a misconfiguration, and dressing
    /// it up as one would send whoever reads the notes artifact to restart a host that is running correctly.
    /// </summary>
    [Fact]
    public async Task GetAgentTranscriptAsync_leaves_an_ordinary_404_alone()
    {
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.RequestUri!.ToString().Contains("/transcript", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"error\":\"forbidden\",\"code\":\"unknown_target\"}"),
                });
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "secret", "app-id", "app-key");

        var act = () => client.GetAgentTranscriptAsync("thread-root", "nope", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>(
            "only the collaboration code is evidence of the misconfiguration; every other 404 stays the "
                + "transport failure it is");
    }
}
