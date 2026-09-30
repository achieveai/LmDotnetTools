using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Skiffgate.Models;

namespace Quillfeather.Services.Skiffgate.Handlers;

/// <summary>
/// Handles AuditPallet requests for skiffgate. Called from the queue consumer.
/// </summary>
public sealed class AuditPalletHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public AuditPalletHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(AuditPalletRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("auditPallet received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("skiffgate.auditPallet.skipped");
                continue;
            }

            await ValidateAsync(line, ct).ConfigureAwait(false);
            await PublishAsync(line, ct).ConfigureAwait(false);
            await NormaliseAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("skiffgate.auditPallet.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task ValidateAsync(AuditPalletLine line, CancellationToken ct)
    {
        _log.Debug("validate line", line.Sku);
        return Task.CompletedTask;
    }

    private Task PublishAsync(AuditPalletLine line, CancellationToken ct)
    {
        _log.Debug("publish line", line.Sku);
        return Task.CompletedTask;
    }

    private Task NormaliseAsync(AuditPalletLine line, CancellationToken ct)
    {
        _log.Debug("normalise line", line.Sku);
        return Task.CompletedTask;
    }
}
