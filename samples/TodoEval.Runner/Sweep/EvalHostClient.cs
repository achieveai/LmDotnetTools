using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TodoEval.Runner.Sweep;

/// <summary>Terminal-status snapshot returned by the host's status-by-input endpoint.</summary>
internal sealed record RunStatus(string Status, string? RunId);

/// <summary>
/// Thin REST client over the ISOLATED LmStreaming.Sample instance, following
/// ConversationDaemon.Sample's BCL-only pattern (HttpClient + System.Text.Json, no shared client
/// library). Deliberately sends NO S2S auth headers: the isolated host runs with
/// <c>Auth:S2SInboundSecret</c> unset, and the inbound guard is marker-gated — a request carrying
/// neither <c>X-S2S-Auth</c> nor <c>X-Sbx-App-Id</c> passes as same-origin.
/// </summary>
internal sealed class EvalHostClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;

    /// <param name="httpClient">Base address must point at the host root and end with '/'.</param>
    public EvalHostClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient.BaseAddress);
        _httpClient = httpClient;
    }

    // ── Providers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Provider/model ids the host reports as available. On this host a model id IS a provider id.</summary>
    public async Task<IReadOnlyList<string>> ListAvailableProviderIdsAsync(CancellationToken ct)
    {
        var body = await SendReadAsync(HttpMethod.Get, "api/providers", body: null, ct);
        using var doc = JsonDocument.Parse(body);
        var ids = new List<string>();
        if (
            doc.RootElement.TryGetProperty("providers", out var providers)
            && providers.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var provider in providers.EnumerateArray())
            {
                var available =
                    !provider.TryGetProperty("available", out var availableProp)
                    || availableProp.ValueKind != JsonValueKind.False;
                if (
                    available
                    && provider.TryGetProperty("id", out var idProp)
                    && idProp.ValueKind == JsonValueKind.String
                )
                {
                    ids.Add(idProp.GetString()!);
                }
            }
        }

        return ids;
    }

    // ── Chat modes ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates or updates the eval mode from its raw <c>mode.json</c> payload and returns the mode
    /// id to provision with. The identity key is the mode NAME (ids are server-minted). System modes
    /// are never touched: matching one is an error, not an update.
    /// </summary>
    public async Task<string> EnsureModeAsync(string modeName, JsonObject modePayload, CancellationToken ct)
    {
        var listBody = await SendReadAsync(HttpMethod.Get, "api/chat-modes", body: null, ct);
        using var listDoc = JsonDocument.Parse(listBody);

        string? existingId = null;
        foreach (var mode in listDoc.RootElement.EnumerateArray())
        {
            if (
                mode.TryGetProperty("name", out var nameProp)
                && nameProp.ValueKind == JsonValueKind.String
                && string.Equals(nameProp.GetString(), modeName, StringComparison.Ordinal)
            )
            {
                if (
                    mode.TryGetProperty("isSystemDefined", out var systemProp)
                    && systemProp.ValueKind == JsonValueKind.True
                )
                {
                    throw new InvalidOperationException(
                        $"A SYSTEM mode is already named '{modeName}'. The eval runner never edits system modes; "
                            + "rename the eval mode."
                    );
                }

                existingId = mode.GetProperty("id").GetString();
                break;
            }
        }

        var payloadJson = modePayload.ToJsonString();
        string responseBody;
        if (existingId is not null)
        {
            responseBody = await SendReadRawAsync(
                HttpMethod.Put,
                $"api/chat-modes/{Uri.EscapeDataString(existingId)}",
                payloadJson,
                ct
            );
        }
        else
        {
            responseBody = await SendReadRawAsync(HttpMethod.Post, "api/chat-modes", payloadJson, ct);
        }

        return ReadStringProperty(responseBody, "id");
    }

    // ── Workspaces ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Returns the id of the workspace with the given name, creating it when absent.</summary>
    public async Task<string> EnsureWorkspaceAsync(string workspaceName, CancellationToken ct)
    {
        var listBody = await SendReadAsync(HttpMethod.Get, "api/workspaces", body: null, ct);
        using var listDoc = JsonDocument.Parse(listBody);
        if (
            listDoc.RootElement.TryGetProperty("workspaces", out var workspaces)
            && workspaces.ValueKind == JsonValueKind.Array
        )
        {
            foreach (var workspace in workspaces.EnumerateArray())
            {
                if (
                    workspace.TryGetProperty("name", out var nameProp)
                    && nameProp.ValueKind == JsonValueKind.String
                    && string.Equals(nameProp.GetString(), workspaceName, StringComparison.Ordinal)
                )
                {
                    return workspace.GetProperty("id").GetString()!;
                }
            }
        }

        var createBody = await SendReadAsync(HttpMethod.Post, "api/workspaces", new { Name = workspaceName }, ct);
        return ReadStringProperty(createBody, "id");
    }

    // ── Conversations ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Provisions a conversation. <paramref name="providerId"/> carries the PER-RUN model: on this
    /// host a discovered model id is a provider id, so provisioning with it is the per-call model
    /// selection channel (#565).
    /// </summary>
    public async Task<string> ProvisionConversationAsync(
        string workspaceId,
        string providerId,
        string modeId,
        CancellationToken ct
    )
    {
        var body = await SendReadAsync(
            HttpMethod.Post,
            "api/conversations",
            new
            {
                WorkspaceId = workspaceId,
                ProviderId = providerId,
                ModeId = modeId,
            },
            ct
        );
        return ReadStringProperty(body, "threadId");
    }

    public async Task<string> SendMessageAsync(string threadId, string text, CancellationToken ct)
    {
        var body = await SendReadAsync(
            HttpMethod.Post,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/messages",
            new { Text = text },
            ct
        );
        return ReadStringProperty(body, "inputId");
    }

    public async Task<RunStatus> GetStatusByInputIdAsync(string threadId, string inputId, CancellationToken ct)
    {
        var body = await SendReadAsync(
            HttpMethod.Get,
            $"api/conversations/{Uri.EscapeDataString(threadId)}/status?inputId={Uri.EscapeDataString(inputId)}",
            body: null,
            ct
        );
        using var doc = JsonDocument.Parse(body);
        var status =
            doc.RootElement.GetProperty("status").GetString()
            ?? throw new InvalidOperationException($"status endpoint returned a null status. Body: {body}");
        var runId =
            doc.RootElement.TryGetProperty("runId", out var runIdProp) && runIdProp.ValueKind == JsonValueKind.String
                ? runIdProp.GetString()
                : null;
        return new RunStatus(status, runId);
    }

    // ── Polling ──────────────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> TerminalStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        RunOutcomes.Completed,
        RunOutcomes.Errored,
        RunOutcomes.Interrupted,
    };

    /// <summary>
    /// Polls status-by-input to a terminal state, with the review daemon's two hard-won behaviours:
    /// geometric backoff to a ceiling, and NOT believing an <c>Interrupted</c> reading at face value.
    /// The host records an accepted input before draining it into a run, so the first poll after a
    /// send can read a synthesized <c>Interrupted</c> for a run that is about to start; an
    /// <c>Interrupted</c> reading is therefore re-polled through a grace window and only returned if
    /// it stays <c>Interrupted</c> on the SAME run id throughout.
    /// </summary>
    /// <exception cref="TimeoutException">The run did not reach a terminal status by <paramref name="deadlineUtc"/>.</exception>
    public async Task<RunStatus> PollToTerminalAsync(
        string threadId,
        string inputId,
        DateTimeOffset deadlineUtc,
        PollConfig poll,
        CancellationToken ct
    )
    {
        var interval = poll.InitialInterval;
        var status = new RunStatus("NotStarted", null);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadlineUtc)
            {
                throw new TimeoutException(
                    $"Run on thread {threadId} did not reach a terminal status before {deadlineUtc:O} "
                        + $"(last status: {status.Status})."
                );
            }

            status = await GetStatusByInputIdAsync(threadId, inputId, ct);

            if (IsInterrupted(status))
            {
                var settled = await SettleInterruptedAsync(threadId, inputId, status, deadlineUtc, poll, ct);
                if (settled is not null)
                {
                    return settled;
                }

                // Superseded — the input re-bound to a new run. Re-poll tightly.
                interval = poll.InitialInterval;
                continue;
            }

            if (TerminalStatuses.Contains(status.Status))
            {
                return status;
            }

            await Task.Delay(interval, ct);
            var next = interval + interval;
            interval = next > poll.MaxInterval ? poll.MaxInterval : next;
        }
    }

    private static bool IsInterrupted(RunStatus status) =>
        string.Equals(status.Status, RunOutcomes.Interrupted, StringComparison.OrdinalIgnoreCase);

    private async Task<RunStatus?> SettleInterruptedAsync(
        string threadId,
        string inputId,
        RunStatus interrupted,
        DateTimeOffset deadlineUtc,
        PollConfig poll,
        CancellationToken ct
    )
    {
        var graceEnd = DateTimeOffset.UtcNow + poll.InterruptedGrace;
        if (deadlineUtc < graceEnd)
        {
            graceEnd = deadlineUtc;
        }

        while (DateTimeOffset.UtcNow < graceEnd)
        {
            await Task.Delay(poll.InterruptedConfirmDelay, ct);
            var next = await GetStatusByInputIdAsync(threadId, inputId, ct);
            if (!IsInterrupted(next) || !string.Equals(next.RunId, interrupted.RunId, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return interrupted;
    }

    // ── HTTP plumbing ────────────────────────────────────────────────────────────────────────────

    private async Task<string> SendReadAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var json = body is null ? null : JsonSerializer.Serialize(body, JsonOptions);
        return await SendReadRawAsync(method, path, json, ct);
    }

    private async Task<string> SendReadRawAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{method} {path} returned {(int)response.StatusCode} {response.StatusCode}. Body: {responseBody}"
            );
        }

        return responseBody;
    }

    private static string ReadStringProperty(string body, string propertyName)
    {
        using var doc = JsonDocument.Parse(body);
        if (
            doc.RootElement.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
        )
        {
            return text;
        }

        throw new InvalidOperationException($"Server response did not contain a '{propertyName}' string. Body: {body}");
    }
}
