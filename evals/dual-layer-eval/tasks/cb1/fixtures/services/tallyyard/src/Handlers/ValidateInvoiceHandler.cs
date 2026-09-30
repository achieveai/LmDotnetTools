using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Tallyyard.Models;

namespace Quillfeather.Services.Tallyyard.Handlers;

/// <summary>
/// Handles ValidateInvoice requests for tallyyard. Called from the queue consumer.
/// </summary>
public sealed class ValidateInvoiceHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public ValidateInvoiceHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(ValidateInvoiceRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("validateInvoice received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("tallyyard.validateInvoice.skipped");
                continue;
            }

            await NormaliseAsync(line, ct).ConfigureAwait(false);
            await EnrichAsync(line, ct).ConfigureAwait(false);
            await ValidateAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("tallyyard.validateInvoice.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task NormaliseAsync(ValidateInvoiceLine line, CancellationToken ct)
    {
        _log.Debug("normalise line", line.Sku);
        return Task.CompletedTask;
    }

    private Task EnrichAsync(ValidateInvoiceLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("enrich note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task ValidateAsync(ValidateInvoiceLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("validate note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }
}
