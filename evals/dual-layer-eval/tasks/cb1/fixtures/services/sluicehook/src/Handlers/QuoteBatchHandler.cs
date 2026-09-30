using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Sluicehook.Models;

namespace Quillfeather.Services.Sluicehook.Handlers;

/// <summary>
/// Handles QuoteBatch requests for sluicehook. Called from the HTTP front door.
/// </summary>
public sealed class QuoteBatchHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public QuoteBatchHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(QuoteBatchRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("quoteBatch received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("sluicehook.quoteBatch.skipped");
                continue;
            }

            await NormaliseAsync(line, ct).ConfigureAwait(false);
            await EnrichAsync(line, ct).ConfigureAwait(false);
            await ValidateAsync(line, ct).ConfigureAwait(false);
            await PersistAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("sluicehook.quoteBatch.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task NormaliseAsync(QuoteBatchLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("normalise note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task EnrichAsync(QuoteBatchLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("enrich note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task ValidateAsync(QuoteBatchLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("validate note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task PersistAsync(QuoteBatchLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("persist note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }
}
