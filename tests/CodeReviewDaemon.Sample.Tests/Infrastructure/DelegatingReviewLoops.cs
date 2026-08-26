using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Forwards every <see cref="IMultiTurnAgent"/> member to an inner loop, modelling the live path's decorator
/// (<c>ToolScopedReviewLoop</c>) without its MCP-client ownership. The subclasses below differ ONLY in which
/// capability interfaces they declare, which is exactly what sub-agent surface resolution keys off.
/// </summary>
internal abstract class DelegatingLoop(IMultiTurnAgent inner) : IMultiTurnAgent
{
    protected IMultiTurnAgent Wrapped => inner;

    public string? CurrentRunId => inner.CurrentRunId;

    /// <summary>Virtual because a decorator standing in for the HOSTED loop reports the conversation the host
    /// minted, not the daemon-local id of the loop underneath.</summary>
    public virtual string ThreadId => inner.ThreadId;

    public bool IsRunning => inner.IsRunning;

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default
    ) => inner.SendAsync(messages, inputId, parentRunId, ct);

    public ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default
    ) => inner.TrySendAsync(messages, inputId, parentRunId, ct);

    /// <summary>Virtual so a decorator can do what the hosted loop does around a turn — provision the
    /// conversation, resolve the armed checkpoint — before the inner script runs.</summary>
    public virtual IAsyncEnumerable<IMessage> ExecuteRunAsync(UserInput userInput, CancellationToken ct = default) =>
        inner.ExecuteRunAsync(userInput, ct);

    public IAsyncEnumerable<IMessage> SubscribeAsync(CancellationToken ct = default) => inner.SubscribeAsync(ct);

    public Task RunAsync(CancellationToken ct = default) => inner.RunAsync(ct);

    public Task StopAsync(TimeSpan? timeout = null) => inner.StopAsync(timeout);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>A decorator that DECLARES what it wraps, so the surface resolves through it.</summary>
internal sealed class WrappingLoop(IMultiTurnAgent inner) : DelegatingLoop(inner), IReviewLoopWrapper
{
    public IMultiTurnAgent Inner => Wrapped;
}

/// <summary>A decorator that declares NOTHING — the executor cannot tell whether it can spawn.</summary>
internal sealed class OpaqueLoop(IMultiTurnAgent inner) : DelegatingLoop(inner);

