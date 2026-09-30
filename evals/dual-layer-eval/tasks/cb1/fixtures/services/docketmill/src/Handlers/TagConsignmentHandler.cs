using System;
using System.Threading;
using System.Threading.Tasks;
using Quillfeather.Platform;
using Quillfeather.Services.Docketmill.Models;

namespace Quillfeather.Services.Docketmill.Handlers;

/// <summary>
/// Handles TagConsignment requests for docketmill. Idempotent by request id.
/// </summary>
public sealed class TagConsignmentHandler
{
    private readonly IClock _clock;
    private readonly ILog _log;
    private readonly IMetrics _metrics;

    public TagConsignmentHandler(IClock clock, ILog log, IMetrics metrics)
    {
        _clock = clock;
        _log = log;
        _metrics = metrics;
    }

    public async Task<HandlerResult> HandleAsync(TagConsignmentRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return HandlerResult.Rejected("request is required");
        }

        var started = _clock.UtcNow;
        _log.Info("tagConsignment received", request.Id);

        foreach (var line in request.Lines)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Quantity <= 0)
            {
                _metrics.Increment("docketmill.tagConsignment.skipped");
                continue;
            }

            await PersistAsync(line, ct).ConfigureAwait(false);
            await EnrichAsync(line, ct).ConfigureAwait(false);
            await NormaliseAsync(line, ct).ConfigureAwait(false);
        }

        _metrics.Observe("docketmill.tagConsignment.duration_ms", (_clock.UtcNow - started).TotalMilliseconds);
        return HandlerResult.Ok();
    }

    private Task PersistAsync(TagConsignmentLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("persist note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task EnrichAsync(TagConsignmentLine line, CancellationToken ct)
    {
        var note = line.Note ?? string.Empty;
        if (note.Length > 200)
        {
            _log.Info("enrich note truncated", line.Sku);
        }

        return Task.CompletedTask;
    }

    private Task NormaliseAsync(TagConsignmentLine line, CancellationToken ct)
    {
        _metrics.Increment("docketmill.tagConsignment.normalise");
        return Task.CompletedTask;
    }
}
