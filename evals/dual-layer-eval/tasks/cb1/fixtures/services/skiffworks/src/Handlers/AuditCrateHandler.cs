using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Skiffworks.Models;

namespace Quillfeather.Services.Skiffworks.Handlers;

/// <summary>
/// Handles AuditCrate requests for skiffworks. Safe to retry.
/// </summary>
public sealed class AuditCrateHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public AuditCrateHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(AuditCrateRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("auditCrate received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("skiffworks.auditCrate.skipped");
                continue;
            }

            await NormaliseAsync(line, ct).ConfigureAwait(false);
            await MeasureAsync(line, ct).ConfigureAwait(false);
            await PersistAsync(line, ct).ConfigureAwait(false);
            await ValidateAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("skiffworks.auditCrate.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task NormaliseAsync(AuditCrateLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }

    private Task MeasureAsync(AuditCrateLine line, CancellationToken ct)
    {
        _metrics.Increment("skiffworks.auditCrate.measure");
        return Task.CompletedTask;
    }

    private Task PersistAsync(AuditCrateLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }

    private Task ValidateAsync(AuditCrateLine line, CancellationToken ct)
    {
        _metrics.Increment("skiffworks.auditCrate.validate");
        return Task.CompletedTask;
    }
}