/// <summary>
/// The RESUMABLE half of the review-loop double, deliberately split out of <see cref="FakeMultiTurnAgent"/>.
/// Only the S2S path's turns are durable — they run on a host that outlives the daemon — so a single fake
/// that offered resumability on both paths would let an in-process test pass on a checkpoint production
/// would never be able to write. Wrapping instead keeps the two paths as different in the tests as they are
/// in production, and (via <see cref="IReviewLoopWrapper"/>) proves the executor resolves the capability
/// THROUGH a decorator rather than off the loop it happens to hold.
/// <para>
/// It models the hosted agent's three observable behaviours: a conversation is minted exactly once and only
/// when the loop was not seeded with one to resume; the mint callback is load-bearing, so a throw from it
/// takes the turn down; and arming is ONE SHOT — the armed turn either rejoins an already-accepted input
/// (<see cref="RejoinedInputIds"/>, nothing newly queued) or reports the id it just accepted
/// (<see cref="AcceptedInputIds"/>) before producing anything.
/// </para>
/// </summary>
internal sealed class ResumableFakeLoop(IMultiTurnAgent inner, string? resumeHostedThreadId, string mintedThreadId)
    : DelegatingLoop(inner),
        IReviewLoopWrapper,
        IResumableReviewTurn,
        IDeadlineBoundedReviewLoop,
        IPerTurnModelReviewLoop
{
    private string? _hostedThreadId = resumeHostedThreadId;
    private Action<string>? _onConversationMinted;
    private string? _armedIdempotencyKey;
    private string? _armedResumeInputId;
    private Action<string, string?>? _onInputAccepted;
    private bool _provisioned;

    public IMultiTurnAgent Inner => Wrapped;

    /// <summary>Forwards the attempt's shared budget, exactly as the live <c>ToolScopedReviewLoop</c> does. A
    /// decorator that swallowed it would leave the loop underneath unbounded — and, in a test, would let a
    /// deadline assertion pass vacuously against a collection nothing ever wrote to.</summary>
    public void UseDeadline(DateTimeOffset deadlineUtc) =>
        (Wrapped as IDeadlineBoundedReviewLoop)?.UseDeadline(deadlineUtc);

    /// <summary>The hosted conversation: the resumed one when seeded, otherwise the id minted on first use.
    /// Falls back to the inner loop's id only before either has happened.</summary>
    public override string ThreadId => _hostedThreadId ?? Wrapped.ThreadId;

    /// <summary>The id reported as newly accepted when an armed turn is SENT rather than rejoined.</summary>
    public string NextInputId { get; set; } = "input-1";

    /// <summary>Idempotency keys passed to <see cref="ArmTurnCheckpoint"/>, in order.</summary>
    public List<string> ArmedIdempotencyKeys { get; } = [];

    /// <summary>Accepted-input ids passed to <see cref="ArmTurnCheckpoint"/>, in order (null = send a new turn).</summary>
    public List<string?> ArmedResumeInputIds { get; } = [];

    /// <summary>Ids reported to the caller as newly accepted — i.e. turns this double SENT.</summary>
    public List<string> AcceptedInputIds { get; } = [];

    /// <summary>Ids of turns REJOINED rather than sent; nothing new was queued for these.</summary>
    public List<string> RejoinedInputIds { get; } = [];

    /// <summary>Conversations this loop minted (at most one, and none at all when it resumed one).</summary>
    public List<string> MintedThreadIds { get; } = [];

    /// <summary>One-turn model overrides requested by the executor, in order.</summary>
    public List<string> RequestedModelIds { get; } = [];

    public void UseModelForNextTurn(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        RequestedModelIds.Add(modelId.Trim());
    }

    public void ObserveConversationMint(Action<string> onConversationMinted)
    {
        ArgumentNullException.ThrowIfNull(onConversationMinted);
        _onConversationMinted = onConversationMinted;
    }

    public void ArmTurnCheckpoint(
        string idempotencyKey,
        string? acceptedInputId,
        Action<string, string?>? onInputAccepted
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArmedIdempotencyKeys.Add(idempotencyKey);
        ArmedResumeInputIds.Add(acceptedInputId);
        _armedIdempotencyKey = idempotencyKey;
        _armedResumeInputId = acceptedInputId;
        _onInputAccepted = onInputAccepted;
    }

    public override async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        EnsureProvisioned();
        ResolveArmedTurn();
        await foreach (var message in Wrapped.ExecuteRunAsync(userInput, ct).WithCancellation(ct))
        {
            yield return message;
        }
    }

    /// <summary>Mints the conversation on first use — unless one was seeded, which is a RESUME and mints
    /// nothing. The callback is invoked unguarded on purpose: it is the checkpoint that makes the fan-out
    /// this turn is about to start findable again, so a failure to record it must fail the turn.</summary>
    private void EnsureProvisioned()
    {
        if (_provisioned)
        {
            return;
        }

        _provisioned = true;
        if (_hostedThreadId is not null)
        {
            return;
        }

        _hostedThreadId = mintedThreadId;
        MintedThreadIds.Add(mintedThreadId);
        _onConversationMinted?.Invoke(mintedThreadId);
    }

    /// <summary>Applies the one-shot arming to the turn that is starting: consumed either way, so a later
    /// unarmed turn neither rejoins a spent input nor re-reports one.</summary>
    private void ResolveArmedTurn()
    {
        var key = _armedIdempotencyKey;
        var rejoin = _armedResumeInputId;
        var onAccepted = _onInputAccepted;
        _armedIdempotencyKey = null;
        _armedResumeInputId = null;
        _onInputAccepted = null;
        if (key is null)
        {
            return;
        }

        if (rejoin is not null)
        {
            RejoinedInputIds.Add(rejoin);
            return;
        }

        AcceptedInputIds.Add(NextInputId);
        onAccepted?.Invoke(NextInputId, RequestedModelIds.LastOrDefault());
    }
}

/// <summary>
/// A decorator whose <see cref="Inner"/> can be re-pointed AFTER construction, so a test can tie the knot a
/// real decorator only produces by accident: a wrapper that reports ITSELF as the loop it wraps, or a pair
/// that report each other. Surface resolution must reject those with a catchable exception — an unguarded
/// recursion would raise StackOverflowException, which cannot be caught and takes the daemon down with it.
/// </summary>
internal sealed class MutableWrappingLoop(IMultiTurnAgent inner) : DelegatingLoop(inner), IReviewLoopWrapper
{
    public IMultiTurnAgent Inner { get; set; } = inner;
}

/// <summary>
/// A decorator that declares BOTH interfaces — the shape that would let an outer surface mask the loop it
/// wraps if resolution short-circuited on the first declaration instead of merging member by member.
/// Its own capabilities default to null ("I add nothing of my own").
/// </summary>
internal sealed class SurfaceDeclaringWrapper(IMultiTurnAgent inner)
    : DelegatingLoop(inner),
        IReviewLoopWrapper,
        IReviewLoopSubAgentSurface
{
    public IMultiTurnAgent Inner => Wrapped;

    public IReviewSubAgentCompletionSource? CompletionSource { get; set; }

    public Func<IDisposable>? SuppressSpawning { get; set; }
}
