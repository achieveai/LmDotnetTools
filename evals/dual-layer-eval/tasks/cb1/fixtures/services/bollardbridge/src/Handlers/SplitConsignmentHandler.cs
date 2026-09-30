using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Bollardbridge.Models;

namespace Quillfeather.Services.Bollardbridge.Handlers;

/// <summary>
/// Handles SplitConsignment requests for bollardbridge. Called from the queue consumer.
/// </summary>
public sealed class SplitConsignmentHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public SplitConsignmentHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(SplitConsignmentRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("splitConsignment received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("bollardbridge.splitConsignment.skipped");
                continue;
            }

            await EnrichAsync(line, ct).ConfigureAwait(false);
            await MeasureAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("bollardbridge.splitConsignment.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task EnrichAsync(SplitConsignmentLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("enrich note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task MeasureAsync(SplitConsignmentLine line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line.Sku))
        {
            throw new ArgumentException("sku is required", nameof(line));
        }

        return Task.CompletedTask;
    }
}
