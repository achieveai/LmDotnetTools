using AchieveAi.LmDotnetTools.LmEmbeddings.Core.Internal;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Core;

public class FailoverEmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingService _primary;
    private readonly IEmbeddingService _backup;
    private readonly FailoverExecutor<IEmbeddingService> _executor;
    private readonly ILogger<FailoverEmbeddingService> _logger;

    public int EmbeddingSize => _primary.EmbeddingSize;

    public FailoverEmbeddingService(
        IEmbeddingService primary,
        IEmbeddingService backup,
        FailoverOptions options,
        ILogger<FailoverEmbeddingService>? logger = null)
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

        if (primary.EmbeddingSize != backup.EmbeddingSize)
        {
            throw new ArgumentException(
                $"Primary and backup embedding sizes must match. Primary: {primary.EmbeddingSize}, Backup: {backup.EmbeddingSize}.",
                nameof(backup));
        }

        _logger = logger ?? NullLogger<FailoverEmbeddingService>.Instance;
        _executor = new FailoverExecutor<IEmbeddingService>(
            primary, backup, options, _logger, "embedding service");
    }

    public async Task<float[]> GetEmbeddingAsync(string sentence, CancellationToken cancellationToken = default)
    {
        return await _executor.ExecuteAsync(
            (svc, ct) => svc.GetEmbeddingAsync(sentence, ct),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<EmbeddingResponse> GenerateEmbeddingsAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        return await _executor.ExecuteAsync(
            (svc, ct) => svc.GenerateEmbeddingsAsync(request, ct),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<EmbeddingResponse> GenerateEmbeddingAsync(string text, string model, CancellationToken cancellationToken = default)
    {
        return await _executor.ExecuteAsync(
            (svc, ct) => svc.GenerateEmbeddingAsync(text, model, ct),
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
        try { _primary.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose primary embedding service."); }

        try { _backup.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to dispose backup embedding service."); }

        GC.SuppressFinalize(this);
    }
#pragma warning restore CA1031
}
