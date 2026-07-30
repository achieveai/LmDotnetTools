using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// The contract every <see cref="IInputAcceptanceStore"/> must keep, run against all three shipped
/// implementations. The interface exists so a host can answer "will a repeated idempotency key queue a
/// second turn?" with a store capability rather than a hope, so these tests are what that answer rests on.
/// </summary>
public sealed class InputAcceptanceStoreTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"InputAcceptanceStoreTests_{Guid.NewGuid():N}");
    private readonly List<IAsyncDisposable> _disposables = [];

    /// <summary>The shipped implementations. Each case is run against every one of them.</summary>
    public enum StoreKind
    {
        InMemory,
        File,
        Sqlite,
    }

    public static TheoryData<StoreKind> AllStores => [StoreKind.InMemory, StoreKind.File, StoreKind.Sqlite];

    /// <summary>
    /// The implementations that claim atomicity against writers in OTHER PROCESSES, which is what the
    /// interface actually promises. In-memory is excluded because it cannot make that claim — a host using
    /// it is single-process by construction.
    /// </summary>
    public static TheoryData<StoreKind> CrossProcessStores => [StoreKind.File, StoreKind.Sqlite];

    public Task InitializeAsync()
    {
        _ = Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        // SQLite keeps pooled connections on the file; clearing them is what lets the directory delete.
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A still-locked temp file must not fail an otherwise passing test run.
        }
    }

    /// <summary>
    /// Creates a store of <paramref name="kind"/>. Two calls with the same <paramref name="backingName"/>
    /// produce INDEPENDENT store objects over the SAME durable storage — the stand-in for two processes,
    /// since neither shares any in-process lock with the other.
    /// </summary>
    private IInputAcceptanceStore CreateStore(StoreKind kind, string backingName = "default")
    {
        switch (kind)
        {
            case StoreKind.InMemory:
                return new InMemoryConversationStore();
            case StoreKind.File:
                return new FileConversationStore(Path.Combine(_root, backingName));
            case StoreKind.Sqlite:
                var store = new SqliteConversationStore(Path.Combine(_root, backingName + ".db"));
                _disposables.Add(store);
                return store;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled store kind.");
        }
    }

    private static InputAcceptance Admission(
        string inputId = "idem:1:review-run-7",
        InputAcceptanceState state = InputAcceptanceState.Pending,
        bool spawningSuppressed = true,
        Guid? reservationId = null) =>
        new(
            "thread-1",
            inputId,
            new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero),
            state,
            spawningSuppressed,
            IdempotencyHonored: true,
            reservationId ?? Guid.NewGuid());

    [Theory]
    [MemberData(nameof(AllStores))]
    public async Task TryReserveAcceptanceAsync_AdmitsOnce_AndHandsEveryLaterCallerTheStoredRecord(StoreKind kind)
    {
        var store = CreateStore(kind);
        var first = Admission();

        var winner = await store.TryReserveAcceptanceAsync(first);
        var loser = await store.TryReserveAcceptanceAsync(Admission());

        winner.Should().BeNull("a null return is what tells a caller it owns the input");
        loser.Should().Be(first, "the loser must be answered from the stored fact, not from its own request");
    }

    /// <summary>
    /// The whole point of the record: it outlives the drain. The agent loop deletes the accepted-input
    /// marker as soon as it folds an input into a run, and a caller retrying after that still has to be
    /// told what the host granted rather than being allowed to queue the turn a second time.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStores))]
    public async Task Acceptance_SurvivesTheDrainThatDeletesTheAcceptedInputMarker(StoreKind kind)
    {
        var store = CreateStore(kind);
        var ledger = (IRunLedgerStore)store;
        var admission = Admission();
        _ = await store.TryReserveAcceptanceAsync(admission);
        await ledger.RecordAcceptedInputAsync(admission.ThreadId, admission.InputId, admission.AcceptedAt);

        await ledger.RemoveAcceptedInputAsync(admission.ThreadId, admission.InputId);

        (await ledger.ListAcceptedInputIdsAsync(admission.ThreadId)).Should().BeEmpty();
        (await store.GetAcceptanceAsync(admission.ThreadId, admission.InputId)).Should().Be(admission);
    }

    [Theory]
    [MemberData(nameof(AllStores))]
    public async Task TryRecordOutcomeAsync_ReplacesTheRecord_WhenTheReservationMatches(StoreKind kind)
    {
        var store = CreateStore(kind);
        var admission = Admission();
        _ = await store.TryReserveAcceptanceAsync(admission);

        var resolved = admission with
        {
            State = InputAcceptanceState.Unenforced,
            SpawningSuppressed = false,
        };
        var applied = await store.TryRecordOutcomeAsync(resolved);

        applied.Should().BeTrue();
        (await store.GetAcceptanceAsync(admission.ThreadId, admission.InputId)).Should().Be(resolved);
    }

    /// <summary>
    /// Compensation and completion are both scoped to the request that took the admission. Without the
    /// token guard, a release arriving late — after its id was retracted and re-admitted by a different
    /// send — would delete the NEW owner's record and let a third send queue a duplicate turn.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStores))]
    public async Task ForeignReservation_CanNeitherCompleteNorDeleteAnotherRequestsAdmission(StoreKind kind)
    {
        var store = CreateStore(kind);
        var owner = Admission();
        _ = await store.TryReserveAcceptanceAsync(owner);
        var intruder = owner with { ReservationId = Guid.NewGuid(), SpawningSuppressed = false };

        var completed = await store.TryRecordOutcomeAsync(intruder);
        var released = await store.TryReleaseAcceptanceAsync(
            owner.ThreadId,
            owner.InputId,
            intruder.ReservationId);

        completed.Should().BeFalse();
        released.Should().BeFalse();
        (await store.GetAcceptanceAsync(owner.ThreadId, owner.InputId))
            .Should().Be(owner, "the owner's record must be exactly as it left it");
    }

    [Theory]
    [MemberData(nameof(AllStores))]
    public async Task TryReleaseAcceptanceAsync_FreesTheIdForALaterSend_WhenTheOwnerRetractsIt(StoreKind kind)
    {
        var store = CreateStore(kind);
        var abandoned = Admission();
        _ = await store.TryReserveAcceptanceAsync(abandoned);

        var released = await store.TryReleaseAcceptanceAsync(
            abandoned.ThreadId,
            abandoned.InputId,
            abandoned.ReservationId);
        var retry = Admission();
        var retryLost = await store.TryReserveAcceptanceAsync(retry);

        released.Should().BeTrue();
        retryLost.Should().BeNull("the retry has to be able to queue the turn the abandoned send never did");
        (await store.GetAcceptanceAsync(retry.ThreadId, retry.InputId)).Should().Be(retry);
    }

    [Theory]
    [MemberData(nameof(AllStores))]
    public async Task GetAcceptanceAsync_ReturnsNull_ForAnInputThatWasNeverAdmitted(StoreKind kind)
    {
        var store = CreateStore(kind);

        (await store.GetAcceptanceAsync("thread-1", "idem:0:never-sent")).Should().BeNull();
    }

    /// <summary>
    /// The claim the capability makes, tested the only way that means anything: TWO INDEPENDENT store
    /// objects over one piece of storage, sharing no in-process lock — the same position two hosts (or two
    /// restarts overlapping) are in. A store whose atomicity comes from a private <c>SemaphoreSlim</c>
    /// admits both callers here and must therefore not implement the interface at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(CrossProcessStores))]
    public async Task TwoIndependentStoresOverOneBacking_AdmitOneInputExactlyOnce(StoreKind kind)
    {
        var backing = "shared-" + kind;
        var storeA = CreateStore(kind, backing);
        var storeB = CreateStore(kind, backing);
        var inputId = "idem:1:contested";

        // Both admissions are minted before either is attempted, so the two calls contend over the same id
        // rather than one observing the other's completed write.
        var admissionA = Admission(inputId);
        var admissionB = Admission(inputId);
        var results = await Task.WhenAll(
            Task.Run(() => storeA.TryReserveAcceptanceAsync(admissionA)),
            Task.Run(() => storeB.TryReserveAcceptanceAsync(admissionB)));

        results.Count(r => r is null).Should().Be(1, "exactly one caller may be told it owns the input");
        var stored = await storeA.GetAcceptanceAsync("thread-1", inputId);
        stored.Should().NotBeNull();
        results.Should().Contain(r => r == stored, "the loser must be handed the record the winner wrote");
        new[] { admissionA.ReservationId, admissionB.ReservationId }
            .Should().Contain(stored!.ReservationId, "the record must be the winner's, not a merge of both");
    }

    /// <summary>
    /// The record has to survive a process restart intact — including the enum, which a durable format can
    /// silently reorder if it is stored positionally.
    /// </summary>
    [Theory]
    [MemberData(nameof(CrossProcessStores))]
    public async Task AnAdmissionRoundTripsThroughAFreshStoreObject(StoreKind kind)
    {
        var backing = "restart-" + kind;
        var resolved = Admission(state: InputAcceptanceState.Unenforced, spawningSuppressed: false);
        var before = CreateStore(kind, backing);
        _ = await before.TryReserveAcceptanceAsync(resolved with { State = InputAcceptanceState.Pending });
        _ = await before.TryRecordOutcomeAsync(resolved);

        var after = CreateStore(kind, backing);

        (await after.GetAcceptanceAsync(resolved.ThreadId, resolved.InputId)).Should().Be(resolved);
    }
}
