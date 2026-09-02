using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Thin outbound REST client over an already-running LmStreaming.Sample <b>review host</b>. Wraps an
/// injected <see cref="HttpClient"/> (the caller sets its <see cref="HttpClient.BaseAddress"/>) and speaks
/// the headless conversation + workspace API (<c>/api/conversations</c>, <c>/api/workspaces</c>) using only
/// BCL <c>HttpClient</c> + <c>System.Text.Json</c> — no project reference to LmStreaming.Sample. Modeled on
/// <c>ConversationDaemon.Sample.DaemonRestClient</c>, with two differences: it attaches the S2S auth headers
/// (<c>X-S2S-Auth</c> + the <c>X-Sbx-App-*</c> gateway-credential passthrough) on <b>every</b> request so the
/// sandbox session binds to the daemon's gateway identity, and it adds the <c>api/workspaces</c> list/create
/// calls the review-workspace preparer needs. The final review text rides <c>status.Response</c>, so no
/// message-replay endpoint is used.
/// <para>
/// <b>Never</b> logs or echoes the S2S secret or the app key (AUTH_ENFORCE invariant): the auth values are
/// only ever written into request headers, never into log output or exception messages.
/// </para>
/// </summary>
internal sealed class LmStreamingS2SClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string? _s2sSecret;
    private readonly string? _sandboxAppId;
    private readonly string? _sandboxAppKey;

    public LmStreamingS2SClient(HttpClient httpClient, string? s2sSecret, string? sandboxAppId, string? sandboxAppKey)
    {
        _httpClient = httpClient;
        _baseUrl = httpClient.BaseAddress?.ToString() ?? "the configured LmStreaming base URL";
        _s2sSecret = s2sSecret;
        _sandboxAppId = sandboxAppId;
        _sandboxAppKey = sandboxAppKey;
    }

    /// <summary>Lists all workspaces the review host knows about.</summary>
    public async Task<IReadOnlyList<S2SWorkspace>> ListWorkspacesAsync(CancellationToken ct)
    {
        var body = await SendReadAsync(HttpMethod.Get, "api/workspaces", body: null, ct);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.ValueKind switch
        {
            // Backward compatibility with hosts predating the gateway-catalog envelope.
            JsonValueKind.Array => Deserialize<List<S2SWorkspace>>(body),
            JsonValueKind.Object when document.RootElement.TryGetProperty("workspaces", out var workspaces) =>
                workspaces.Deserialize<List<S2SWorkspace>>(JsonOptions) ?? [],
            _ => throw new JsonException("The workspace response was neither an array nor a catalog envelope."),
        };
    }

    /// <summary>
    /// Creates a workspace whose <paramref name="directoryRelPath"/> leaf mounts the daemon's pre-cloned
    /// checkout, with <paramref name="marketplaces"/> attached so the gateway surfaces the
    /// <c>code-reviewer:*</c> sub-agents. Returns the server's stored workspace (its sanitized
    /// <c>DirectoryRelPath</c> + minted <c>Id</c>).
    /// </summary>
    public async Task<S2SWorkspace> CreateWorkspaceAsync(
        string name,
        string directoryRelPath,
        IReadOnlyList<string> marketplaces,
        CancellationToken ct
    )
    {
        var body = await SendReadAsync(
            HttpMethod.Post,
            "api/workspaces",
            new
            {
                Name = name,
                DirectoryRelPath = directoryRelPath,
                Marketplaces = marketplaces,
            },
            ct
        );
        return Deserialize<S2SWorkspace>(body);
    }

    /// <summary>
    /// Provisions a new conversation thread and returns its server-minted thread id.
    /// <paramref name="systemPromptAppendix"/> is the review profile's system prompt: the optional provision
    /// model/effort fields configure execution rather than instructions, so this is the ONLY channel by which
    /// the daemon's review methodology, output contract and sub-agent-dispatch instructions reach the hosted
    /// agent. The host records it in thread
    /// metadata and composes it into the prompt at agent build via
    /// <c>SystemPromptAugmenter.ComposeAsync</c>, LAST — after the mode prompt, the workspace suffix and
    /// the discovered CLAUDE.md/AGENTS.md block (additive, not a replacement).
    /// <para>
    /// That was NOT true until #528. The value was sent, stored, and read by nothing: the only function
    /// that could apply an appendix had zero production callers, so every S2S review ran under the bare
    /// mode prompt (<c>workspace-agent</c>, the pre-#628 default) — precisely the state
    /// <c>S2SReviewAgentLoopFactory</c>'s own
    /// ArgumentException was written to prevent. Sending it here is still only half the contract; a test
    /// asserting this call carries the field proves delivery to the host, not application to the model.
    /// The test that proves application is
    /// <c>LmStreaming.Sample.E2E.Tests.SystemPromptCompositionTests</c>, which reads the composed prompt
    /// back off the outbound provider request.
    /// </para>
    /// A null/blank value is sent as <c>null</c>, which the host treats the same as absent.
    /// <para>
    /// <paramref name="subAgentModelId"/> is the model every sub-agent spawned in this conversation runs on
    /// unless the spawn names its own. It is conversation-scoped rather than per-turn because the host builds
    /// its sub-agent options once per thread, when the agent is created. Optional and additive: a host that
    /// predates the field ignores it and every child inherits the parent model, which is the behavior this
    /// call had before — so unlike the spawn-suppression and idempotency flags below, there is nothing to
    /// acknowledge and no contract to fail. Blank is sent as <c>null</c>, never as an empty string: a host
    /// that stored <c>""</c> would hand each spawn a blank model id instead of leaving it to inherit.
    /// </para>
    /// </summary>
    public async Task<string> ProvisionAsync(
        string workspaceId,
        string providerId,
        string modeId,
        string? systemPromptAppendix,
        string? subAgentModelId,
        string? reasoningEffort,
        CancellationToken ct
    )
    {
        using var response = await ExecuteAsync(
            HttpMethod.Post,
            "api/conversations",
            new
            {
                WorkspaceId = workspaceId,
                ProviderId = providerId,
                ModeId = modeId,
                SystemPromptAppendix = string.IsNullOrWhiteSpace(systemPromptAppendix) ? null : systemPromptAppendix,
                SubAgentModelId = string.IsNullOrWhiteSpace(subAgentModelId) ? null : subAgentModelId,
                // Do not normalize empty to null: empty explicitly asks the host to omit effort, while null
                // leaves the provider's default intact.
                ReasoningEffort = reasoningEffort,
            },
            ct
        );

        // The host's provision route answers 404 for an unresolvable mode (and for an unknown
        // workspace) — before this branch existed that surfaced as a bare HttpRequestException
        // ("response status code does not indicate success: 404"), naming neither the mode nor the
        // host, on every review. Named here and thrown as a CONTRACT failure (bounded retries):
        // a mode id the host cannot resolve stays unresolvable until an operator fixes the
        // configuration or the host's Prompts.yaml, so retrying is pure amplification.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new ReviewHostContractException(
                $"The LmStreaming review host at {_baseUrl} refused to provision the review conversation "
                    + $"(POST api/conversations returned 404). The configured mode id '{modeId}' "
                    + $"(CodeReviewDaemon:LmStreamingModeId) most likely does not resolve on that host - "
                    + $"check the host's Prompts.yaml / user modes. Host response: {detail}"
            );
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new ReviewHostContractException(
                $"The LmStreaming review host at {_baseUrl} rejected the review-conversation contract "
                    + $"(POST api/conversations returned 400). Requested reasoning effort: "
                    + $"'{reasoningEffort ?? "(absent)"}'. Host response: {detail}"
            );
        }

        _ = response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        var threadId = ReadStringProperty(body, "threadId");
        if (reasoningEffort is not null && !ReadBoolProperty(body, "reasoningEffortAccepted"))
        {
            try
            {
                _ = await DeleteConversationAsync(threadId, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Cleanup is best-effort. The stable contract error below is more actionable than a secondary
                // delete failure, and the retention sweeper cannot know this id because provisioning never
                // returned it to the caller.
            }

            throw new ReviewHostContractException(
                $"The LmStreaming review host at {_baseUrl} did not acknowledge requested root reasoning effort "
                    + $"'{reasoningEffort}'. Upgrade the review host before running this review."
            );
        }

        return threadId;
    }

    /// <summary>Updates a conversation's title/preview metadata (e.g. a human-readable "Review PR #n").</summary>
    public async Task UpdateMetadataAsync(string threadId, string? title, string? preview, CancellationToken ct)
    {
        await SendAsync(
            HttpMethod.Put,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/metadata",
            new { Title = title, Preview = preview },
            ct
        );
    }

    /// <summary>
    /// Discards a hosted conversation: the review host evicts its pooled agent and deletes the thread, so
    /// the deep-link <c>?threadId=</c> stops resolving. Returns <c>true</c> when this call deleted it and
    /// <c>false</c> when the host reports it was already gone (404) — an absent conversation is the state
    /// the caller wanted, not a failure, so the retention sweep can treat both as "done" while a genuine
    /// error (auth, 5xx, host down) still throws and leaves the ledger row for the next cycle.
    /// <para>
    /// This is a RETENTION-CEILING operation, never a teardown one. Conversations must outlive their
    /// review — the posted comment's deep-link is the reason the S2S path exists.
    /// </para>
    /// </summary>
    public async Task<bool> DeleteConversationAsync(string threadId, CancellationToken ct)
    {
        using var response = await ExecuteAsync(
            HttpMethod.Delete,
            $"api/conversations/{Uri.EscapeDataString(threadId)}",
            body: null,
            ct
        );

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        _ = response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Verifies, WITHOUT creating or queueing anything, that the host implements the three contracts a review
    /// depends on: root reasoning effort, per-turn spawn suppression, and message idempotency.
    /// <para>
    /// Response acknowledgements alone are too late for the FIRST send. An old host ignores unknown request
    /// properties, so the daemon only learns its key was never honored after that turn is already queued and
    /// running — the duplicate the key exists to prevent has been created by the very call that detected the
    /// problem. A capability read has no such side effect, which is also why it (not a probe send) is what a
    /// RESUMED review uses: a resume must never enqueue to find out who it is talking to.
    /// </para>
    /// <para>
    /// Old hosts have no such endpoint and answer 404/405; that is the expected shape of the failure, and it
    /// is reported as a contract failure rather than a transport one so the daemon can bound its retries
    /// instead of re-attempting an incompatibility forever. A rejected CREDENTIAL (401/403) is bounded the
    /// same way and for the same reason: this endpoint carries the host's ordinary inbound-auth policy, so a
    /// refusal here means every subsequent call is refused too. Retrying a wrong or missing shared secret
    /// until the governor's clock runs out only delays the same conclusion.
    /// </para>
    /// </summary>
    public async Task EnsureHostContractAsync(CancellationToken ct)
    {
        using var response = await ExecuteAsync(HttpMethod.Get, "api/conversations/capabilities", body: null, ct);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
        {
            throw new ReviewHostContractException(
                $"The LmStreaming review host at {_baseUrl} does not advertise conversation capabilities "
                    + $"(GET api/conversations/capabilities returned {(int)response.StatusCode}), so it "
                    + "predates the root reasoning-effort, per-turn spawn-suppression, and message-idempotency "
                    + "contracts this review requires. Upgrade the review host."
            );
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ReviewHostContractException(
                $"The LmStreaming review host at {_baseUrl} rejected this daemon's credential on the "
                    + $"capability read (GET api/conversations/capabilities returned "
                    + $"{(int)response.StatusCode}). The inbound S2S secret this daemon presents does not "
                    + "match the host's; no send would be accepted either. Fix the shared secret."
            );
        }

        _ = response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);

        var missing = new List<string>();
        if (!ReadBoolProperty(body, "messageIdempotency"))
        {
            missing.Add("messageIdempotency");
        }

        if (!ReadBoolProperty(body, "spawnSuppression"))
        {
            missing.Add("spawnSuppression");
        }

        if (!ReadBoolProperty(body, "rootReasoningEffort"))
        {
            missing.Add("rootReasoningEffort");
        }

        if (missing.Count > 0)
        {
            throw new ReviewHostContractException(
                $"The LmStreaming review host at {_baseUrl} does not support "
                    + $"{string.Join(" and ", missing)}, which this review requires before it may send. "
                    + $"Body: {body}"
            );
        }
    }

    /// <summary>
    /// Queues a user message onto the thread and returns the input id to poll status by.
    /// <para>
    /// <paramref name="suppressSubAgentSpawning"/> asks the host to run THIS turn with no ability to start
    /// new sub-agents (the synthesis turn, after the completion barrier). It is a hard guarantee, not a
    /// hint: when requested, the host must acknowledge it with <c>spawningSuppressed: true</c>, and this
    /// method throws when it does not. That is what makes the call version-safe — a host predating the
    /// field happily ignores the unknown request property and returns a normal 202, so without the
    /// acknowledgement check the daemon would believe it had a guarantee it never got.
    /// </para>
    /// <para>
    /// <paramref name="idempotencyKey"/> is acknowledged the same way and for the same reason. It makes the
    /// send safe to REPEAT: the host records the input under an id derived from the key (folding in the
    /// options that change what the turn does) and reconciles a repeat against its durable accepted-input
    /// ledger, so a caller whose response was lost — a socket reset, or a process that
    /// died between the host accepting and the answer arriving — recovers the same input instead of queueing
    /// a second (minutes-long, sub-agent-fanning) turn. An unacknowledged key means the host minted its own
    /// id and the repeat WOULD duplicate, so the send is failed rather than silently retried into a double
    /// review.
    /// </para>
    /// </summary>
    public async Task<string> SendMessageAsync(
        string threadId,
        string text,
        bool suppressSubAgentSpawning,
        string? idempotencyKey,
        CancellationToken ct
    )
    {
        var body = await SendReadAsync(
            HttpMethod.Post,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/messages",
            new
            {
                Text = text,
                SuppressSubAgentSpawning = suppressSubAgentSpawning,
                IdempotencyKey = idempotencyKey,
            },
            ct
        );

        if (suppressSubAgentSpawning && !ReadBoolProperty(body, "spawningSuppressed"))
        {
            throw new ReviewHostContractException(
                "The review host did not acknowledge the requested sub-agent spawn suppression "
                    + "('spawningSuppressed' was absent or false), so this turn cannot be guaranteed free of "
                    + $"new sub-agents. Upgrade the LmStreaming review host at {_baseUrl}. Body: {body}"
            );
        }

        if (idempotencyKey is not null && !ReadBoolProperty(body, "idempotencyKeyHonored"))
        {
            throw new ReviewHostContractException(
                "The review host did not acknowledge the supplied idempotency key "
                    + "('idempotencyKeyHonored' was absent or false), so re-sending this turn after a lost "
                    + $"response would queue a second one. Upgrade the LmStreaming review host at {_baseUrl}. "
                    + $"Body: {body}"
            );
        }

        return ReadStringProperty(body, "inputId");
    }

    /// <summary>
    /// Resolves a run's status by the input id returned from <see cref="SendMessageAsync"/>. The review
    /// text, once the run is terminal, rides the <c>response</c> field: the server pre-serializes the run's
    /// final assistant non-thinking <c>TextMessage</c> there (snake_case keys), so
    /// <see cref="S2SStatusResult.ResponseText"/> is its <c>text</c> property.
    /// </summary>
    public async Task<S2SStatusResult> GetStatusByInputIdAsync(string threadId, string inputId, CancellationToken ct)
    {
        var body = await SendReadAsync(
            HttpMethod.Get,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/status?inputId={Uri.EscapeDataString(inputId)}",
            body: null,
            ct
        );
        return ParseStatus(body);
    }

    /// <summary>
    /// Reads the versioned recursive descendant graph (schema v1) for <paramref name="rootThreadId"/> via
    /// <c>GET api/conversations/{rootThreadId}/subagents?recursive=true</c>, for the S2S review-completion
    /// source to feed the shared review-completion barrier.
    /// Fails closed (throws <see cref="InvalidOperationException"/> with the raw body embedded) on
    /// anything that is not an unambiguous schema-v1 tree: a missing/unsupported <c>schemaVersion</c>,
    /// the OLD flat (bare-array, pre-versioned) response shape, or a node missing a required
    /// relationship field (<c>agentId</c>/<c>threadId</c>/<c>parentThreadId</c>/<c>depth</c>/
    /// <c>template</c>/<c>status</c>) — an incompatible response is never silently treated as an empty,
    /// successful tree. An unrecognized <c>status</c> string maps to <see cref="ReviewSubAgentStatus.Unknown"/>
    /// rather than throwing or defaulting to a terminal value.
    /// <para>
    /// <b>Deployment order:</b> the review host's recursive endpoint must already be deployed and
    /// serving schema v1 before this client (and the daemon completion barrier that depends on it) is
    /// enabled — enabling the barrier first would fail closed against a host that has not been upgraded
    /// yet.
    /// </para>
    /// </summary>
    public async Task<ReviewSubAgentTreeSnapshot> GetSubAgentTreeAsync(string rootThreadId, CancellationToken ct)
    {
        var body = await SendReadAsync(
            HttpMethod.Get,
            $"api/conversations/{Uri.EscapeDataString(rootThreadId)}/subagents?recursive=true",
            body: null,
            ct
        );
        return ParseSubAgentTree(body);
    }

    /// <summary>
    /// Reads one collaborating agent's transcript via
    /// <c>GET api/conversations/{rootThreadId}/agents/{agentId}/transcript</c> — the read half of the
    /// agent directory the review host publishes over HTTP (<see cref="GetSubAgentTreeAsync"/> is the
    /// roster half). <paramref name="agentId"/> is the <c>AgentId</c> carried by a
    /// <see cref="ReviewSubAgentNode"/>, which is exactly the key the host's hierarchy projection
    /// resolves.
    /// <para>
    /// The daemon reads with no <c>viewer</c>, so the host authorizes it as the ROOT agent — an ancestor
    /// of every descendant, and therefore allowed by the transcript visibility policy. Reasoning is
    /// stripped host-side and never appears in the response.
    /// </para>
    /// <para>
    /// Unlike the sub-agent tree, this route legitimately returns a BARE JSON array (the host's
    /// <c>Ok(result.Messages)</c>), so a bare array is the success shape here — not the version skew it
    /// signals there. Anything that is not an array throws with the raw body embedded rather than being
    /// silently read as an empty transcript.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetAgentTranscriptAsync(
        string rootThreadId,
        string agentId,
        CancellationToken ct
    )
    {
        var body = await SendReadAsync(
            HttpMethod.Get,
            $"api/conversations/{Uri.EscapeDataString(rootThreadId)}"
                + $"/agents/{Uri.EscapeDataString(agentId)}/transcript",
            body: null,
            ct
        );
        return ParseAgentTranscript(body);
    }

    /// <summary>
    /// Reads the root review conversation's own messages via
    /// <c>GET api/conversations/{threadId}/messages</c> — the lead reviewer's transcript.
    /// <para>
    /// A different route from <see cref="GetAgentTranscriptAsync"/> because it has to be. The host's
    /// agent-transcript route resolves its <c>agentId</c> against the polled thread's <b>descendants</b>,
    /// so the root agent is not a candidate there and no id can name it. The messages route serves an
    /// ordinary (non <c>subagent-</c>/<c>workflow-</c>) root conversation to a machine caller and returns
    /// the same persisted-message array, which is why the same parser applies unchanged.
    /// </para>
    /// <para>
    /// Note this route does <b>not</b> strip reasoning the way the descendant route does; the caller is
    /// responsible for deciding what of it is worth retaining.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetConversationTranscriptAsync(
        string threadId,
        CancellationToken ct
    )
    {
        var body = await SendReadAsync(
            HttpMethod.Get,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/messages",
            body: null,
            ct
        );
        return ParseAgentTranscript(body);
    }

    // ── HTTP plumbing ────────────────────────────────────────────────────────────────────────────

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await ExecuteAsync(method, path, body, ct);
        _ = response.EnsureSuccessStatusCode();
    }

    private async Task<string> SendReadAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await ExecuteAsync(method, path, body, ct);
        _ = response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> ExecuteAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct
    )
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Auth headers on EVERY request. The inbound guard is marker-gated (armed by X-S2S-Auth OR
        // X-Sbx-App-Id), and the sandbox session binds to whatever app id the caller forwards — so both
        // must ride each call. Values are only ever written to headers, never logged.
        if (!string.IsNullOrEmpty(_s2sSecret))
        {
            request.Headers.TryAddWithoutValidation("X-S2S-Auth", _s2sSecret);
        }

        if (!string.IsNullOrEmpty(_sandboxAppId))
        {
            request.Headers.TryAddWithoutValidation("X-Sbx-App-Id", _sandboxAppId);
        }

        if (!string.IsNullOrEmpty(_sandboxAppKey))
        {
            request.Headers.TryAddWithoutValidation("X-Sbx-App-Key", _sandboxAppKey);
        }

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        try
        {
            return await _httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
            when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
        {
            // Connection-REFUSED means the review host isn't listening — the actionable "start it" case.
            // Other socket errors are genuine HTTP-layer failures and propagate as HttpRequestException.
            throw new S2SConnectionException(S2SConnectionException.BuildMessage(_baseUrl), ex);
        }
        // Retained for the fake-handler unit test path (which injects a bare SocketException) and as
        // defensiveness: a real HttpClient wraps SocketException in HttpRequestException.
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            throw new S2SConnectionException(S2SConnectionException.BuildMessage(_baseUrl), ex);
        }
    }

    private static S2SStatusResult ParseStatus(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var status =
            root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()!
                : throw new InvalidOperationException(
                    $"Status response did not contain a 'status' string. Body: {body}"
                );

        string? runId =
            root.TryGetProperty("runId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;

        // response is the pre-serialized final assistant message (snake_case keys); the review text is its
        // "text" property. Absent/non-object/non-text ⇒ null (run not terminal yet, or a tool-only run).
        string? responseText = null;
        if (
            root.TryGetProperty("response", out var resp)
            && resp.ValueKind == JsonValueKind.Object
            && resp.TryGetProperty("text", out var t)
            && t.ValueKind == JsonValueKind.String
        )
        {
            responseText = t.GetString();
        }

        return new S2SStatusResult(status, runId, responseText);
    }

    private static string ReadStringProperty(string body, string propertyName)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        throw new InvalidOperationException($"Server response did not contain a '{propertyName}' string. Body: {body}");
    }

    private static bool ReadBoolProperty(string body, string propertyName)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private static T Deserialize<T>(string body)
    {
        return JsonSerializer.Deserialize<T>(body, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Could not parse a {typeof(T).Name} from the server response. Body: {body}"
            );
    }

    private static ReviewSubAgentTreeSnapshot ParseSubAgentTree(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            // The OLD flat (pre-versioned) endpoint returns a bare SubAgentSummary[] array — never treat
            // that as an empty, successful schema-v1 tree.
            throw new InvalidOperationException(
                $"Sub-agent tree response was not a schema-v1 object (got a bare array — the old, "
                    + $"pre-versioned flat shape?). Body: {body}"
            );
        }

        if (
            !root.TryGetProperty("schemaVersion", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || versionElement.GetInt32() != 1
        )
        {
            throw new InvalidOperationException(
                $"Sub-agent tree response has a missing or unsupported schemaVersion (only 1 is "
                    + $"supported). Body: {body}"
            );
        }

        if (!root.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Sub-agent tree response did not contain a 'nodes' array. Body: {body}"
            );
        }

        var nodes = new List<ReviewSubAgentNode>(nodesElement.GetArrayLength());
        foreach (var element in nodesElement.EnumerateArray())
        {
            nodes.Add(ParseNode(element, body));
        }

        return new ReviewSubAgentTreeSnapshot(nodes);
    }

    private static ReviewSubAgentNode ParseNode(JsonElement element, string fullBody)
    {
        return new ReviewSubAgentNode
        {
            AgentId = RequireString(element, "agentId", fullBody),
            ThreadId = RequireString(element, "threadId", fullBody),
            ParentThreadId = RequireString(element, "parentThreadId", fullBody),
            Depth = RequireInt(element, "depth", fullBody),
            Template = RequireString(element, "template", fullBody),
            Status = ParseNodeStatus(RequireString(element, "status", fullBody)),
            Name = OptionalString(element, "name"),
            TerminalAtUtc = OptionalDateTimeOffset(element, "terminalAtUtc"),
            LastActivityUtc = OptionalDateTimeOffset(element, "lastActivityUtc"),
            FailureCode = OptionalString(element, "failureCode"),
            // OPTIONAL, and this is a compatibility requirement rather than a style choice. The daemon and
            // the S2S host deploy independently, so a new daemon polls an old host that omits these three
            // entirely. RequireString here would turn that omission into a throw at the settlement barrier
            // and fail the whole review over a presentation field. Adding optional fields is why the wire
            // does NOT get a schemaVersion bump for this: bumping to 2 makes every new daemon throw against
            // every not-yet-updated host.
            EffectiveModelId = OptionalString(element, "effectiveModelId"),
            EffectiveModelIntelligence = OptionalInt(element, "effectiveModelIntelligence"),
            ModelSelectionSource = OptionalString(element, "modelSelectionSource"),
            RequestedReasoningEffort = OptionalString(element, "requestedReasoningEffort"),
            ShapedReasoningEffort = OptionalString(element, "shapedReasoningEffort"),
        };
    }

    private static string RequireString(JsonElement element, string propertyName, string fullBody)
    {
        // An empty string is treated the same as an absent property: a relationship field such as
        // "agentId"/"threadId"/"parentThreadId" is never legitimately empty, so this must fail closed
        // rather than let a blank identifier flow into the barrier — mirroring ReadStringProperty's
        // existing IsNullOrEmpty rejection elsewhere in this file.
        if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        throw new InvalidOperationException(
            $"Sub-agent tree node is missing the required '{propertyName}' string field. Body: {fullBody}"
        );
    }

    private static int RequireInt(JsonElement element, string propertyName, string fullBody) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : throw new InvalidOperationException(
                $"Sub-agent tree node is missing the required '{propertyName}' number field. Body: {fullBody}"
            );

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads an optional integer node field. A property that is absent, null, or not an integral number
    /// reads as null rather than throwing — the same fail-soft contract as <see cref="OptionalString"/>,
    /// for the same reason: these fields are presentation, and an old host omits them.
    /// </summary>
    private static int? OptionalInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? OptionalDateTimeOffset(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetDateTimeOffset()
            : null;

    /// <summary>
    /// Maps the wire status string to <see cref="ReviewSubAgentStatus"/> case-insensitively. Any value that
    /// is not exactly one of Running/Completed/Error/Stopped maps to <see cref="ReviewSubAgentStatus.Unknown"/>
    /// — parsed as a plain string first (never <c>JsonSerializer.Deserialize&lt;ReviewSubAgentStatus&gt;</c>
    /// against the C# enum directly), so an unrecognized or new wire status never throws a JSON enum error
    /// and never silently defaults to a terminal value.
    /// </summary>
    private static ReviewSubAgentStatus ParseNodeStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "running" => ReviewSubAgentStatus.Running,
            "completed" => ReviewSubAgentStatus.Completed,
            "error" => ReviewSubAgentStatus.Error,
            "stopped" => ReviewSubAgentStatus.Stopped,
            _ => ReviewSubAgentStatus.Unknown,
        };

    /// <summary>
    /// Parses the transcript route's bare array of persisted-message rows. Every field is read
    /// defensively: this feeds a diagnostic artifact, so a row the host shapes slightly differently
    /// should degrade to a blank field rather than abort the whole transcript — but a body that is not
    /// an array at all is a contract failure and throws with the body embedded.
    /// </summary>
    private static IReadOnlyList<ReviewAgentTranscriptEntry> ParseAgentTranscript(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Agent transcript response was not a JSON array — the review host may predate the "
                    + $"transcript route or returned an error envelope. Body: {body}"
            );
        }

        var messages = new List<ReviewAgentTranscriptEntry>(root.GetArrayLength());
        foreach (var element in root.EnumerateArray())
        {
            messages.Add(
                new ReviewAgentTranscriptEntry(
                    MessageType: OptionalString(element, "messageType") ?? string.Empty,
                    Role: OptionalString(element, "role") ?? string.Empty,
                    FromAgent: OptionalString(element, "fromAgent"),
                    TimestampUtc: element.TryGetProperty("timestamp", out var ts)
                    && ts.ValueKind == JsonValueKind.Number
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ts.GetInt64())
                        : null,
                    Body: ExtractTranscriptBody(OptionalString(element, "messageJson"))
                )
            );
        }

        return messages;
    }

    /// <summary>
    /// Reduces one persisted <c>messageJson</c> payload to the text worth writing down. The host
    /// serializes every <c>IMessage</c> shape through the same field, so this tries the two that carry
    /// plain prose and otherwise keeps the payload verbatim — an unrecognized shape is preserved rather
    /// than dropped, because dropping it would silently lose the very reviewer output we are collecting.
    /// </summary>
    private static string ExtractTranscriptBody(string? messageJson)
    {
        if (string.IsNullOrWhiteSpace(messageJson))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(messageJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "text", "content" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (or not the shape we hoped for) — keep the raw payload below.
        }

        return messageJson;
    }
}

