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

    public LmStreamingS2SClient(
        HttpClient httpClient,
        string? s2sSecret,
        string? sandboxAppId,
        string? sandboxAppKey)
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
        return Deserialize<List<S2SWorkspace>>(body);
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
        CancellationToken ct)
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
            ct);
        return Deserialize<S2SWorkspace>(body);
    }

    /// <summary>
    /// Provisions a new conversation thread and returns its server-minted thread id.
    /// <paramref name="systemPromptAppendix"/> is the review profile's system prompt: provision carries no
    /// model or tool overrides, so it is the ONLY channel by which the daemon's review methodology, output
    /// contract and sub-agent-dispatch instructions reach the hosted agent. The host appends it to the
    /// workspace-agent mode's own prompt (additive, not a replacement). A null/blank value is sent as
    /// <c>null</c>, which the host treats the same as absent.
    /// </summary>
    public async Task<string> ProvisionAsync(
        string workspaceId,
        string providerId,
        string modeId,
        string? systemPromptAppendix,
        CancellationToken ct)
    {
        var body = await SendReadAsync(
            HttpMethod.Post,
            "api/conversations",
            new
            {
                WorkspaceId = workspaceId,
                ProviderId = providerId,
                ModeId = modeId,
                SystemPromptAppendix = string.IsNullOrWhiteSpace(systemPromptAppendix)
                    ? null
                    : systemPromptAppendix,
            },
            ct);
        return ReadStringProperty(body, "threadId");
    }

    /// <summary>Updates a conversation's title/preview metadata (e.g. a human-readable "Review PR #n").</summary>
    public async Task UpdateMetadataAsync(
        string threadId,
        string? title,
        string? preview,
        CancellationToken ct)
    {
        await SendAsync(
            HttpMethod.Put,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/metadata",
            new { Title = title, Preview = preview },
            ct);
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
            ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        _ = response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Queues a user message onto the thread and returns the input id to poll status by.</summary>
    public async Task<string> SendMessageAsync(string threadId, string text, CancellationToken ct)
    {
        var body = await SendReadAsync(
            HttpMethod.Post,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/messages",
            new { Text = text },
            ct);
        return ReadStringProperty(body, "inputId");
    }

    /// <summary>
    /// Resolves a run's status by the input id returned from <see cref="SendMessageAsync"/>. The review
    /// text, once the run is terminal, rides the <c>response</c> field: the server pre-serializes the run's
    /// final assistant non-thinking <c>TextMessage</c> there (snake_case keys), so
    /// <see cref="S2SStatusResult.ResponseText"/> is its <c>text</c> property.
    /// </summary>
    public async Task<S2SStatusResult> GetStatusByInputIdAsync(
        string threadId,
        string inputId,
        CancellationToken ct)
    {
        var body = await SendReadAsync(
            HttpMethod.Get,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/status?inputId={Uri.EscapeDataString(inputId)}",
            body: null,
            ct);
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
            ct);
        return ParseSubAgentTree(body);
    }

    // ── HTTP plumbing ────────────────────────────────────────────────────────────────────────────

    private async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var response = await ExecuteAsync(method, path, body, ct);
        _ = response.EnsureSuccessStatusCode();
    }

    private async Task<string> SendReadAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        using var response = await ExecuteAsync(method, path, body, ct);
        _ = response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> ExecuteAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
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

        var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()!
            : throw new InvalidOperationException($"Status response did not contain a 'status' string. Body: {body}");

        string? runId = root.TryGetProperty("runId", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;

        // response is the pre-serialized final assistant message (snake_case keys); the review text is its
        // "text" property. Absent/non-object/non-text ⇒ null (run not terminal yet, or a tool-only run).
        string? responseText = null;
        if (root.TryGetProperty("response", out var resp)
            && resp.ValueKind == JsonValueKind.Object
            && resp.TryGetProperty("text", out var t)
            && t.ValueKind == JsonValueKind.String)
        {
            responseText = t.GetString();
        }

        return new S2SStatusResult(status, runId, responseText);
    }

    private static string ReadStringProperty(string body, string propertyName)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        throw new InvalidOperationException(
            $"Server response did not contain a '{propertyName}' string. Body: {body}");
    }

    private static T Deserialize<T>(string body)
    {
        return JsonSerializer.Deserialize<T>(body, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Could not parse a {typeof(T).Name} from the server response. Body: {body}");
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
                    + $"pre-versioned flat shape?). Body: {body}");
        }

        if (!root.TryGetProperty("schemaVersion", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || versionElement.GetInt32() != 1)
        {
            throw new InvalidOperationException(
                $"Sub-agent tree response has a missing or unsupported schemaVersion (only 1 is "
                    + $"supported). Body: {body}");
        }

        if (!root.TryGetProperty("nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Sub-agent tree response did not contain a 'nodes' array. Body: {body}");
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
            FailureCode = OptionalString(element, "failureCode"),
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
            $"Sub-agent tree node is missing the required '{propertyName}' string field. Body: {fullBody}");
    }

    private static int RequireInt(JsonElement element, string propertyName, string fullBody) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : throw new InvalidOperationException(
                $"Sub-agent tree node is missing the required '{propertyName}' number field. Body: {fullBody}");

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
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
}

/// <summary>A workspace as returned by the review host's <c>api/workspaces</c> list/create endpoints.</summary>
internal sealed record S2SWorkspace(
    string Id,
    string Name,
    string DirectoryRelPath,
    IReadOnlyList<string> Marketplaces);

/// <summary>
/// A polled run status: the top-level <c>Status</c> string (one of <c>NotStarted</c>/<c>InProgress</c>/
/// <c>Completed</c>/<c>Errored</c>/<c>Interrupted</c>), the run id, and the final assistant text once the
/// run is terminal (null while still running).
/// </summary>
internal sealed record S2SStatusResult(string Status, string? RunId, string? ResponseText);

/// <summary>
/// Thrown when the daemon cannot open a TCP connection to the LmStreaming review host (it is not running).
/// Carries actionable guidance and is distinct from an HTTP-layer failure so the caller can surface a clean
/// "start the review host" message.
/// </summary>
internal sealed class S2SConnectionException : Exception
{
    public S2SConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public static string BuildMessage(string baseUrl) =>
        $"Could not reach the LmStreaming review host at {baseUrl}. "
        + "Start it first (e.g. `dotnet run --project samples/LmStreaming.Sample` with "
        + "ASPNETCORE_URLS set to the review host URL), then re-run the daemon with UseS2SReviewAgent=true.";
}
