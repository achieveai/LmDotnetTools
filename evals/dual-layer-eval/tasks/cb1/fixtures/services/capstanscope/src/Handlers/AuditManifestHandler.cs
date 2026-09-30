using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Capstanscope.Models;

namespace Quillfeather.Services.Capstanscope.Handlers;

/// <summary>
/// Handles AuditManifest requests for capstanscope. Idempotent by request id.
/// </summary>
public sealed class AuditManifestHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public AuditManifestHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(AuditManifestRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("auditManifest received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("capstanscope.auditManifest.skipped");
                continue;
            }

            await PersistAsync(line, ct).ConfigureAwait(false);
            await ValidateAsync(line, ct).ConfigureAwait(false);
            await EnrichAsync(line, ct).ConfigureAwait(false);
            await MeasureAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("capstanscope.auditManifest.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task PersistAsync(AuditManifestLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }

    private Task ValidateAsync(AuditManifestLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("validate note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task EnrichAsync(AuditManifestLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }

    private Task MeasureAsync(AuditManifestLine line, CancellationToken ct)
    {
        _metrics.Increment("capstanscope.auditManifest.measure");
        return Task.CompletedTask;
    }
}
