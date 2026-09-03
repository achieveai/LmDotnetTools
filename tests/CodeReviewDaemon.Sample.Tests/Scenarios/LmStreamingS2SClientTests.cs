using System.Globalization;
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
        var handler = new FakeHttpMessageHandler().On(
            req =>
                req.Method == HttpMethod.Post
                && req.RequestUri!.ToString().Contains("api/conversations", StringComparison.Ordinal),
            req =>
            {
                capturedS2SAuth = req.Headers.TryGetValues("X-S2S-Auth", out var values)
                    ? values.FirstOrDefault()
                    : null;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"threadId\":\"thread-abc123\",\"reasoningEffortAccepted\":true}"),
                };
            }
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(
            http,
            s2sSecret: "s2s-secret",
            sandboxAppId: "codereview-daemon",
            sandboxAppKey: "sbx-key"
        );

        var threadId = await client.ProvisionAsync(
            "ws-1",
            "openai",
            "workspace-agent",
            "REVIEW METHODOLOGY",
            "gpt-5.6-sol",
            "xhigh",
            CancellationToken.None
        );

        threadId.Should().Be("thread-abc123");
        var recorded = handler.Requests.Should().ContainSingle().Subject;
        recorded
            .Body.Should()
            .Contain("\"workspaceId\":\"ws-1\"")
            .And.Contain("\"providerId\":\"openai\"")
            .And.Contain("\"modeId\":\"workspace-agent\"")
            // The review profile's system prompt is the ONLY channel for the daemon's methodology, sub-agent
            // dispatch instruction and output contract — provision carries no model or tool overrides.
            // This asserts the daemon SENDS it; it does not assert the host APPLIES it. Those two claims
            // were conflated for the whole life of the field, during which the host stored the value and
            // read it back with nothing (#528). Application is proved by
            // LmStreaming.Sample.E2E.Tests.SystemPromptCompositionTests, which reads the composed prompt
            // off the outbound provider request. If that test is ever deleted, delete this assertion's
            // claim to meaning with it rather than leaving it green.
            .And.Contain("\"systemPromptAppendix\":\"REVIEW METHODOLOGY\"")
            // The configured sub-agent model rides the same call. Provision is the only moment it can be
            // set: the host builds a thread's sub-agent options once, when it creates the agent.
            .And.Contain("\"subAgentModelId\":\"gpt-5.6-sol\"")
            // Root effort is also conversation-scoped. It must cross S2S instead of falling back to the
            // provider default, which is only medium for the deployed GPT-5.6 models.
            .And.Contain("\"reasoningEffort\":\"xhigh\"");
        // The sandbox binds to whatever app id the daemon forwards — both passthrough headers must ride the call.
        recorded.SbxAppId.Should().Be("codereview-daemon");
        recorded.SbxAppKey.Should().Be("sbx-key");
        capturedS2SAuth.Should().Be("s2s-secret");
    }

    [Fact]
    public async Task ProvisionAsync_rejects_a_host_that_does_not_acknowledge_requested_reasoning_effort()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "api/conversations",
            "{\"threadId\":\"thread-old-host\"}"
        );
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () =>
            client.ProvisionAsync(
                "ws-1",
                "gpt-5.6-sol",
                "code-review-daemon",
                systemPromptAppendix: null,
                subAgentModelId: null,
                reasoningEffort: "xhigh",
                CancellationToken.None
            );

        var thrown = await act.Should().ThrowAsync<ReviewHostContractException>();
        thrown.Which.Message.Should().Contain("did not acknowledge");
        thrown.Which.Message.Should().Contain("reasoning effort");
        thrown.Which.Message.Should().Contain("xhigh");
    }

    [Fact]
    public async Task ProvisionAsync_deletes_a_minted_thread_when_effort_acknowledgement_is_missing()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Post, "api/conversations", "{\"threadId\":\"thread-old-host\"}")
            .OnJson(HttpMethod.Delete, "api/conversations/thread-old-host", "{}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () =>
            client.ProvisionAsync(
                "ws-1",
                "gpt-5.6-sol",
                "code-review-daemon",
                systemPromptAppendix: null,
                subAgentModelId: null,
                reasoningEffort: "xhigh",
                CancellationToken.None
            );

        _ = await act.Should().ThrowAsync<ReviewHostContractException>();
        handler
            .Requests.Should()
            .ContainSingle(r =>
                r.Method == HttpMethod.Delete
                && r.Uri.ToString().EndsWith("api/conversations/thread-old-host", StringComparison.Ordinal)
            );
    }

    [Fact]
    public async Task ProvisionAsync_turns_an_invalid_effort_400_into_a_bounded_contract_error()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "api/conversations",
            "{\"error\":\"reasoning_effort_invalid\",\"code\":\"reasoning_effort_invalid\"}",
            HttpStatusCode.BadRequest
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, s2sSecret: null, sandboxAppId: null, sandboxAppKey: null);

        var act = () =>
            client.ProvisionAsync(
                "ws-1",
                "gpt-5.6-sol",
                "code-review-daemon",
                systemPromptAppendix: null,
                subAgentModelId: null,
                reasoningEffort: "turbo",
                CancellationToken.None
            );

        var thrown = await act.Should().ThrowAsync<ReviewHostContractException>();
        thrown.Which.Message.Should().Contain("reasoning_effort_invalid");
        thrown.Which.Message.Should().Contain("turbo");
    }

    [Fact]
    public async Task ProvisionAsync_omits_the_auth_headers_when_no_secret_or_app_credentials_are_configured()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "api/conversations",
            "{\"threadId\":\"thread-x\"}"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, s2sSecret: null, sandboxAppId: null, sandboxAppKey: null);

        _ = await client.ProvisionAsync(
            "ws-1",
            "openai",
            "workspace-agent",
            systemPromptAppendix: null,
            subAgentModelId: null,
            reasoningEffort: null,
            CancellationToken.None
        );

        var recorded = handler.Requests.Should().ContainSingle().Subject;
        recorded.SbxAppId.Should().BeNull();
        recorded.SbxAppKey.Should().BeNull();
        // A caller with no instructions sends an explicit null, which the host treats as absent.
        recorded.Body.Should().Contain("\"systemPromptAppendix\":null");
        // Same for an unconfigured sub-agent model: an explicit null, never an empty string. A host that
        // stored "" would then hand every spawn a blank model id instead of leaving it to inherit.
        recorded.Body.Should().Contain("\"subAgentModelId\":null");
        // Null means "use the host/provider default" and must stay distinct from explicit empty below.
        recorded.Body.Should().Contain("\"reasoningEffort\":null");
    }

    [Fact]
    public async Task ProvisionAsync_preserves_an_explicit_empty_reasoning_effort_on_the_wire()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "api/conversations",
            "{\"threadId\":\"thread-effort-empty\",\"reasoningEffortAccepted\":true}"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, s2sSecret: null, sandboxAppId: null, sandboxAppKey: null);

        _ = await client.ProvisionAsync(
            "ws-1",
            "openai",
            "workspace-agent",
            systemPromptAppendix: null,
            subAgentModelId: null,
            reasoningEffort: string.Empty,
            CancellationToken.None
        );

        handler.Requests.Should().ContainSingle().Subject.Body.Should().Contain("\"reasoningEffort\":\"\"");
    }

    [Fact]
    public async Task ProvisionAsync_empty_reasoning_effort_still_requires_host_acknowledgement()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "api/conversations",
            "{\"threadId\":\"thread-effort-unacknowledged\"}"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, s2sSecret: null, sandboxAppId: null, sandboxAppKey: null);

        var act = () =>
            client.ProvisionAsync(
                "ws-1",
                "openai",
                "workspace-agent",
                systemPromptAppendix: null,
                subAgentModelId: null,
                reasoningEffort: string.Empty,
                CancellationToken.None
            );

        var thrown = await act.Should().ThrowAsync<ReviewHostContractException>();
        thrown.Which.Message.Should().Contain("did not acknowledge requested root reasoning effort");
    }

    /// <summary>
    /// #628: an unresolvable mode id used to surface as a bare <c>HttpRequestException</c> ("404")
    /// naming neither the mode nor the host. It is a configuration/contract failure (bounded
    /// retries), and the message must name both so the operator can fix
    /// <c>CodeReviewDaemon:LmStreamingModeId</c> or the host's Prompts.yaml without spelunking.
    /// </summary>
    [Fact]
    public async Task ProvisionAsync_turns_a_404_into_a_contract_error_naming_the_mode_id_and_the_host()
    {
        var handler = new FakeHttpMessageHandler().On(
            req =>
                req.Method == HttpMethod.Post
                && req.RequestUri!.ToString().Contains("api/conversations", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"Mode 'code-review-daemon' not found.\"}"),
            }
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, s2sSecret: null, sandboxAppId: null, sandboxAppKey: null);

        var act = () =>
            client.ProvisionAsync(
                "ws-1",
                "openai",
                "code-review-daemon",
                systemPromptAppendix: null,
                subAgentModelId: null,
                reasoningEffort: null,
                CancellationToken.None
            );

        var thrown = await act.Should().ThrowAsync<ReviewHostContractException>();
        thrown.Which.Message.Should().Contain("code-review-daemon", "the message must name the configured mode id");
        thrown.Which.Message.Should().Contain("http://localhost:5051", "the message must name the review host");
        thrown.Which.Message.Should().Contain("LmStreamingModeId", "the message must point at the config key to fix");
    }

    [Fact]
    public async Task ProvisionAsync_sends_a_blank_sub_agent_model_as_null_rather_than_an_empty_string()
    {
        // CodeReviewDaemonOptions.SubAgentModelId defaults to "" — not null — so the unconfigured daemon
        // hits this path on every provision, and it is the path that must not put "" on the wire.
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "api/conversations",
            "{\"threadId\":\"thread-y\"}"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, s2sSecret: null, sandboxAppId: null, sandboxAppKey: null);

        _ = await client.ProvisionAsync(
            "ws-1",
            "openai",
            "workspace-agent",
            systemPromptAppendix: null,
            subAgentModelId: "   ",
            reasoningEffort: null,
            CancellationToken.None
        );

        handler.Requests.Should().ContainSingle().Subject.Body.Should().Contain("\"subAgentModelId\":null");
    }

    [Fact]
    public async Task ListWorkspacesAsync_and_CreateWorkspaceAsync_round_trip_the_workspace_json()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(
                HttpMethod.Get,
                "api/workspaces",
                "[{\"id\":\"ws-1\",\"name\":\"Review PR #118\",\"directoryRelPath\":\"review-pr-118\","
                    + "\"marketplaces\":[\"code-reviewer\"]}]"
            )
            .OnJson(
                HttpMethod.Post,
                "api/workspaces",
                "{\"id\":\"ws-2\",\"name\":\"Review PR #200\",\"directoryRelPath\":\"review-pr-200\","
                    + "\"marketplaces\":[\"code-reviewer\"]}"
            );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var listed = await client.ListWorkspacesAsync(CancellationToken.None);
        var existing = listed.Should().ContainSingle().Subject;
        existing.Id.Should().Be("ws-1");
        existing.DirectoryRelPath.Should().Be("review-pr-118");
        existing.Marketplaces.Should().ContainSingle().Which.Should().Be("code-reviewer");

        var created = await client.CreateWorkspaceAsync(
            "Review PR #200",
            "review-pr-200",
            ["code-reviewer"],
            CancellationToken.None
        );
        created.Id.Should().Be("ws-2");
        created.DirectoryRelPath.Should().Be("review-pr-200");

        var postBody = handler.Requests.Single(r => r.Method == HttpMethod.Post).Body;
        postBody
            .Should()
            .Contain("\"name\":\"Review PR #200\"")
            .And.Contain("\"directoryRelPath\":\"review-pr-200\"")
            .And.Contain("\"marketplaces\":[\"code-reviewer\"]");
    }

    [Fact]
    public async Task ListWorkspacesAsync_reads_the_gateway_catalog_envelope()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "api/workspaces",
            "{\"gateway\":{\"canonicalBaseUrl\":\"http://gateway\",\"appId\":\"review\","
                + "\"available\":true,\"error\":null},\"workspaces\":[{\"id\":\"ws-1\","
                + "\"name\":\"Review\",\"directoryRelPath\":\"review-slot-0\","
                + "\"marketplaces\":[\"gb-plugins\"],\"isSystemDefined\":false,"
                + "\"createdAt\":1,\"updatedAt\":1,\"compatibility\":\"compatible\","
                + "\"unsupportedMarketplaces\":[]}]}"
        );
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
                "{\"status\":\"Completed\",\"runId\":\"run-9\",\"response\":{\"text\":\"LGTM, ship it.\"}}"
            );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var inputId = await client.SendMessageAsync(
            "thread-1",
            "review this PR",
            suppressSubAgentSpawning: false,
            idempotencyKey: null,
            CancellationToken.None
        );
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
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/messages",
            "{\"inputId\":\"input-1\",\"spawningSuppressed\":true}"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var inputId = await client.SendMessageAsync(
            "thread-1",
            "synthesize",
            suppressSubAgentSpawning: true,
            idempotencyKey: null,
            CancellationToken.None
        );

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
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () =>
            client.SendMessageAsync(
                "thread-1",
                "synthesize",
                suppressSubAgentSpawning: true,
                idempotencyKey: null,
                CancellationToken.None
            );

        _ = await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*spawningSuppressed*");
    }

    /// <summary>The key has to reach the host, and the host's echo is what proves the send is REPEATABLE:
    /// only a host that adopted the key can hand the same input back after a lost response.</summary>
    [Fact]
    public async Task SendMessageAsync_puts_the_idempotency_key_on_the_wire_and_accepts_the_hosts_acknowledgement()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/messages",
            "{\"inputId\":\"turn-key-1\",\"idempotencyKeyHonored\":true}"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var inputId = await client.SendMessageAsync(
            "thread-1",
            "synthesize",
            suppressSubAgentSpawning: false,
            idempotencyKey: "turn-key-1",
            CancellationToken.None
        );

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
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Post,
            "/messages",
            "{\"inputId\":\"host-minted-id\"}"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () =>
            client.SendMessageAsync(
                "thread-1",
                "synthesize",
                suppressSubAgentSpawning: false,
                idempotencyKey: "turn-key-1",
                CancellationToken.None
            );

        _ = await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*idempotencyKeyHonored*");
    }

    /// <summary>An ordinary send asks for nothing, so an un-acknowledging host is fine.</summary>
    [Fact]
    public async Task SendMessageAsync_does_not_require_an_acknowledgement_when_suppression_was_not_requested()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Post, "/messages", "{\"inputId\":\"input-1\"}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var inputId = await client.SendMessageAsync(
            "thread-1",
            "review this PR",
            suppressSubAgentSpawning: false,
            idempotencyKey: null,
            CancellationToken.None
        );

        inputId.Should().Be("input-1");
        handler.Requests.Single().Body.Should().Contain("\"suppressSubAgentSpawning\":false");
    }

    [Fact]
    public async Task GetStatusByInputIdAsync_returns_null_response_text_while_the_run_is_still_in_progress()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "/status",
            "{\"status\":\"InProgress\",\"runId\":\"run-9\"}"
        );
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
        var handler = new FakeHttpMessageHandler().On(
            req => req.Method == HttpMethod.Delete,
            req =>
            {
                capturedS2SAuth = req.Headers.TryGetValues("X-S2S-Auth", out var values)
                    ? values.FirstOrDefault()
                    : null;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(
            http,
            s2sSecret: "s2s-secret",
            sandboxAppId: "codereview-daemon",
            sandboxAppKey: "sbx-key"
        );

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
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Delete,
            "api/conversations",
            "{}",
            HttpStatusCode.NotFound
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var deleted = await client.DeleteConversationAsync("thread-gone", CancellationToken.None);

        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteConversationAsync_throws_on_a_server_error_so_the_caller_can_retry()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Delete,
            "api/conversations",
            "boom",
            HttpStatusCode.InternalServerError
        );
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
        var handler = new FakeHttpMessageHandler().On(
            _ => true,
            _ => throw new SocketException((int)SocketError.ConnectionRefused)
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.ListWorkspacesAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<S2SConnectionException>())
            .Which.Message.Should()
            .Contain("http://localhost:5051");
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_MapsEveryFieldAndAttachesTheAuthHeaders_ForASchemaV1Graph()
    {
        string? capturedS2SAuth = null;
        var handler = new FakeHttpMessageHandler().On(
            req =>
                req.Method == HttpMethod.Get
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
                            + "\"terminalAtUtc\":\"2026-01-01T00:05:00Z\",\"failureCode\":null}]}"
                    ),
                };
            }
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(
            http,
            s2sSecret: "s2s-secret",
            sandboxAppId: "codereview-daemon",
            sandboxAppKey: "sbx-key"
        );

        var snapshot = await client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        var node = snapshot.Nodes.Should().ContainSingle().Subject;
        node.AgentId.Should().Be("a1");
        node.Name.Should().Be("reviewer-a");
        node.Template.Should().Be("reviewer");
        node.ThreadId.Should().Be("thread-a1");
        node.ParentThreadId.Should().Be("thread-root");
        node.Depth.Should().Be(1);
        node.Status.Should().Be(ReviewSubAgentStatus.Completed);
        node.TerminalAtUtc.Should().Be(DateTimeOffset.Parse("2026-01-01T00:05:00Z", CultureInfo.InvariantCulture));
        // The VALUE, not just its presence. This one drives the barrier's unknown-node quiescence escape
        // hatch, which is the only thing that can open a barrier over a node the host could not resolve --
        // so a mapping that silently produced null here would hold every such review open for its full
        // deadline and nothing in the barrier's own tests, which build nodes directly, would notice.
        node.LastActivityUtc.Should()
            .Be(DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture));
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
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "subagents",
            "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                + "\"status\":\"completed\",\"effectiveModelId\":\"gpt-5.6-sol\","
                + "\"effectiveModelIntelligence\":5,\"modelSelectionSource\":\"spawn-tier\","
                + "\"requestedReasoningEffort\":\"xhigh\",\"shapedReasoningEffort\":\"xhigh\"}]}"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var snapshot = await client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        var node = snapshot.Nodes.Should().ContainSingle().Subject;
        node.EffectiveModelId.Should().Be("gpt-5.6-sol");
        node.EffectiveModelIntelligence.Should().Be(5);
        node.ModelSelectionSource.Should().Be("spawn-tier");
        node.RequestedReasoningEffort.Should().Be("xhigh");
        node.ShapedReasoningEffort.Should().Be("xhigh");
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_NormalizesProviderOnlyShapedReasoningEffort()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "subagents",
            "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                + "\"status\":\"completed\",\"shapedReasoningEffort\":\" MAX \"}]}"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var snapshot = await client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        var node = snapshot.Nodes.Should().ContainSingle().Subject;
        node.RequestedReasoningEffort.Should().BeNull();
        node.ShapedReasoningEffort.Should().Be("max");
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_DropsNonCanonicalReasoningEffortTelemetry()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "subagents",
            "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                + "\"status\":\"completed\",\"requestedReasoningEffort\":\"xhigh | Status | approved\","
                + "\"shapedReasoningEffort\":\"max | Status | approved\"}]}"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var snapshot = await client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        var node = snapshot.Nodes.Should().ContainSingle().Subject;
        node.RequestedReasoningEffort.Should().BeNull();
        node.ShapedReasoningEffort.Should().BeNull();
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_ParsesANodeFromAHostThatPredatesTheModelFields()
    {
        // The compatibility case, and the reason those three are read with OptionalString/OptionalInt rather
        // than the Require* helpers beside them. The daemon and the S2S host deploy independently, so a new
        // daemon polls an old host that omits all three. Reading them as required would turn that omission
        // into a throw at the settlement barrier and fail the whole review over presentation fields — and
        // bumping schemaVersion instead would fail EVERY review against EVERY not-yet-updated host.
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "subagents",
            "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                + "\"status\":\"completed\"}]}"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var snapshot = await client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        var node = snapshot.Nodes.Should().ContainSingle().Subject;
        node.AgentId.Should().Be("a1", "the node still parses in full — nothing else degrades");
        node.Status.Should().Be(ReviewSubAgentStatus.Completed);
        node.EffectiveModelId.Should().BeNull("absent is not a model, and must not become one downstream");
        node.EffectiveModelIntelligence.Should().BeNull();
        node.ModelSelectionSource.Should().BeNull();
        node.RequestedReasoningEffort.Should().BeNull();
        node.ShapedReasoningEffort.Should().BeNull();
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_TreatsAnUnreadableModelTierAsAbsentRatherThanFailingTheNode()
    {
        // A tier that arrives as a string (or anything non-integral) is a host that changed its mind about
        // the shape. Losing the roster over it would cost the review; losing the tier costs a table cell.
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "subagents",
            "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                + "\"status\":\"completed\",\"effectiveModelId\":\"gpt-5.6-luna\","
                + "\"effectiveModelIntelligence\":\"three\"}]}"
        );
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
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "subagents",
            "[{\"agentId\":\"a1\",\"template\":\"reviewer\",\"task\":\"t\",\"status\":\"completed\","
                + "\"threadId\":\"thread-a1\"}]"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_FailsClosed_WhenSchemaVersionIsAbsent()
    {
        var handler = new FakeHttpMessageHandler().OnJson(HttpMethod.Get, "subagents", "{\"nodes\":[]}");
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetSubAgentTreeAsync_FailsClosed_WhenSchemaVersionIsUnsupported()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "subagents",
            "{\"schemaVersion\":2,\"nodes\":[]}"
        );
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
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "subagents",
            "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\",\"depth\":1,"
                + "\"template\":\"reviewer\",\"status\":\"completed\"}]}"
        );
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
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "subagents",
            "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                + "\"status\":\"Paused\"}]}"
        );
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
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "subagents",
            "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"a1\",\"threadId\":\"thread-a1\","
                + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                + "\"status\":\"Completed\"}]}"
        );
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
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get,
            "subagents",
            "{\"schemaVersion\":1,\"nodes\":[{\"agentId\":\"\",\"threadId\":\"thread-a1\","
                + "\"parentThreadId\":\"thread-root\",\"depth\":1,\"template\":\"reviewer\","
                + "\"status\":\"completed\"}]}"
        );
        using var http = NewHttp(handler);
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () => client.GetSubAgentTreeAsync("thread-root", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// The injected client's <see cref="HttpClient.Timeout"/> is the operative per-request deadline: a review
    /// host that has accepted the connection and is still working is abandoned when it elapses, with
    /// <see cref="CancellationToken.None"/> from the caller. This is the mechanism that broke every review —
    /// <c>Program.cs</c> built the client with no Timeout, so the deadline was .NET's silent 100-second
    /// default while <c>POST .../messages</c> blocks on the host building the agent and provisioning its
    /// sandbox session.
    /// <para>
    /// The stall is a controllable fake and the span is scaled down to milliseconds, so this proves the
    /// mechanism and not the production number. The number that reaches the transport is pinned separately by
    /// <c>DaemonS2SClientTimeoutWiringTests</c> over the real <c>Program</c> graph; reproducing the literal
    /// 100-second trip would mean a 100-second test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_clients_transport_timeout_is_the_deadline_for_a_stalled_review_host()
    {
        using var handler = new StallingHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5051/"),
            Timeout = TimeSpan.FromMilliseconds(200),
        };
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var act = () =>
            client.SendMessageAsync(
                "thread-1",
                "review this PR",
                suppressSubAgentSpawning: false,
                idempotencyKey: null,
                CancellationToken.None
            );

        var thrown = await act.Should().ThrowAsync<TaskCanceledException>();
        thrown
            .Which.InnerException.Should()
            .BeOfType<TimeoutException>(
                "the caller passed no cancellable token, so only the transport's own stopwatch could have "
                    + "ended this request — which is what makes that stopwatch the review's real deadline"
            );
    }

    /// <summary>
    /// The same stalled host, the same fake, the only difference being a transport budget generous enough to
    /// outlast it: the send now completes. Pairs with the test above — that one shows the transport stopwatch
    /// CAN end a live request, this one shows raising it is what stops it happening, so neither passes for a
    /// reason unrelated to the timeout.
    /// </summary>
    [Fact]
    public async Task A_generous_transport_timeout_lets_a_still_working_review_host_finish()
    {
        using var handler = new StallingHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5051/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        var client = new LmStreamingS2SClient(http, "s", "id", "key");

        var send = client.SendMessageAsync(
            "thread-1",
            "review this PR",
            suppressSubAgentSpawning: false,
            idempotencyKey: null,
            CancellationToken.None
        );

        send.IsCompleted.Should().BeFalse("the host has not answered yet — it is still building the agent");
        handler.Release("{\"inputId\":\"input-1\"}");

        (await send).Should().Be("input-1", "a slow but legitimate turn must survive its transport budget");
    }

    /// <summary>
    /// Holds every request until <see cref="Release"/> (or cancellation), standing in for a review host that
    /// has accepted the call and is still working. Cancelling through the handed-in token is the point: that
    /// is precisely the token <see cref="HttpClient"/> cancels when its own <see cref="HttpClient.Timeout"/>
    /// elapses, so the stall is ended by the real mechanism rather than by a test-only shortcut.
    /// </summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        /// <summary>Answers the pending request with <paramref name="json"/>.</summary>
        public void Release(string json) =>
            _release.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            using var registration = cancellationToken.Register(() => _release.TrySetCanceled(cancellationToken));
            return await _release.Task.ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            // Unblocks any request still parked on the gate, so a failing test cannot leave one hanging.
            _ = _release.TrySetCanceled();
            base.Dispose(disposing);
        }
    }
}
