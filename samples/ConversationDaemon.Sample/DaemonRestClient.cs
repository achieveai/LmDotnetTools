using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ConversationDaemon.Sample;

/// <summary>
/// Thin REST client over an already-running LmStreaming.Sample instance. Wraps an injected
/// <see cref="HttpClient"/> (the caller sets its <see cref="HttpClient.BaseAddress"/>) and speaks the
/// headless conversation API (<c>/api/conversations</c>) using only BCL <c>HttpClient</c> +
/// <c>System.Text.Json</c>. A connection-refused failure is surfaced as a
/// <see cref="DaemonConnectionException"/> so no raw socket exception escapes.
/// </summary>
internal sealed class DaemonRestClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public DaemonRestClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _baseUrl = httpClient.BaseAddress?.ToString() ?? "the configured base URL";
    }

    /// <summary>Provisions a new conversation thread and returns its server-minted thread id.</summary>
    public async Task<string> ProvisionAsync(
        string workspaceId,
        string providerId,
        string modeId,
        CancellationToken ct)
    {
        var body = await SendReadAsync(
            HttpMethod.Post,
            "api/conversations",
            new { WorkspaceId = workspaceId, ProviderId = providerId, ModeId = modeId },
            ct);
        return ReadStringProperty(body, "threadId");
    }

    /// <summary>Updates a conversation's title/preview metadata.</summary>
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

    /// <summary>Gets the conversation's live run state (whether a run is currently in progress).</summary>
    public async Task<RunState> GetRunStateAsync(string threadId, CancellationToken ct)
    {
        var body = await SendReadAsync(
            HttpMethod.Get,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/run-state",
            body: null,
            ct);
        return Deserialize<RunState>(body);
    }

    /// <summary>
    /// Returns the raw JSON array body of the messages endpoint, so callers can scan it directly
    /// (e.g. for a parked <c>Wait</c> marked <c>is_deferred</c>).
    /// </summary>
    public async Task<string> GetMessagesRawAsync(string threadId, CancellationToken ct)
    {
        return await SendReadAsync(
            HttpMethod.Get,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/messages",
            body: null,
            ct);
    }

    /// <summary>Resolves a run's status by the input id returned from <see cref="SendMessageAsync"/>.</summary>
    public async Task<StatusResult> GetStatusByInputIdAsync(
        string threadId,
        string inputId,
        CancellationToken ct)
    {
        var body = await SendReadAsync(
            HttpMethod.Get,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/status?inputId={Uri.EscapeDataString(inputId)}",
            body: null,
            ct);
        return Deserialize<StatusResult>(body);
    }

    /// <summary>Switches the conversation's provider while it is idle.</summary>
    public async Task<SwitchResult> SwitchProviderAsync(
        string threadId,
        string providerId,
        CancellationToken ct)
    {
        var body = await SendReadAsync(
            HttpMethod.Post,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/provider",
            new { ProviderId = providerId },
            ct);
        return Deserialize<SwitchResult>(body);
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
            // Only connection-REFUSED means "the server isn't listening" — the actionable
            // "start the server" guidance. Other socket errors (DNS/host-unreachable/reset) are
            // genuine HTTP-layer failures and are left to propagate as HttpRequestException.
            throw new DaemonConnectionException(DaemonMessages.ConnectionRefused(_baseUrl), ex);
        }
        // Retained for the fake-handler unit test path (which injects a bare SocketException) and as
        // defensiveness: a real HttpClient wraps SocketException in HttpRequestException, so this
        // bare catch is not normally reached in production. Narrowed to connection-refused so a
        // non-refused socket error is not mislabeled "start the server".
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            throw new DaemonConnectionException(DaemonMessages.ConnectionRefused(_baseUrl), ex);
        }
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
}
