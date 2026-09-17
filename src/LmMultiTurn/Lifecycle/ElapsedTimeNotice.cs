using System.Collections.Immutable;
using System.Globalization;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;

/// <summary>
/// Builds and recognises the experimental elapsed-time notice (<see cref="ElapsedTimeNoticeOptions"/>).
/// </summary>
/// <remarks>
/// The notice is a <see cref="Role.User"/> <see cref="TextMessage"/> rather than a system-role one: a
/// system-role text mid-history replaces the system prompt on the Anthropic wire. Anthropic merges
/// consecutive same-role messages, so a user-role notice after the previous turn's tool results (also
/// a user-role turn) is safe cross-provider — the same discipline the wrap-up instruction relies on.
/// The <see cref="MetadataKey"/> marker is written inline by the message's shadow-property converter,
/// so it survives persistence and lets a host filter the row out of what a client reloads.
/// </remarks>
public static class ElapsedTimeNotice
{
    /// <summary>Metadata key that marks a <see cref="TextMessage"/> as an elapsed-time notice.</summary>
    public const string MetadataKey = "elapsed_time_notice";

    /// <summary>True when <paramref name="message"/> is an elapsed-time notice.</summary>
    public static bool IsNotice(IMessage message)
    {
        return message is TextMessage { Metadata: { } metadata } && metadata.ContainsKey(MetadataKey);
    }

    /// <summary>The notice for a request that has been running for <paramref name="elapsed"/> at <paramref name="now"/>.</summary>
    public static TextMessage Build(TimeSpan elapsed, DateTimeOffset now)
    {
        var text =
            "<elapsed-time-notice>\n"
            + $"You have been working on the current request for {FormatElapsed(elapsed)}.\n"
            + $"Current time: {now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC.\n"
            + "</elapsed-time-notice>";

        return new TextMessage
        {
            Text = text,
            Role = Role.User,
            Metadata = ImmutableDictionary<string, object>.Empty.Add(MetadataKey, true),
        };
    }

    /// <summary>Renders a duration as <c>59s</c>, <c>1m 1s</c> or <c>1h 2m 5s</c>.</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var totalSeconds = (long)Math.Floor(elapsed.TotalSeconds);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds % 3600 / 60;
        var seconds = totalSeconds % 60;

        return hours > 0 ? $"{hours}h {minutes}m {seconds}s"
            : minutes > 0 ? $"{minutes}m {seconds}s"
            : $"{seconds}s";
    }
}

/// <summary>
/// Per-loop clock for the notice: remembers when the current run started and when the last notice
/// went out, and hands back the next notice once an interval has elapsed since the later of the two.
/// </summary>
internal sealed class ElapsedTimeNoticeTracker(ElapsedTimeNoticeOptions options)
{
    private readonly TimeProvider _clock = options.Clock ?? TimeProvider.System;
    private DateTimeOffset _runStartedAt;
    private DateTimeOffset _lastNoticeAt;

    /// <summary>Resets both marks: a new request's clock starts at zero.</summary>
    public void OnRunStarted()
    {
        _runStartedAt = _clock.GetUtcNow();
        _lastNoticeAt = _runStartedAt;
    }

    /// <summary>The notice due at this turn boundary, or null when the interval has not elapsed.</summary>
    public TextMessage? NextNotice()
    {
        var now = _clock.GetUtcNow();
        if (now - _lastNoticeAt < options.Interval)
        {
            return null;
        }

        _lastNoticeAt = now;
        return ElapsedTimeNotice.Build(now - _runStartedAt, now);
    }
}
