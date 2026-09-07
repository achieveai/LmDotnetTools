namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>Chooses the message shape delivered by a multi-turn subscription.</summary>
public sealed record SubscribeOptions
{
    /// <summary>
    /// False preserves streaming updates and provider messages. True delivers canonical messages
    /// in history order, with visible reasoning projected to thinking text and no streaming updates.
    /// Lifecycle messages and the existing bounded-channel recovery behavior are retained.
    /// </summary>
    public bool JoinedOnly { get; init; }

    /// <summary>The existing streaming subscription behavior.</summary>
    public static SubscribeOptions Default { get; } = new();

    /// <summary>Canonical messages only.</summary>
    public static SubscribeOptions Joined { get; } = new() { JoinedOnly = true };
}
