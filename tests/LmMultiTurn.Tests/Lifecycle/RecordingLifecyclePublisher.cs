using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;
using FluentAssertions;

namespace LmMultiTurn.Tests.Lifecycle;

/// <summary>
/// A subscriber that keeps everything it is handed, so a test can assert on the events a loop
/// actually published rather than on the calls it made to publish them.
/// </summary>
/// <remarks>
/// Top-level rather than nested in one test class: loops publish from background threads, and every
/// lifecycle test needs the same thread-safe capture plus the same payload-decoding helpers. A second
/// copy would be a second place for the locking to be subtly wrong.
/// </remarks>
internal sealed class RecordingLifecyclePublisher : ILifecyclePublisher
{
    private readonly List<LifecycleEventEnvelope> _events = [];
    private readonly Lock _gate = new();

    /// <summary>Everything published so far, as a snapshot safe to enumerate while more arrives.</summary>
    public IReadOnlyList<LifecycleEventEnvelope> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>The event types in publication order — usually the whole assertion.</summary>
    public IReadOnlyList<string> EventTypes => [.. Events.Select(e => e.EventType)];

    public ValueTask PublishAsync(
        LifecycleEventEnvelope envelope,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            _events.Add(envelope);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Decodes the payload of the event at <paramref name="index"/>, failing the test if it will not decode.</summary>
    public TPayload PayloadAt<TPayload>(int index)
        where TPayload : class
    {
        LifecycleSerializer
            .TryReadPayload<TPayload>(Events[index], out var payload)
            .Should()
            .BeTrue();
        return payload!;
    }

    /// <summary>Decodes every payload published under <paramref name="eventType"/>, in order.</summary>
    public IReadOnlyList<TPayload> Payloads<TPayload>(string eventType)
        where TPayload : class =>
        [
            .. Events
                .Where(e => e.EventType == eventType)
                .Select(e =>
                {
                    LifecycleSerializer.TryReadPayload<TPayload>(e, out var payload).Should().BeTrue();
                    return payload!;
                }),
        ];

    /// <summary>The correlation blocks of every event published under <paramref name="eventType"/>, in order.</summary>
    public IReadOnlyList<LifecycleCorrelation> CorrelationsFor(string eventType) =>
        [
            .. Events
                .Where(e => e.EventType == eventType)
                .Select(e => e.Correlation ?? new LifecycleCorrelation()),
        ];
}
