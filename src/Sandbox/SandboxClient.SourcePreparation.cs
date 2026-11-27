using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    private static readonly TimeSpan S_sourcePreparationFirstPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan S_sourcePreparationMaxPoll = TimeSpan.FromSeconds(2);
    private const string SourcePreparationOperation = "preparing sandbox sources";

    /// <summary>
    /// Prepares a marketplace and plugin selection (<c>POST /api/v1/apps/self/plugins/prepare</c>) and, while the
    /// gateway answers <c>preparing</c>, polls this app's own status route on the configured gateway until it is
    /// <c>ready</c> or <c>unversioned</c>. The gateway's <c>Location</c> header is never followed. Polling backs off
    /// from 100 ms to 2 s and stops at <see cref="SandboxSourcePreparationRequest.MaxWait"/> or when
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// Send <see cref="SandboxSourcePreparation.SourceVersion"/> as <see cref="SandboxCreateRequest.ExpectedSourceVersion"/>
    /// with the identical selection. Once the gateway has named an operation, every <see cref="SandboxException"/>
    /// carries it as <see cref="SandboxException.OperationId"/>; pass it to <see cref="ResumeSourcePreparationAsync"/>
    /// to keep waiting without preparing again. Needs a gateway containing PR #375.
    /// </remarks>
    /// <exception cref="SandboxException">
    /// A <c>failed</c> status (an HTTP 200 body) or an error response, with <see cref="SandboxException.ErrorCode"/>
    /// when the gateway supplied one: <c>sources_changed</c>, <c>sources_not_fresh</c>, <c>sources_not_prepared</c>,
    /// <c>preparation_in_progress</c>, <c>source_snapshot_expired</c> and <c>preparation_expired</c> are
    /// <see cref="SandboxErrorKind.Conflict"/> (prepare again); <c>marketplace_access_denied</c> is
    /// <see cref="SandboxErrorKind.Authorization"/>; <c>marketplace_broker_unavailable</c> and
    /// <c>preparation_capacity</c> are <see cref="SandboxErrorKind.Unavailable"/>; <c>preparation_not_found</c> and
    /// <c>baseline_not_found</c> are <see cref="SandboxErrorKind.NotFound"/>; the <c>422</c> version and snapshot codes
    /// are <see cref="SandboxErrorKind.Protocol"/>. <see cref="SandboxErrorKind.TransportTimeout"/> when
    /// <see cref="SandboxSourcePreparationRequest.MaxWait"/> elapses (with <see cref="SandboxException.OperationId"/>).
    /// </exception>
    public async Task<SandboxSourcePreparation> PrepareSourcesAsync(
        SandboxSourcePreparationRequest request,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Marketplaces.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Marketplace aliases cannot be blank.", nameof(request));
        }

        if (request.PluginSelection?.Any(static reference => reference is null) == true)
        {
            throw new ArgumentException("A plugin selection cannot contain a null reference.", nameof(request));
        }

        if (request.MaxWait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "MaxWait must be positive.");
        }

        var body = new PrepareSourcesWireRequest(
            request.ExplicitMarketplaces || request.Marketplaces.Count > 0 ? [.. request.Marketplaces] : null,
            request.PluginSelection is null ? null : [.. request.PluginSelection.Select(ToPluginRefDto)],
            request.Refresh ? true : null,
            request.RequireFresh ? true : null
        );

        return await RunSourcePreparationAsync(
                body,
                resumeOperationId: null,
                request.MaxWait,
                S_sourcePreparationMaxPoll,
                SourcePreparationOperation,
                ReadSourcePreparation,
                ct
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a preparation this app started, by the <paramref name="operationId"/> a
    /// <see cref="SandboxSourcePreparation.OperationId"/> or <see cref="SandboxException.OperationId"/> gave. It sends
    /// no new preparation: it starts with <c>GET /api/v1/apps/self/plugins/prepare/{operationId}</c> on the configured
    /// gateway and polls that route (and any restored operation the gateway names there) exactly as
    /// <see cref="PrepareSourcesAsync"/> does, until <c>ready</c> or <c>unversioned</c>.
    /// </summary>
    /// <param name="operationId">A saved operation id: a non-empty GUID in its <c>D</c> form.</param>
    /// <param name="maxWait">
    /// The longest to wait for a <c>preparing</c> operation; <see langword="null"/> is the
    /// <see cref="SandboxSourcePreparationRequest.MaxWait"/> default (5 minutes).
    /// </param>
    /// <param name="ct">Cancels the wait; surfaces as <see cref="OperationCanceledException"/>.</param>
    /// <remarks>
    /// The route is app-bound: another app's operation id reads as <c>404 preparation_not_found</c>. The failures, and
    /// the <see cref="SandboxErrorKind.TransportTimeout"/> when <paramref name="maxWait"/> elapses, are those of
    /// <see cref="PrepareSourcesAsync"/>, and every one carries <see cref="SandboxException.OperationId"/>.
    /// </remarks>
    public async Task<SandboxSourcePreparation> ResumeSourcePreparationAsync(
        string operationId,
        TimeSpan? maxWait = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(operationId);
        if (!TryParseOperationId(operationId, out var id))
        {
            throw new ArgumentException(
                "An operation id is a non-empty GUID in its 'D' form, as the SDK reported it.",
                nameof(operationId)
            );
        }

        var wait = maxWait ?? SandboxSourcePreparationRequest.DefaultMaxWait;
        if (wait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWait), "maxWait must be positive.");
        }

        return await RunSourcePreparationAsync(
                submission: null,
                id,
                wait,
                S_sourcePreparationMaxPoll,
                SourcePreparationOperation,
                ReadSourcePreparation,
                ct
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The one prepare/status route implementation behind <see cref="PrepareSourcesAsync"/>,
    /// <see cref="ResumeSourcePreparationAsync"/> and the plugin and archive selection calls, so their status, operation
    /// id and failure handling cannot drift. It POSTs <paramref name="submission"/>, or, when
    /// <paramref name="resumeOperationId"/> is set, starts with a GET of that operation. While the gateway answers
    /// <c>preparing</c> it polls this app's fixed status route (never the <c>Location</c> header), following a restored
    /// operation id the gateway names, with a delay that doubles from 100 ms up to <paramref name="maxPoll"/>.
    /// <paramref name="complete"/> reads the terminal <c>ready</c>/<c>unversioned</c> reply.
    /// </summary>
    /// <remarks>
    /// <paramref name="maxWait"/> bounds the whole call; each HTTP call is also bounded by the transport timeout. Once an
    /// operation id is known, every <see cref="SandboxException"/> leaving here carries it, including transport timeouts,
    /// network failures, malformed bodies and error statuses. Caller cancellation propagates as
    /// <see cref="OperationCanceledException"/>.
    /// </remarks>
    private async Task<T> RunSourcePreparationAsync<T>(
        object? submission,
        Guid? resumeOperationId,
        TimeSpan maxWait,
        TimeSpan maxPoll,
        string operation,
        Func<PreparationReply, T> complete,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        using var budget = new CancellationTokenSource(maxWait, TransportClock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        var operationId = resumeOperationId;
        var delay = S_sourcePreparationFirstPoll;
        try
        {
            while (true)
            {
                var polling = operationId.HasValue;
                var path = polling ? $"{PluginPreparationPath}/{operationId:D}" : PluginPreparationPath;
                using (
                    var response = await SendRestAsync(
                            polling ? HttpMethod.Get : HttpMethod.Post,
                            $"{path}?{AppIdQuery()}",
                            polling ? null : submission,
                            sessionId: null,
                            deadline.Token
                        )
                        .ConfigureAwait(false)
                )
                {
                    var status = (int)response.StatusCode;
                    if (!response.IsSuccessStatusCode)
                    {
                        throw await MapErrorResponseAsync(response, operation, deadline.Token).ConfigureAwait(false);
                    }

                    if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
                    {
                        throw new SandboxException(
                            SandboxErrorKind.Protocol,
                            $"Unexpected HTTP status while {operation}.",
                            status
                        );
                    }

                    using var document = await ReadAppMarketplaceJsonAsync<JsonDocument>(
                            response,
                            operation,
                            deadline.Token
                        )
                        .ConfigureAwait(false);
                    var reply = document.RootElement;
                    var replyStatus = ReadOptionalString(reply, "status", status, operation);
                    if (replyStatus == "failed")
                    {
                        var code = ReadOptionalString(reply, "error_code", status, operation);
                        throw new SandboxException(
                            MapGatewayErrorKind(response.StatusCode, code),
                            $"Sandbox gateway reported a failed preparation while {operation} (error_code {code ?? "none"}).",
                            status,
                            operationId: operationId?.ToString("D")
                        )
                        {
                            ErrorCode = code,
                        };
                    }

                    if (response.StatusCode == HttpStatusCode.OK && replyStatus is "ready" or "unversioned")
                    {
                        return complete(new PreparationReply(reply, replyStatus, status, operationId));
                    }

                    if (
                        replyStatus != "preparing"
                        || (!polling && response.StatusCode != HttpStatusCode.Accepted)
                        || !TryParseOperationId(ReadOptionalString(reply, "operationId", status, operation), out var id)
                    )
                    {
                        throw new SandboxException(
                            SandboxErrorKind.Protocol,
                            $"Sandbox gateway returned an invalid preparation status or operation ID while {operation}.",
                            status
                        );
                    }

                    // A status read may name a new operation (it restores an evicted capture). Only this app's fixed
                    // path on the configured gateway is polled; the Location header is never used.
                    operationId = id;
                }

                await Task.Delay(delay, TransportClock, deadline.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, maxPoll.Ticks));
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw PreparationTimedOut(ex);
        }
        catch (SandboxException ex)
            when (ex.Kind == SandboxErrorKind.TransportTimeout
                && !ct.IsCancellationRequested
                && budget.IsCancellationRequested
            )
        {
            // SendRestAsync reports the maxWait token firing mid-call as its own transport timeout.
            throw PreparationTimedOut(ex);
        }
        catch (SandboxException ex) when (operationId is { } known && ex.OperationId is null)
        {
            throw WithOperationId(ex, known);
        }

        SandboxException PreparationTimedOut(Exception inner) =>
            new(
                SandboxErrorKind.TransportTimeout,
                $"Sandbox gateway did not finish {operation} within {maxWait}.",
                statusCode: null,
                inner,
                operationId: operationId?.ToString("D")
            );
    }

    /// <summary>A terminal <c>ready</c>/<c>unversioned</c> reply, valid only until the delegate it is handed returns.</summary>
    private readonly record struct PreparationReply(JsonElement Body, string Status, int StatusCode, Guid? OperationId);

    // Guid.TryParseExact trims surrounding whitespace, so the length check keeps the id exactly its D form.
    private static bool TryParseOperationId(string? value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id) && value.Length == 36 && id != Guid.Empty;

    private static SandboxException WithOperationId(SandboxException error, Guid operationId) =>
        new(error.Kind, error.Message, error.StatusCode, error, operationId.ToString("D"))
        {
            ErrorCode = error.ErrorCode,
            Retryable = error.Retryable,
            RetryAfter = error.RetryAfter,
            IsDirectReadCapExceeded = error.IsDirectReadCapExceeded,
            InvalidKeys = error.InvalidKeys,
            SnapshotIds = error.SnapshotIds,
            AvailableMarketplaces = error.AvailableMarketplaces,
        };

    private static SandboxSourcePreparation ReadSourcePreparation(PreparationReply prepared)
    {
        var (reply, replyStatus, status, operationId) = prepared;
        var sourceVersion = ReadOptionalString(reply, "sourceVersion", status);
        var ready = replyStatus == "ready";
        if (
            ready
                ? string.IsNullOrWhiteSpace(sourceVersion) || sourceVersion.Any(char.IsControl)
                : sourceVersion is not null
        )
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned an invalid source version while {SourcePreparationOperation}.",
                status
            );
        }

        // A ready reply always carries its receipt. A gateway without a marketplace store answers unversioned with
        // only its capabilities and recipe (preparation.rs unversioned_reply), so there the receipt may be absent.
        var hasReceipt =
            reply.TryGetProperty("sourceReceipt", out var receipt) && receipt.ValueKind != JsonValueKind.Null;
        if (hasReceipt ? receipt.ValueKind != JsonValueKind.Object : ready)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned no source receipt while {SourcePreparationOperation}.",
                status
            );
        }

        return new SandboxSourcePreparation
        {
            Status = ready ? SandboxSourcePreparationStatus.Ready : SandboxSourcePreparationStatus.Unversioned,
            OperationId = operationId?.ToString("D"),
            SourceVersion = sourceVersion,
            ErrorCode = ReadOptionalString(reply, "error_code", status),
            Sources = hasReceipt ? ReadReceiptSources(receipt, SourcePreparationOperation, status) : null,
            RawSourceReceipt = hasReceipt ? receipt.Clone() : null,
        };
    }

    /// <summary>
    /// The <c>sources</c> of a gateway source receipt (<c>db::SourceManifest</c>), shared by preparation and sandbox
    /// create/GET. Each entry needs a <c>kind</c> and <c>marketplace</c>; the Git fields may be null. Anything else
    /// malformed is <see cref="SandboxErrorKind.Protocol"/>.
    /// </summary>
    private static IReadOnlyList<SandboxSourceReceiptEntry> ReadReceiptSources(
        JsonElement receipt,
        string operation,
        int status
    )
    {
        if (
            receipt.ValueKind != JsonValueKind.Object
            || !receipt.TryGetProperty("sources", out var sources)
            || sources.ValueKind != JsonValueKind.Array
        )
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a source receipt without sources while {operation}.",
                status
            );
        }

        var entries = new List<SandboxSourceReceiptEntry>();
        foreach (var source in sources.EnumerateArray())
        {
            var kind = ReadOptionalString(source, "kind", status, operation);
            var marketplace = ReadOptionalString(source, "marketplace", status, operation);
            if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(marketplace))
            {
                throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway returned a source receipt entry without kind or marketplace while {operation}.",
                    status
                );
            }

            entries.Add(
                new SandboxSourceReceiptEntry(
                    kind,
                    marketplace,
                    ReadOptionalString(source, "plugin", status, operation),
                    ReadOptionalString(source, "url", status, operation),
                    ReadOptionalString(source, "repo_key", status, operation),
                    ReadOptionalString(source, "subdir", status, operation),
                    ReadOptionalString(source, "sha", status, operation)
                )
            );
        }

        return entries.AsReadOnly();
    }

    /// <summary>A string field, or null when absent or JSON null. A non-object or a non-string is a protocol error.</summary>
    private static string? ReadOptionalString(
        JsonElement element,
        string name,
        int status,
        string operation = SourcePreparationOperation
    )
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a non-object where an object was expected while {operation}.",
                status
            );
        }

        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a non-string '{name}' while {operation}.",
                status
            ),
        };
    }

    private sealed record PrepareSourcesWireRequest(
        [property: JsonPropertyName("marketplaces")] IReadOnlyList<string>? Marketplaces,
        [property: JsonPropertyName("pluginSelection")] IReadOnlyList<PluginRefDto>? PluginSelection,
        [property: JsonPropertyName("refresh")] bool? Refresh,
        [property: JsonPropertyName("requireFresh")] bool? RequireFresh
    );
}
