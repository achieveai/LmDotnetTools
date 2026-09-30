using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Ledgerbridge.Models;

namespace Quillfeather.Services.Ledgerbridge.Handlers;

/// <summary>
/// Handles QuoteCrate requests for ledgerbridge. Called from the queue consumer.
/// </summary>
public sealed class QuoteCrateHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public QuoteCrateHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(QuoteCrateRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("quoteCrate received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("ledgerbridge.quoteCrate.skipped");
                continue;
            }

            await NormaliseAsync(line, ct).ConfigureAwait(false);
            await MeasureAsync(line, ct).ConfigureAwait(false);
            await ValidateAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("ledgerbridge.quoteCrate.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task NormaliseAsync(QuoteCrateLine line, CancellationToken ct)
    {
        _metrics.Increment("ledgerbridge.quoteCrate.normalise");
        return Task.CompletedTask;
    }

    private Task MeasureAsync(QuoteCrateLine line, CancellationToken ct)
    {
        _metrics.Increment("ledgerbridge.quoteCrate.measure");
        return Task.CompletedTask;
    }

    private Task ValidateAsync(QuoteCrateLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("validate note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }
}
