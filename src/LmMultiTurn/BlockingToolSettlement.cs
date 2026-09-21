using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// How a blocking tool call — one that parked the run waiting for a human or an external event —
/// finally ended.
/// </summary>
internal enum BlockingToolEnding
{
    /// <summary>Nobody has ended it yet.</summary>
    Pending = 0,

    /// <summary>
    /// The loop settled it with its tool's placeholder so an interrupting input could run. The real
    /// answer, if one ever arrives, is redirected into the conversation instead of resolving the call.
    /// </summary>
    SettledEarly = 1,

    /// <summary>The real result arrived first and resolved the call in the ordinary way.</summary>
    Answered = 2,
}

/// <summary>
/// The one-shot ending of a single blocking tool call, and the one-shot delivery of the answer that
/// lost to an early settle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists on top of the coordinator's claim.</b>
/// <see cref="DelayedResultCoordinator.TryBeginResolve"/> already serialises two resolutions of the
/// same call under its own lock — but the loser is told only "somebody else holds this with a
/// different fingerprint", and <c>MultiTurnAgentLoop.ResolveToolCallInternalAsync</c> turns that into
/// <see cref="ResolveToolCallOutcome.Conflict"/> <em>without touching history</em>. When the loser is
/// the human's answer and the winner is an early settle, refusing it is exactly how the answer gets
/// lost. This latch is what lets the loser find out <em>which</em> ending won, so an answer that lost
/// can be redirected into the conversation rather than refused.
/// </para>
/// <para>
/// Claimed before the coordinator is asked, by both paths, so the two orders are symmetric: whichever
/// caller reaches the CAS first decides the ending, and the other one reads that decision instead of
/// racing it.
/// </para>
/// <para>
/// In-memory only, and deliberately so — it covers the live race. Survival across a loop rebuild (a
/// mode or provider switch) and a process restart comes from the placeholder that the early settle
/// writes into the resolved <c>ToolCallResultMessage</c> in history, which
/// <see cref="EarlySettlePlaceholders.IsEarlySettleResult"/> recognises.
/// </para>
/// </remarks>
internal sealed class BlockingToolOutcome
{
    private int _ending;
    private int _answerDelivered;

    /// <summary>How the call ended, or <see cref="BlockingToolEnding.Pending"/> while it has not.</summary>
    public BlockingToolEnding Ending => (BlockingToolEnding)Volatile.Read(ref _ending);

    /// <summary>Claims the ending for an early settle. Exactly one claim ever wins, forever.</summary>
    public bool TryClaimEarlySettle() => TryClaim(BlockingToolEnding.SettledEarly);

    /// <summary>Claims the ending for the real result. Exactly one claim ever wins, forever.</summary>
    public bool TryClaimRealResult() => TryClaim(BlockingToolEnding.Answered);

    /// <summary>
    /// Claims the right to inject the redirected answer. False for every delivery after the first, so
    /// a redelivered webhook or a double-submitted answer form cannot inject the same answer twice.
    /// </summary>
    public bool TryClaimAnswerDelivery() => Interlocked.Exchange(ref _answerDelivered, 1) == 0;

    private bool TryClaim(BlockingToolEnding ending) =>
        Interlocked.CompareExchange(ref _ending, (int)ending, (int)BlockingToolEnding.Pending)
        == (int)BlockingToolEnding.Pending;
}

/// <summary>
/// What a blocking tool returns when the loop settles it early, and how to restate its request to the
/// model when the real answer arrives later.
/// </summary>
/// <param name="ResultJson">
/// The tool result written in place of the real one. It is the tool's own envelope shape with a named,
/// non-error <c>status</c>, so a model branching on <c>status</c> sees a legitimate outcome rather than
/// a string to parse.
/// </param>
/// <param name="RenderRequest">
/// Renders the original call's arguments back into readable form, for the injected message that carries
/// the request alongside the late answer.
/// </param>
internal sealed record EarlySettleSpec(string ResultJson, Func<string?, string> RenderRequest);

/// <summary>
/// The tools the loop is allowed to settle early, and what it settles them with.
/// </summary>
/// <remarks>
/// <para>
/// A tool that is not in this table keeps the pre-existing fail-fast: a third-party tool with no async
/// fallback must never be silently settled with a lie about what happened.
/// </para>
/// <para>
/// <b>Adoption point.</b> The <c>WaitAgent</c>/<c>WaitForAgents</c> work adds one entry here, with the
/// placeholder text living as a <c>const</c> on the tool's own provider so the tool description and the
/// placeholder cannot drift apart.
/// </para>
/// </remarks>
internal static class EarlySettlePlaceholders
{
    private static readonly Dictionary<string, EarlySettleSpec> Specs = new(StringComparer.Ordinal)
    {
        [AskUserQuestionToolProvider.ToolName] = new EarlySettleSpec(
            AskUserQuestionToolProvider.EarlySettleResultJson,
            AskUserQuestionToolProvider.RenderRequestForInjection
        ),
    };

    /// <summary>The settlement for <paramref name="toolName"/>, or false when it has none.</summary>
    public static bool TryGet(string? toolName, out EarlySettleSpec spec)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            spec = null!;
            return false;
        }

        return Specs.TryGetValue(toolName, out spec!);
    }

    /// <summary>
    /// Whether <paramref name="result"/> is the placeholder an early settle left behind.
    /// </summary>
    /// <remarks>
    /// This is the durable half of the latch. The placeholder is written into the resolved
    /// <c>ToolCallResultMessage</c> by the ordinary resolution path, so it is persisted with the
    /// conversation and read back on a loop rebuild or a process restart — at which point the
    /// in-memory <see cref="BlockingToolOutcome"/> no longer exists and this is the only way a late
    /// answer can tell "settled early" from "answered with something else".
    /// </remarks>
    public static bool IsEarlySettleResult(string? result)
    {
        if (string.IsNullOrEmpty(result) || result[0] != '{')
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(result);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), EarlySettleStatus, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The <c>status</c> every early-settle placeholder carries. Shared across tools on purpose: it is
    /// the one token a model — and <see cref="IsEarlySettleResult"/> — branches on.
    /// </summary>
    public const string EarlySettleStatus = "deferred_to_notification";
}
