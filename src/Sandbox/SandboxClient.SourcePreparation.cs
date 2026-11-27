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
    /// with the identical selection. Needs a gateway containing PR #375.
    /// </remarks>
    /// <exception cref="SandboxException">
    /// A <c>failed</c> status (an HTTP 200 body) or an error response, with <see cref="SandboxException.ErrorCode"/>:
    /// <c>sources_changed</c>, <c>sources_not_fresh</c>, <c>sources_not_prepared</c>, <c>preparation_in_progress</c> and
    /// <c>source_snapshot_expired</c> are <see cref="SandboxErrorKind.Conflict"/> (prepare again);
    /// <c>marketplace_access_denied</c> is <see cref="SandboxErrorKind.Authorization"/>;
    /// <c>marketplace_broker_unavailable</c> and <c>preparation_capacity</c> are <see cref="SandboxErrorKind.Unavailable"/>;
    /// <c>preparation_not_found</c> is <see cref="SandboxErrorKind.NotFound"/>; the <c>422</c> version and snapshot codes
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

        ct.ThrowIfCancellationRequested();
        var body = new PrepareSourcesWireRequest(
            request.ExplicitMarketplaces || request.Marketplaces.Count > 0 ? [.. request.Marketplaces] : null,
            request.PluginSelection is null ? null : [.. request.PluginSelection.Select(ToPluginRefDto)],
            request.Refresh ? true : null,
            request.RequireFresh ? true : null
        );

        using var budget = new CancellationTokenSource(request.MaxWait, TransportClock);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        Guid? operationId = null;
        var delay = S_sourcePreparationFirstPoll;
        try
        {
            while (true)
            {
                var path = operationId is { } id ? $"{PluginPreparationPath}/{id:D}" : PluginPreparationPath;
                using (
                    var response = await SendRestAsync(
                            operationId.HasValue ? HttpMethod.Get : HttpMethod.Post,
                            $"{path}?{AppIdQuery()}",
                            operationId.HasValue ? null : body,
                            sessionId: null,
                            deadline.Token
                        )
                        .ConfigureAwait(false)
                )
                {
                    var status = (int)response.StatusCode;
                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await MapErrorResponseAsync(response, SourcePreparationOperation, deadline.Token)
                            .ConfigureAwait(false);
                        throw operationId is null ? error : WithOperationId(error, operationId.Value);
                    }

                    if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
                    {
                        throw new SandboxException(
                            SandboxErrorKind.Protocol,
                            $"Unexpected HTTP status while {SourcePreparationOperation}.",
                            status
                        );
                    }

                    using var document = await ReadAppMarketplaceJsonAsync<JsonDocument>(
                            response,
                            SourcePreparationOperation,
                            deadline.Token
                        )
                        .ConfigureAwait(false);
                    var reply = document.RootElement;
                    var replyStatus = ReadOptionalString(reply, "status", status);
                    if (replyStatus == "failed")
                    {
                        var code = ReadOptionalString(reply, "error_code", status);
                        throw new SandboxException(
                            MapGatewayErrorKind(response.StatusCode, code),
                            $"Sandbox source preparation failed (error_code {code ?? "none"}).",
                            status,
                            operationId: operationId?.ToString("D")
                        )
                        {
                            ErrorCode = code,
                        };
                    }

                    if (response.StatusCode == HttpStatusCode.OK && replyStatus is "ready" or "unversioned")
                    {
                        return ReadSourcePreparation(reply, replyStatus, operationId, status);
                    }

                    if (
                        replyStatus != "preparing"
                        || (operationId is null && response.StatusCode != HttpStatusCode.Accepted)
                        || !Guid.TryParseExact(ReadOptionalString(reply, "operationId", status), "D", out var pendingId)
                        || pendingId == Guid.Empty
                    )
                    {
                        throw new SandboxException(
                            SandboxErrorKind.Protocol,
                            "Sandbox gateway returned an invalid source preparation status or operation ID.",
                            status
                        );
                    }

                    // A status read may hand back a new operation (it restores an evicted capture). Only this app's
                    // fixed path on the configured gateway is polled; the Location header is never used.
                    operationId = pendingId;
                }

                await Task.Delay(delay, TransportClock, deadline.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, S_sourcePreparationMaxPoll.Ticks));
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                $"Sandbox source preparation did not finish within {request.MaxWait}.",
                statusCode: null,
                ex,
                operationId: operationId?.ToString("D")
            );
        }
        catch (SandboxException ex)
            when (ex.Kind == SandboxErrorKind.TransportTimeout
                && !ct.IsCancellationRequested
                && budget.IsCancellationRequested
            )
        {
            // SendRestAsync reports our MaxWait token firing mid-call as its own transport timeout.
            throw new SandboxException(
                SandboxErrorKind.TransportTimeout,
                $"Sandbox source preparation did not finish within {request.MaxWait}.",
                statusCode: null,
                ex,
                operationId: operationId?.ToString("D")
            );
        }
    }

    private static SandboxException WithOperationId(SandboxException error, Guid operationId) =>
        new(error.Kind, error.Message, error.StatusCode, error, operationId.ToString("D"))
        {
            ErrorCode = error.ErrorCode,
            Retryable = error.Retryable,
            RetryAfter = error.RetryAfter,
        };

    private static SandboxSourcePreparation ReadSourcePreparation(
        JsonElement reply,
        string replyStatus,
        Guid? operationId,
        int status
    )
    {
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

        if (!reply.TryGetProperty("sourceReceipt", out var receipt) || receipt.ValueKind != JsonValueKind.Object)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned no source receipt while {SourcePreparationOperation}.",
                status
            );
        }

        var entries = ReadReceiptSources(receipt, SourcePreparationOperation, status);

        return new SandboxSourcePreparation
        {
            Status = ready ? SandboxSourcePreparationStatus.Ready : SandboxSourcePreparationStatus.Unversioned,
            OperationId = operationId?.ToString("D"),
            SourceVersion = sourceVersion,
            ErrorCode = ReadOptionalString(reply, "error_code", status),
            Sources = entries,
            RawSourceReceipt = receipt.Clone(),
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
