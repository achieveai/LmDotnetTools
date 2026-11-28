using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    private const string PluginPreparationPath = "api/v1/apps/self/plugins/prepare";
    private static readonly TimeSpan S_pluginPreparationPollDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Prepares an explicit same-app marketplace selection and returns the gateway's dependency-expanded references.
    /// The configured transport timeout covers submission and any polling together. Status, operation id and failure
    /// handling are those of <see cref="PrepareSourcesAsync"/>: a <c>failed</c> status throws with the same
    /// <see cref="SandboxErrorKind"/> for its <c>error_code</c>, and a restored operation id is followed.
    /// </summary>
    public async Task<IReadOnlyList<SandboxPluginRef>> PrepareMarketplacePluginSelectionAsync(
        IReadOnlyList<string> marketplaces,
        IReadOnlyList<SandboxPluginRef> pluginSelection,
        CancellationToken ct = default
    )
    {
        var request = CreatePrepareSelectionRequest(marketplaces, pluginSelection, ct);
        return await PrepareSelectionAsync(
                request,
                (reply, status) =>
                    ReadEffectivePreparation(reply, request.PluginSelection, PreparationOperation, status),
                ct
            )
            .ConfigureAwait(false);
    }

    /// <summary>Prepares an explicit selection with exact publisher-archive receipts and returns its source version.</summary>
    public async Task<SandboxArchivePreparation> PrepareMarketplaceArchiveSelectionAsync(
        IReadOnlyList<string> marketplaces,
        IReadOnlyList<SandboxPluginRef> pluginSelection,
        IReadOnlyList<SandboxAppMarketplaceArchive> archives,
        CancellationToken ct = default
    )
    {
        var request = CreatePrepareSelectionRequest(marketplaces, pluginSelection, ct);
        ArgumentNullException.ThrowIfNull(archives);
        var expected = archives.ToArray();
        ValidateArchiveSelection(expected, request.Marketplaces, request.PluginSelection);

        var archiveRequest = request with { ArchiveReceipts = [.. expected.Select(ToArchiveReceiptDto)] };
        return await PrepareSelectionAsync(
                archiveRequest,
                (reply, status) =>
                {
                    var resolved = ReadResolvedArchiveReceipts(
                        reply.ResolvedArchiveReceipts,
                        reply.SourceVersion,
                        PreparationOperation,
                        status
                    );
                    if (reply.Status != "ready" || reply.SourceVersion is null || !MatchesArchives(expected, resolved))
                    {
                        throw new SandboxException(
                            SandboxErrorKind.Protocol,
                            "The gateway did not prepare the exact archive selection.",
                            status
                        );
                    }

                    var effective = ReadEffectivePreparation(
                        reply,
                        archiveRequest.PluginSelection,
                        PreparationOperation,
                        status
                    );
                    return new SandboxArchivePreparation(reply.SourceVersion, effective, resolved);
                },
                ct
            )
            .ConfigureAwait(false);
    }

    private const string PreparationOperation = "preparing marketplace plugin selection";

    private static PrepareSelectionRequest CreatePrepareSelectionRequest(
        IReadOnlyList<string> marketplaces,
        IReadOnlyList<SandboxPluginRef> pluginSelection,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(marketplaces);
        ArgumentNullException.ThrowIfNull(pluginSelection);
        ct.ThrowIfCancellationRequested();
        if (marketplaces.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Marketplace aliases cannot be blank.", nameof(marketplaces));
        }
        if (pluginSelection.Any(static reference => reference is null))
        {
            throw new ArgumentException("A plugin selection cannot contain a null reference.", nameof(pluginSelection));
        }

        return new PrepareSelectionRequest([.. marketplaces], [.. pluginSelection.Select(ToPluginRefDto)]);
    }

    /// <summary>
    /// Runs the shared prepare/status loop (<see cref="RunSourcePreparationAsync"/>) for a selection, bounded by the
    /// configured transport timeout as a whole and polling every 100 ms. <paramref name="read"/> validates the terminal
    /// reply inside that loop, so a rejected reply still carries the operation id it belongs to.
    /// </summary>
    private Task<T> PrepareSelectionAsync<T>(
        PrepareSelectionRequest request,
        Func<PrepareSelectionReply, int, T> read,
        CancellationToken ct
    ) =>
        RunSourcePreparationAsync(
            request,
            resumeOperationId: null,
            _options.TransportTimeout,
            S_pluginPreparationPollDelay,
            PreparationOperation,
            prepared => read(ReadSelectionReply(prepared), prepared.StatusCode),
            ct
        );

    private static PrepareSelectionReply ReadSelectionReply(PreparationReply prepared)
    {
        try
        {
            return prepared.Body.Deserialize<PrepareSelectionReply>(SandboxJson.RestOptions)
                ?? throw new JsonException("The preparation reply was null.");
        }
        catch (JsonException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a malformed body {PreparationOperation}.",
                prepared.StatusCode,
                ex
            );
        }
    }

    private void ValidateArchiveSelection(
        IReadOnlyList<SandboxAppMarketplaceArchive> archives,
        IReadOnlyList<string> marketplaces,
        IReadOnlyList<PluginRefDto>? pluginSelection
    )
    {
        if (
            archives.Count == 0
            || pluginSelection is null
            || archives.Any(receipt =>
                receipt is null
                || receipt.AppId != _options.AppId
                || !IsArchiveAlias(receipt.Alias)
                || !IsArchiveUrl(receipt.Url)
                || !IsArchivePlugin(receipt.Plugin)
                || !IsArchiveDigest(receipt.Sha256)
                || !IsArchiveDigest(receipt.ContentSha256)
                || marketplaces.Count(alias => string.Equals(alias, receipt.Alias, StringComparison.OrdinalIgnoreCase))
                    != 1
                || !marketplaces.Contains(receipt.Alias, StringComparer.Ordinal)
                || pluginSelection.Count(plugin =>
                    string.Equals(plugin.Marketplace, receipt.Alias, StringComparison.OrdinalIgnoreCase)
                ) != 1
                || !pluginSelection.Any(plugin =>
                    plugin.Marketplace == receipt.Alias && plugin.Plugin == receipt.Plugin
                )
            )
            || archives.Select(receipt => receipt.Alias).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != archives.Count
        )
        {
            throw new ArgumentException(
                "Each receipt must name a distinct selected archive package registered by this app.",
                nameof(archives)
            );
        }
    }

    private static bool IsArchiveDigest(string? value) => value is { Length: 64 } && value.All(IsLowerHexDigit);

    private static AppMarketplaceArchiveReceiptDto ToArchiveReceiptDto(SandboxAppMarketplaceArchive archive) =>
        new(
            "archive",
            archive.AppId,
            archive.Alias,
            archive.Url,
            archive.Sha256,
            archive.ContentSha256,
            archive.Plugin
        );

    private IReadOnlyList<SandboxAppMarketplaceArchive> ReadResolvedArchiveReceipts(
        JsonElement? echo,
        string? sourceVersion,
        string operation,
        int status
    )
    {
        if (echo is null || echo.Value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        List<AppMarketplaceArchiveReceiptDto?>? rows;
        try
        {
            rows = echo.Value.Deserialize<List<AppMarketplaceArchiveReceiptDto?>>(SandboxJson.RestOptions);
        }
        catch (JsonException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned invalid archive receipts while {operation}.",
                status,
                ex
            );
        }

        if (rows is not { Count: > 0 } || string.IsNullOrWhiteSpace(sourceVersion) || sourceVersion.Any(char.IsControl))
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned no exact archive source while {operation}.",
                status
            );
        }

        var receipts = rows.Select(row => ReadArchiveReceipt(row, operation, status)).ToArray();
        if (
            receipts.Select(receipt => receipt.Alias).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != receipts.Length
        )
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned duplicate archive receipts while {operation}.",
                status
            );
        }

        return Array.AsReadOnly(receipts);
    }

    private static bool MatchesArchives(
        IReadOnlyList<SandboxAppMarketplaceArchive> expected,
        IReadOnlyList<SandboxAppMarketplaceArchive> actual
    ) => expected.Count == actual.Count && expected.ToHashSet().SetEquals(actual);

    private static IReadOnlyList<SandboxPluginRef> ReadEffectivePreparation(
        PrepareSelectionReply reply,
        IReadOnlyList<PluginRefDto> requested,
        string operation,
        int status
    )
    {
        const string field = "sourceReceipt.recipe.effective";
        var effective =
            ToPluginRefs(reply.SourceReceipt?.Recipe?.Effective, field, operation, status)
            ?? throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned no '{field}' list while {operation}.",
                status
            );
        var identities = effective.Select(static reference => (reference.Marketplace, reference.Plugin)).ToHashSet();
        if (
            identities.Count != effective.Count
            || requested.Any(reference => !identities.Contains((reference.Marketplace, reference.Plugin)))
        )
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned duplicate references or omitted requested plugins from '{field}' while {operation}.",
                status
            );
        }

        return effective;
    }

    private sealed record PrepareSelectionRequest(
        [property: JsonPropertyName("marketplaces")] IReadOnlyList<string> Marketplaces,
        [property: JsonPropertyName("pluginSelection")] IReadOnlyList<PluginRefDto> PluginSelection,
        [property: JsonPropertyName("archiveReceipts")]
            IReadOnlyList<AppMarketplaceArchiveReceiptDto>? ArchiveReceipts = null
    );

    private sealed record PrepareSelectionReply(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("operationId")] string? OperationId,
        [property: JsonPropertyName("sourceReceipt")] PrepareSourceReceipt? SourceReceipt,
        [property: JsonPropertyName("error_code")] string? ErrorCode,
        [property: JsonPropertyName("sourceVersion")] string? SourceVersion,
        [property: JsonPropertyName("resolvedArchiveReceipts")] JsonElement? ResolvedArchiveReceipts
    );

    private sealed record PrepareSourceReceipt([property: JsonPropertyName("recipe")] PrepareRecipe? Recipe);

    private sealed record PrepareRecipe(
        [property: JsonPropertyName("effective")] IReadOnlyList<PluginRefDto>? Effective
    );
}
