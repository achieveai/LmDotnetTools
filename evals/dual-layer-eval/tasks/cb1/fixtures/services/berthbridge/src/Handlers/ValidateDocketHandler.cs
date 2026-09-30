using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Berthbridge.Models;

namespace Quillfeather.Services.Berthbridge.Handlers;

/// <summary>
/// Handles ValidateDocket requests for berthbridge. Idempotent by request id.
/// </summary>
public sealed class ValidateDocketHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public ValidateDocketHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(ValidateDocketRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("validateDocket received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("berthbridge.validateDocket.skipped");
                continue;
            }

            await MeasureAsync(line, ct).ConfigureAwait(false);
            await EnrichAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("berthbridge.validateDocket.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task MeasureAsync(ValidateDocketLine line, CancellationToken ct)
    {
        _metrics.Increment("berthbridge.validateDocket.measure");
        return Task.CompletedTask;
    }

    private Task EnrichAsync(ValidateDocketLine line, CancellationToken ct)
    {
        _log.Debug("enrich line", line.Sku);
        return Task.CompletedTask;
    }
}
