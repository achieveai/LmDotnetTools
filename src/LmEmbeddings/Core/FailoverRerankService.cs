using AchieveAi.LmDotnetTools.LmEmbeddings.Core.Internal;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

public class FailoverRerankService : IRerankService, IDisposable
{
    private readonly IRerankService _primary;
    private readonly IRerankService _backup;
    private readonly FailoverExecutor<IRerankService> _executor;
    private readonly ILogger<FailoverRerankService> _logger;

    public FailoverRerankService(
        IRerankService primary,
        IRerankService backup,
        FailoverOptions options,
        ILogger<FailoverRerankService>? logger = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _backup = backup ?? throw new ArgumentNullException(nameof(backup));
        ArgumentNullException.ThrowIfNull(options);

        if (options.PrimaryRequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.PrimaryRequestTimeout,
                "PrimaryRequestTimeout must be greater than TimeSpan.Zero.");
        }

        if (options.RecoveryInterval.HasValue && options.RecoveryInterval.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.RecoveryInterval,
                "RecoveryInterval must be greater than TimeSpan.Zero when specified.");
        }

        _logger = logger ?? NullLogger<FailoverRerankService>.Instance;
        _executor = new FailoverExecutor<IRerankService>(
            primary, backup, options, _logger, "rerank service");
    }

    public async Task<RerankResponse> RerankAsync(RerankRequest request, CancellationToken cancellationToken = default)
    {
        return await _executor.ExecuteAsync(
            (svc, ct) => svc.RerankAsync(request, ct),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RerankResponse> RerankAsync(string query, IReadOnlyList<string> documents, string model, int? topK = null, CancellationToken cancellationToken = default)
    {
        return await _executor.ExecuteAsync(
            (svc, ct) => svc.RerankAsync(query, documents, model, topK, ct),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        return await _executor.ExecuteAsync(
            (svc, ct) => svc.GetAvailableModelsAsync(ct),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Manually resets failover state to route requests to the primary service.
    /// Use when RecoveryInterval is null and automatic recovery is disabled.
    /// </summary>
    public void ResetToPrimary()
    {
        _executor.ResetToPrimary();
    }

#pragma warning disable CA1031 // Intentional: must dispose both services even if one throws
    public void Dispose()
    {
        try { (_primary as IDisposable)?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose primary rerank service."); }

        try { (_backup as IDisposable)?.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose backup rerank service."); }

        GC.SuppressFinalize(this);
    }
#pragma warning restore CA1031
}
