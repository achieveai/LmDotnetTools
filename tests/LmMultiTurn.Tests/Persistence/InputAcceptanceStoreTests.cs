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
    /// The file-backed store under real contention: many INDEPENDENT store objects, sharing no in-process
    /// lock, racing for each of a series of ids. A pair of calls can step over a check-then-act window of a
    /// few microseconds by luck; a wide fan-out repeated across dozens of contested ids cannot.
    /// <para>
    /// Singled out for the sweep because reserving on a filesystem is where a portable-looking primitive
    /// stops being atomic. <c>File.Move(..., overwrite: false)</c> reads as create-if-absent, but on Unix it
    /// is a <c>stat</c> of the destination followed by a <c>rename</c> that silently REPLACES it, so two
    /// racers both come away owning the input. Only a genuine exclusive create — <c>O_CREAT|O_EXCL</c>, which
    /// <see cref="FileMode.CreateNew"/> compiles to — arbitrates this, and the losers must then survive
    /// reading a record whose content the winner has not finished writing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ContendedIndependentFileStores_AdmitEachInputExactlyOnce()
    {
        const int Racers = 8;
        const int ContestedInputs = 20;
        var stores = Enumerable.Range(0, Racers)
            .Select(_ => CreateStore(StoreKind.File, "contended"))
            .ToArray();

        for (var round = 0; round < ContestedInputs; round++)
        {
            var inputId = $"idem:1:contested-{round}";

            // Every admission is minted before any is attempted, so the calls contend over the same id
            // rather than one observing another's completed write.
            var admissions = stores.Select(_ => Admission(inputId)).ToArray();
            var results = await Task.WhenAll(
                stores.Select((store, i) => Task.Run(() => store.TryReserveAcceptanceAsync(admissions[i]))));

            results.Count(r => r is null)
                .Should().Be(1, "exactly one caller may be told it owns input {0}", inputId);
            var stored = await stores[0].GetAcceptanceAsync("thread-1", inputId);
            stored.Should().NotBeNull();
            results.Where(r => r is not null)
                .Should().AllSatisfy(
                    r => r.Should().Be(stored),
                    "every loser must be handed the record the winner wrote, in full");
            admissions.Select(a => a.ReservationId)
                .Should().Contain(stored!.ReservationId, "the record must be one racer's, not a merge");
        }
    }

    /// <summary>
    /// Retraction is how a request whose work never became queued gives the id back, so two calls carrying
    /// one token must not both be told they retracted it: the caller that gets <c>true</c> goes on to re-take
    /// or re-queue the input, and a second <c>true</c> means a later delete lands on the NEW owner's record
    /// and lets a third send queue a duplicate turn. A read-then-delete implementation reports both.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllStores))]
    public async Task ConcurrentRetractionsCarryingOneToken_RemoveTheAdmissionExactlyOnce(StoreKind kind)
    {
        const int Rounds = 40;
        var backing = "retract-" + kind;
        var storeA = CreateStore(kind, backing);
        var storeB = CreateStore(kind, backing);

        for (var round = 0; round < Rounds; round++)
        {
            var admission = Admission($"idem:1:retracted-{round}");
            (await storeA.TryReserveAcceptanceAsync(admission)).Should().BeNull();

            var released = await Task.WhenAll(
                Task.Run(() => storeA.TryReleaseAcceptanceAsync(
                    admission.ThreadId,
                    admission.InputId,
                    admission.ReservationId)),
                Task.Run(() => storeB.TryReleaseAcceptanceAsync(
                    admission.ThreadId,
                    admission.InputId,
                    admission.ReservationId)));

            released.Count(r => r).Should().Be(
                1,
                "only one caller can have removed the record (round {0})",
                round);
            (await storeA.GetAcceptanceAsync(admission.ThreadId, admission.InputId)).Should().BeNull();
        }
    }

    /// <summary>
    /// A reservation that loses the exclusive create has to work out what the refusal MEANT, and it cannot do
    /// that by looking at the record afterwards: the winner may retract in the same instant, so the record can
    /// be gone by the time the look happens even though the refusal was an ordinary collision. Deciding from
    /// that look — "refused, and nothing there, so this is a store fault" — fails a send whose id is free, and
    /// the caller sees a 500 for an input it could simply have been granted. Only re-attempting the create
    /// settles it.
    /// <para>
    /// The interleave is driven rather than stubbed, in the shape it occurs in: retries of one idempotency key
    /// arriving while an admission whose work never got queued is being compensated away. Whoever is granted
    /// the id gives it straight back, so refusals of that record and deletions of it overlap continuously.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AReservationRefusedWhileTheIdIsBeingRetracted_IsAdmittedRatherThanFailed()
    {
        const int Retries = 4;
        const int HandoffsPerRetry = 120;
        const string InputId = "idem:1:retracted-under-compensation";
        var stores = Enumerable.Range(0, Retries)
            .Select(_ => CreateStore(StoreKind.File, "reserve-vs-retract"))
            .ToArray();

        var contenders = stores.Select(store => Task.Run(async () =>
        {
            for (int taken = 0, attempts = 0; taken < HandoffsPerRetry; attempts++)
            {
                attempts.Should().BeLessThan(
                    HandoffsPerRetry * 500,
                    "a contender that can never take a repeatedly-freed id would hang the test");

                var admission = Admission(InputId);
                if (await store.TryReserveAcceptanceAsync(admission) is not null)
                {
                    continue;
                }

                taken++;
                (await store.TryReleaseAcceptanceAsync(
                        admission.ThreadId,
                        admission.InputId,
                        admission.ReservationId))
                    .Should().BeTrue("a contender must be able to give back the id it was granted");
            }
        })).ToArray();

        var settle = async () => await Task.WhenAll(contenders);

        _ = await settle.Should().NotThrowAsync(
            "a reservation refused for an id that is free by the time it looks must be re-attempted, not "
            + "reported as a store failure");
        (await stores[0].GetAcceptanceAsync("thread-1", InputId))
            .Should().BeNull("every admission taken in the race was given back");
    }

    /// <summary>
    /// The gate that serializes mutations of one record is an OS lock and nothing else. The file it is taken
    /// on is left in place on purpose: a host killed outright cannot delete it, and a store that read the
    /// file's EXISTENCE as "a mutation is in flight" would then refuse every later completion and retraction
    /// of that admission for good — freezing the record in whatever state the dead host left it. Ownership is
    /// the open handle, which the kernel drops when the holder exits however it exits.
    /// </summary>
    [Fact]
    public async Task AGateFileLeftBehindByADeadHost_DoesNotFreezeTheAdmissionItGuards()
    {
        const string Backing = "stale-gate";
        var store = CreateStore(StoreKind.File, Backing);
        var admission = Admission();
        (await store.TryReserveAcceptanceAsync(admission)).Should().BeNull();
        var gateFile = SoleAcceptanceRecordFile(Backing) + ".mutate";
        await File.WriteAllTextAsync(gateFile, string.Empty);

        // A different store object over the same directory — the stand-in for the host that comes after.
        var later = CreateStore(StoreKind.File, Backing);
        var resolved = admission with { State = InputAcceptanceState.Enforced };
        var completed = await later.TryRecordOutcomeAsync(resolved);

        completed.Should().BeTrue("a gate file nobody holds open is not a mutation in flight");
        (await later.GetAcceptanceAsync(admission.ThreadId, admission.InputId)).Should().Be(resolved);

        // And the lock still excludes: while a handle is held the mutation stands down, and it lands as soon
        // as that handle closes — without the gate file itself ever having to go away.
        using (var held = new FileStream(gateFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            (await later.TryReleaseAcceptanceAsync(
                    admission.ThreadId,
                    admission.InputId,
                    admission.ReservationId))
                .Should().BeFalse("the held handle is a mutation in flight");
        }

        (await later.TryReleaseAcceptanceAsync(
                admission.ThreadId,
                admission.InputId,
                admission.ReservationId))
            .Should().BeTrue("closing the handle releases the gate, leftover file and all");
    }

    /// <summary>
    /// An exclusive-creation protocol necessarily has a moment where the record EXISTS but its content has
    /// not landed yet — that is what makes the creation the arbitration point. A reader that meets that
    /// moment and answers "never admitted" would let the host queue a second turn for an input another
    /// caller has already been granted, so the unsettled record must be reported as an error instead. The
    /// same shape covers a record left half-written by a host that died mid-claim.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{\"threadId\":\"thread-1\",\"inputId\":\"idem:1:rev")]
    public async Task AnUnsettledRecord_IsNeverReportedAsAnInputThatWasNeverAdmitted(string onDisk)
    {
        var store = CreateStore(StoreKind.File, "unsettled-" + onDisk.Length);
        var admission = Admission();
        _ = await store.TryReserveAcceptanceAsync(admission);
        await File.WriteAllTextAsync(SoleAcceptanceRecordFile("unsettled-" + onDisk.Length), onDisk);

        var read = async () => await store.GetAcceptanceAsync(admission.ThreadId, admission.InputId);
        var reserve = async () => await store.TryReserveAcceptanceAsync(Admission());

        _ = await read.Should().ThrowAsync<IOException>(
            "an in-progress claim is an admitted input, not an absent one");
        _ = await reserve.Should().ThrowAsync<IOException>(
            "the caller must not be handed ownership of an input someone else is claiming");
    }

    /// <summary>The one admission record under a file-backed store, located without assuming its name.</summary>
    private string SoleAcceptanceRecordFile(string backingName) =>
        Directory.EnumerateFiles(
            Path.Combine(_root, backingName, "thread-1", "acceptances"),
            "*.json").Single();

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