/// <summary>A workspace as returned by the review host's <c>api/workspaces</c> list/create endpoints.</summary>
internal sealed record S2SWorkspace(
    string Id,
    string Name,
    string DirectoryRelPath,
    IReadOnlyList<string> Marketplaces
);

/// <summary>
/// A polled run status: the top-level <c>Status</c> string (one of <c>NotStarted</c>/<c>InProgress</c>/
/// <c>Completed</c>/<c>Errored</c>/<c>Interrupted</c>), the run id, and the final assistant text once the
/// run is terminal (null while still running).
/// </summary>
internal sealed record S2SStatusResult(string Status, string? RunId, string? ResponseText);

/// <summary>
/// Thrown when the review host cannot honour a message-level contract this review depends on — per-turn
/// spawn suppression or message idempotency — either because it predates the contract or because it
/// answered a send without acknowledging it.
/// <para>
/// A distinct type because the failure is NOT transient: retrying an incompatible host reproduces it
/// exactly, and every attempt costs another (possibly duplicated) review turn. The daemon charges it to the
/// bounded retry budget so an unexpected version skew parks the run instead of amplifying against the host
/// forever. Derives from <see cref="InvalidOperationException"/> so callers that only care that the send
/// failed are unaffected.
/// </para>
/// </summary>
internal sealed class ReviewHostContractException(string message) : InvalidOperationException(message);

/// <summary>
/// Thrown when the daemon cannot open a TCP connection to the LmStreaming review host (it is not running).
/// Carries actionable guidance and is distinct from an HTTP-layer failure so the caller can surface a clean
/// "start the review host" message.
/// </summary>
internal sealed class S2SConnectionException : Exception
{
    public S2SConnectionException(string message, Exception innerException)
        : base(message, innerException) { }

    public static string BuildMessage(string baseUrl) =>
        $"Could not reach the LmStreaming review host at {baseUrl}. "
        + "Start it first (e.g. `dotnet run --project samples/LmStreaming.Sample` with "
        + "ASPNETCORE_URLS set to the review host URL), then re-run the daemon with UseS2SReviewAgent=true.";
}
