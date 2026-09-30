using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Gantrybridge.Models;

namespace Quillfeather.Services.Gantrybridge.Handlers;

/// <summary>
/// Handles AuditConsignment requests for gantrybridge. Safe to retry.
/// </summary>
public sealed class AuditConsignmentHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public AuditConsignmentHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(AuditConsignmentRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("auditConsignment received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("gantrybridge.auditConsignment.skipped");
                continue;
            }

            await MeasureAsync(line, ct).ConfigureAwait(false);
            await EnrichAsync(line, ct).ConfigureAwait(false);
            await PublishAsync(line, ct).ConfigureAwait(false);
            await NormaliseAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("gantrybridge.auditConsignment.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task MeasureAsync(AuditConsignmentLine line, CancellationToken ct)
    {
        _log.Debug("measure line", line.Sku);
        return Task.CompletedTask;
    }

    private Task EnrichAsync(AuditConsignmentLine line, CancellationToken ct)
    {
        _metrics.Increment("gantrybridge.auditConsignment.enrich");
        return Task.CompletedTask;
    }

    private Task PublishAsync(AuditConsignmentLine line, CancellationToken ct)
    {
        _log.Debug("publish line", line.Sku);
        return Task.CompletedTask;
    }

    private Task NormaliseAsync(AuditConsignmentLine line, CancellationToken ct)
    {
        _metrics.Increment("gantrybridge.auditConsignment.normalise");
        return Task.CompletedTask;
    }
}
