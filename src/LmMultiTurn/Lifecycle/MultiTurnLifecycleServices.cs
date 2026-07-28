using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;

/// <summary>
/// Everything a multi-turn loop needs in order to observe its own lifecycle and gate its own tool
/// calls, carried as one value so a loop's constructor grows by one parameter rather than six.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a bundle.</b> These parts always travel together: a host that wants lifecycle events also
/// wants them ordered by a shared allocator, attributed to the right lineage, and — if it gates
/// tools at all — gated by one preparer. Threading them as separate optional parameters would add
/// six positional slots to every loop constructor and to every factory that forwards to one, and
/// each future addition would break every call site again. One trailing parameter with an inert
/// default keeps existing construction sites compiling untouched.
/// </para>
/// <para>
/// <b>The default is off.</b> <see cref="Disabled"/> publishes nowhere, persists nothing, gates
/// nothing, and reports no lineage. A loop constructed without a bundle behaves exactly as it did
/// before lifecycle hooks existed — no extra allocation, no extra store round trip, no extra
/// await.
/// </para>
/// <para>
/// <b>Share one instance across the loops of a process.</b> <see cref="SequenceAllocator"/> owns
/// the producer epoch and the per-stream ordinals; loops that share a bundle therefore share an
/// epoch, which is what lets a subscriber tell "the producer restarted" from "events were lost".
/// A sub-agent's bundle should be derived from its parent's with <c>with { Lineage = ... }</c>
/// rather than constructed fresh, so the allocator, publisher, and approval configuration are the
/// ones the host actually wired up.
/// </para>
/// </remarks>
public sealed record MultiTurnLifecycleServices
{
    /// <summary>
    /// A bundle that enables nothing. The default for every loop, and the value a legacy
    /// construction site gets without changing.
    /// </summary>
    public static MultiTurnLifecycleServices Disabled { get; } = new();

    /// <summary>
    /// Where lifecycle events go. Defaults to <see cref="NullLifecyclePublisher"/>, which accepts
    /// and discards everything.
    /// </summary>
    public ILifecyclePublisher Publisher { get; init; } = NullLifecyclePublisher.Instance;

    /// <summary>
    /// Supplies per-stream ordinals and the producer epoch. Defaults to a fresh in-process
    /// allocator, so a host that forgets to supply one still gets ordered events — they are just
    /// ordered within this bundle rather than within the process.
    /// </summary>
    public ILifecycleSequenceAllocator SequenceAllocator { get; init; } =
        new InMemoryLifecycleSequenceAllocator();

    /// <summary>
    /// Durable lifecycle state, when the host wants runs to survive a restart.
    /// </summary>
    /// <remarks>
    /// Leave this null to have the loop use its conversation store when that store also implements
    /// <see cref="IRunLifecycleStore"/> — the built-in in-memory, file, and SQLite stores all do.
    /// Set it explicitly to persist lifecycle somewhere other than where messages live.
    /// </remarks>
    public IRunLifecycleStore? LifecycleStore { get; init; }

    /// <summary>
    /// Decides whether a tool call may run. Defaults to
    /// <see cref="ToolInvocationPreparer.Disabled"/>, which approves everything without consulting
    /// anything.
    /// </summary>
    public ToolInvocationPreparer Approval { get; init; } = ToolInvocationPreparer.Disabled;

    /// <summary>Where this agent came from. Defaults to <see cref="AgentLineage.None"/>.</summary>
    public AgentLineage Lineage { get; init; } = AgentLineage.None;

    /// <summary>
    /// Which agent implementation is running. See <see cref="LifecycleAgentKinds"/>. Open
    /// vocabulary; defaults to <see cref="LifecycleAgentKinds.Raw"/>.
    /// </summary>
    public string AgentKind { get; init; } = LifecycleAgentKinds.Raw;

    /// <summary>The model the loop was configured with, when the host knows it.</summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// The clock stamped onto events and durable records. Defaults to
    /// <see cref="TimeProvider.System"/>; tests substitute a controllable one.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Whether events reach a real subscriber.</summary>
    public bool PublishesEvents => !ReferenceEquals(Publisher, NullLifecyclePublisher.Instance);

    /// <summary>
    /// Whether this bundle asks the loop to do anything at all beyond its baseline behavior.
    /// </summary>
    public bool IsEnabled => PublishesEvents || LifecycleStore != null || Approval.IsEnabled;

    /// <summary>
    /// Stamps a host-supplied bundle with the identity of the loop that is about to use it.
    /// </summary>
    /// <param name="services">What the host passed, possibly null.</param>
    /// <param name="agentKind">
    /// The loop's own kind. See <see cref="LifecycleAgentKinds"/>. Always wins: the loop knows which
    /// implementation is running and the host, at best, guesses.
    /// </param>
    /// <param name="modelId">
    /// The model the loop was configured with. Yields to a host-supplied
    /// <see cref="ModelId"/>, which is likelier to be the resolved identifier a subscriber can
    /// price than whatever string the loop was handed.
    /// </param>
    /// <returns>
    /// The stamped bundle, or <see cref="Disabled"/> unchanged when there is nothing to observe —
    /// so a loop constructed without lifecycle allocates nothing.
    /// </returns>
    public static MultiTurnLifecycleServices ForAgent(
        MultiTurnLifecycleServices? services,
        string agentKind,
        string? modelId = null) =>
        services == null || ReferenceEquals(services, Disabled)
            ? Disabled
            : services with { AgentKind = agentKind, ModelId = services.ModelId ?? modelId };
}
