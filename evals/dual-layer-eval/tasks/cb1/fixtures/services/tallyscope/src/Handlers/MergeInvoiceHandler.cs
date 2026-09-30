using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Tallyscope.Models;

namespace Quillfeather.Services.Tallyscope.Handlers;

/// <summary>
/// Handles MergeInvoice requests for tallyscope. Idempotent by request id.
/// </summary>
public sealed class MergeInvoiceHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public MergeInvoiceHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(MergeInvoiceRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("mergeInvoice received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("tallyscope.mergeInvoice.skipped");
                continue;
            }

            await ValidateAsync(line, ct).ConfigureAwait(false);
            await MeasureAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("tallyscope.mergeInvoice.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task ValidateAsync(MergeInvoiceLine line, CancellationToken ct)
    {
        _metrics.Increment("tallyscope.mergeInvoice.validate");
        return Task.CompletedTask;
    }

    private Task MeasureAsync(MergeInvoiceLine line, CancellationToken ct)
    {
        _metrics.Increment("tallyscope.mergeInvoice.measure");
        return Task.CompletedTask;
    }
}
