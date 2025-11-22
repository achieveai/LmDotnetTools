using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.AgUi.DataObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Publishing;

/// <summary>
/// Channel-based event publisher implementation
/// </summary>
public class ChannelEventPublisher : IEventPublisher
{
    private readonly ConcurrentDictionary<string, SessionChannel> _sessions = new();
    private readonly ILogger<ChannelEventPublisher> _logger;
    private readonly int _channelCapacity;

    public ChannelEventPublisher(ILogger<ChannelEventPublisher>? logger = null, int channelCapacity = 1000)
    {
        _logger = logger ?? NullLogger<ChannelEventPublisher>.Instance;
        _channelCapacity = channelCapacity;
    }

    /// <inheritdoc/>
    public async Task PublishAsync(AgUiEventBase evt, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(evt.SessionId))
        {
            _logger.LogWarning("Event {EventType} without SessionId cannot be published", evt.Type);
            return;
        }

        var channel = GetOrCreateChannel(evt.SessionId);

        try
        {
            await channel.WriteAsync(evt, ct);
            _logger.LogDebug("Published {EventType} event to session {SessionId}", evt.Type, evt.SessionId);
        }
        catch (ChannelClosedException)
        {
            _logger.LogWarning("Channel for session {SessionId} is closed", evt.SessionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Publishing event {EventType} to session {SessionId} was cancelled",
                evt.Type,
                evt.SessionId
            );
            throw;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<AgUiEventBase> SubscribeAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        var channel = GetOrCreateChannel(sessionId);
        _logger.LogInformation("Subscriber connected to session {SessionId}", sessionId);

        await foreach (var evt in channel.ReadAllAsync(ct))
        {
            yield return evt;
        }

        _logger.LogInformation("Subscriber disconnected from session {SessionId}", sessionId);
    }

    /// <inheritdoc/>
    public void Unsubscribe(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var channel))
        {
            channel.Complete();
            _logger.LogInformation("Session {SessionId} channel closed", sessionId);
        }
    }

    private SessionChannel GetOrCreateChannel(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, _ => new SessionChannel(_channelCapacity, _logger));
    }

    private class SessionChannel
    {
        private readonly Channel<AgUiEventBase> _channel;
        private readonly ILogger _logger;

        public SessionChannel(int capacity, ILogger logger)
        {
            _logger = logger;

            var options = new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            };

            _channel = Channel.CreateBounded<AgUiEventBase>(options);
        }

        public async Task WriteAsync(AgUiEventBase evt, CancellationToken ct)
        {
            await _channel.Writer.WriteAsync(evt, ct);
        }

        public IAsyncEnumerable<AgUiEventBase> ReadAllAsync(CancellationToken ct)
        {
            return _channel.Reader.ReadAllAsync(ct);
        }

        public void Complete()
        {
            _ = _channel.Writer.TryComplete();
        }
    }
}
