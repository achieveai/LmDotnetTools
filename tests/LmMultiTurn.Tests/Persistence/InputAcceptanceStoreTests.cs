using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
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

        // #477: this suite's file-backed stores are exactly the exclusive-create retry loop the window is
        // about — and AReserveWhoseDirectoryVanishes_KeepsYieldingRatherThanSpinningUncancellably, further
        // down this file, deliberately pins a reserve in that loop — so the root is detached before deleting
        // rather than recursive-deleted in place. Purge's own retry replaces the fixed settle sleep this
        // kind of teardown otherwise needs: it waits for the pooled handle the clear above just released to
        // actually close, rather than guessing how long that takes.
        DetachedStoreTeardown.Purge(_root);
    }

    /// <summary>
    /// Creates a store of <paramref name="kind"/>. Two calls with the same <paramref name="backingName"/>
    /// produce INDEPENDENT store objects over the SAME durable storage — the stand-in for two processes,
    /// since neither shares any in-process lock with the other.
    /// </summary>
    private IInputAcceptanceStore CreateStore(
        StoreKind kind,
        string backingName = "default",
        TimeProvider? clock = null)
    {
        switch (kind)
        {
            case StoreKind.InMemory:
                return new InMemoryConversationStore();
            case StoreKind.File:
                return new FileConversationStore(Path.Combine(_root, backingName), clock);
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
    /// <see cref="FileMode.CreateNew"/> compiles to — arbitrates this, and the losers must then survive the
    /// gap between the winner taking the name and its content being there.
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
    /// The invariant the whole read side rests on: an admission record that can be OPENED can be READ. The
    /// store's reader treats an openable-but-empty record as a claim still settling and waits it out, and it
    /// can only do that for a bounded time before calling it a fault — so every instant in which the record
    /// is openable and empty is an instant that spends a stranger's budget. On a loaded runner that is
    /// precisely what failed: the winner created the record and then flushed its content from a thread-pool
    /// continuation, so the empty record stayed observable across a scheduling point that starvation can
    /// stretch without limit, and losers were handed <c>IOException</c> for an input that was admitted
    /// perfectly well.
    /// <para>
    /// Proven by observation rather than by timing: a dedicated OS thread spins on the acceptances directory
    /// for the whole of a contended reserve/retract sweep and opens every record it finds, exactly as the
    /// store's own reader would. An open that is REFUSED proves nothing and is ignored — that is the claim
    /// holding the file shut, which is the point. An open that SUCCEEDS must yield a complete record. The
    /// observer's own open count is asserted so a run in which it never saw anything cannot pass as clean.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AnAdmissionRecordIsNeverObservableUntilItsContentIsThere()
    {
        const string Backing = "visibility";
        const int Racers = 6;
        const int ContestedInputs = 40;
        var acceptancesDir = Path.Combine(_root, Backing, "thread-1", "acceptances");
        var stores = Enumerable.Range(0, Racers)
            .Select(_ => CreateStore(StoreKind.File, Backing))
            .ToArray();

        var observer = new RecordObserver(acceptancesDir);
        observer.Start();

        try
        {
            for (var round = 0; round < ContestedInputs; round++)
            {
                var inputId = $"idem:1:visible-{round}";
                var admissions = stores.Select(_ => Admission(inputId)).ToArray();
                var results = await Task.WhenAll(
                    stores.Select((store, i) => Task.Run(() => store.TryReserveAcceptanceAsync(admissions[i]))));

                var winner = Array.FindIndex(results, r => r is null);
                winner.Should().BeGreaterThanOrEqualTo(0, "exactly one racer owns input {0}", inputId);

                // Retracting keeps the directory to a single live record, so the observer's every pass lands
                // on the record actually being contended rather than on a growing pile of settled ones.
                (await stores[winner].TryReleaseAcceptanceAsync(
                        "thread-1",
                        inputId,
                        admissions[winner].ReservationId))
                    .Should().BeTrue();
            }
        }
        finally
        {
            observer.StopAndJoin();
        }

        observer.Opened.Should().BeGreaterThan(
            ContestedInputs,
            "an observer that never got a record open proves nothing about what is observable");
        observer.Unreadable.Should().Be(
            0,
            "a record the store lets anyone open must already carry the content that open is for");
    }

    /// <summary>
    /// Opens every admission record it can, as the store's own reader does, and counts the ones that opened
    /// but held no complete record. Refused opens are the claim holding the file shut and are not sightings.
    /// Runs on a dedicated OS thread so the thread pool the store's own contenders are queued on cannot
    /// deschedule the very observation that has to catch them mid-claim.
    /// </summary>
    private sealed class RecordObserver
    {
        private readonly string _acceptancesDir;
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;

        private int _opened;
        private int _unreadable;

        internal RecordObserver(string acceptancesDir)
        {
            _acceptancesDir = acceptancesDir;
            _thread = new Thread(Watch)
            {
                IsBackground = true,
                Name = "acceptance-record-observer",
            };
        }

        internal int Opened => Volatile.Read(ref _opened);

        internal int Unreadable => Volatile.Read(ref _unreadable);

        internal void Start() => _thread.Start();

        internal void StopAndJoin()
        {
            _stop.Cancel();
            _thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the observer thread must finish");
            _stop.Dispose();
        }

        private void Watch()
        {
            while (!_stop.IsCancellationRequested)
            {
                string[] records;
                try
                {
                    records = Directory.Exists(_acceptancesDir)
                        ? Directory.GetFiles(_acceptancesDir, "*.json")
                        : [];
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (var record in records)
                {
                    Inspect(record);
                }
            }
        }

        private void Inspect(string record)
        {
            byte[] content;
            try
            {
                using var stream = new FileStream(
                    record,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 0,
                    FileOptions.None);
                content = new byte[stream.Length];
                stream.ReadExactly(content);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Refused, delete-pending, or already gone: the record is not claiming to be readable.
                return;
            }

            _ = Interlocked.Increment(ref _opened);
            if (content.Length == 0 || !IsCompleteJson(content))
            {
                _ = Interlocked.Increment(ref _unreadable);
            }
        }

        private static bool IsCompleteJson(byte[] content)
        {
            try
            {
                using var parsed = JsonDocument.Parse(content);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
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
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateStore(StoreKind.File, Backing, clock);
        var admission = Admission();
        (await store.TryReserveAcceptanceAsync(admission)).Should().BeNull();
        var gateFile = SoleAcceptanceRecordFile(Backing) + ".mutate";
        await File.WriteAllTextAsync(gateFile, string.Empty);

        // A different store object over the same directory — the stand-in for the host that comes after.
        var later = CreateStore(StoreKind.File, Backing, clock);
        var resolved = admission with { State = InputAcceptanceState.Enforced };
        var completed = await later.TryRecordOutcomeAsync(resolved);

        completed.Should().BeTrue("a gate file nobody holds open is not a mutation in flight");
        (await later.GetAcceptanceAsync(admission.ThreadId, admission.InputId)).Should().Be(resolved);

        // And the lock still excludes: while a handle is held the mutation stands down once its budget is
        // spent, and it lands as soon as that handle closes — without the gate file itself ever having to go
        // away. The stand-down is driven on the injected clock; waiting the budget out in real time would put
        // a fixed ten-second sleep in the suite.
        using (var held = new FileStream(gateFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            var refused = await SettleUnderExpiredBudgetAsync(
                later.TryReleaseAcceptanceAsync(
                    admission.ThreadId,
                    admission.InputId,
                    admission.ReservationId),
                clock);
            refused.Should().BeFalse("the held handle is a mutation in flight");
        }

        (await later.TryReleaseAcceptanceAsync(
                admission.ThreadId,
                admission.InputId,
                admission.ReservationId))
            .Should().BeTrue("closing the handle releases the gate, leftover file and all");
    }

    /// <summary>
    /// A release/outcome call opens the mutation gate itself before it ever inspects the record, and a PRIOR
    /// holder's own <c>await using</c> disposes that same gate handle only after the mutation it guarded is
    /// already durable — so there is a real instant where the record is already gone (or already replaced)
    /// but the gate handle has not finished closing. A caller for the id's NEW owner, admitted and releasing
    /// again the moment the id freed up, can land in exactly that instant. Refusing outright there would
    /// answer a contention that no longer exists; only re-attempting the gate — the same tolerance already
    /// given to a refused reservation and an unsettled read — decides it correctly.
    /// <para>
    /// Driven directly rather than through the wide stress sweep: a handle is held open on the gate file the
    /// whole time <see cref="IInputAcceptanceStore.TryReleaseAcceptanceAsync"/> is in flight for the TRUE
    /// current owner's own reservation, and is only closed after the call has had time to attempt the gate at
    /// least once and start waiting. A store with no retry sees the held handle exactly once and is refused
    /// for good; this fails as soon as the handle closes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AReleaseThatMeetsTheGateStillClosingBehindThePriorHolder_RetriesRatherThanFailingOutright()
    {
        const string Backing = "gate-still-closing";
        var store = CreateStore(StoreKind.File, Backing);
        var admission = Admission();
        (await store.TryReserveAcceptanceAsync(admission)).Should().BeNull();
        var gateFile = SoleAcceptanceRecordFile(Backing) + ".mutate";

        // Stands in for the split second between a prior holder's mutation already landing and its own
        // `await using` finishing the close — the caller here is releasing ITS OWN valid reservation, and
        // has every right to succeed once the gate frees up rather than being told it lost outright.
        var held = new FileStream(gateFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var release = store.TryReleaseAcceptanceAsync(
            admission.ThreadId,
            admission.InputId,
            admission.ReservationId);

        await Task.Delay(50);
        await held.DisposeAsync();

        (await release).Should().BeTrue(
            "a gate contended for only an instant must be retried, not answered as a permanent refusal");
        (await store.GetAcceptanceAsync(admission.ThreadId, admission.InputId)).Should().BeNull();
    }

    /// <summary>
    /// A record left empty or half-written by a host that died mid-claim is still an ADMITTED input: the
    /// name was taken by an exclusive create that some caller won. Answering "never admitted" there would let
    /// the host queue a second turn for an input another caller was already granted, so once the settle
    /// budget is spent on a record that never becomes readable, the store faults rather than guesses. That is
    /// the one behaviour a widened budget must not quietly turn into a hang, so it is asserted directly.
    /// <para>
    /// Driven on an injected clock rather than by waiting the budget out: the budget is deliberately far
    /// wider than any plausible stall (its whole job is to not be a starvation-class discriminator), and a
    /// test that spent it in real time would be trading a flake for a ten-second sleep. The pump advances the
    /// clock until the call settles, with a cap that fails loudly rather than looping forever.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{\"threadId\":\"thread-1\",\"inputId\":\"idem:1:rev")]
    public async Task AnUnsettledRecord_IsNeverReportedAsAnInputThatWasNeverAdmitted(string onDisk)
    {
        var backing = "unsettled-" + onDisk.Length;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateStore(StoreKind.File, backing, clock);
        var admission = Admission();
        _ = await store.TryReserveAcceptanceAsync(admission);
        await File.WriteAllTextAsync(SoleAcceptanceRecordFile(backing), onDisk);

        var read = ExpireSettleBudgetAsync(
            store.GetAcceptanceAsync(admission.ThreadId, admission.InputId),
            clock);
        var reserve = ExpireSettleBudgetAsync(store.TryReserveAcceptanceAsync(Admission()), clock);

        _ = await read.Should().ThrowAsync<IOException>(
            "an in-progress claim is an admitted input, not an absent one");
        _ = await reserve.Should().ThrowAsync<IOException>(
            "the caller must not be handed ownership of an input someone else is claiming");
    }

    /// <summary>
    /// Advances <paramref name="clock"/> until <paramref name="inFlight"/> settles, so a test can prove what
    /// the store does once its settle budget is genuinely spent without spending it in real time. The
    /// iteration cap is a fail-loud bound, not a timeout: a call that will not settle under an
    /// arbitrarily-advanced clock is a hang, and must be reported as one rather than passing quietly.
    /// </summary>
    private static async Task<T> SettleUnderExpiredBudgetAsync<T>(Task<T> inFlight, FakeTimeProvider clock)
    {
        // Sized so that only the FAKE clock can satisfy it: 60 s of injected time is six times the budget,
        // while the real time the pump itself spends stays well under one budget's worth. A store that
        // ignored the injected clock and measured the budget on the wall could not settle inside this, so
        // the cap is what makes the pump a proof that the seam is wired rather than a slow way to wait.
        const int MaxAdvances = 60;
        for (var i = 0; i < MaxAdvances && !inFlight.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));

            // Real yield: the store's next poll resumes on the thread pool, and a delay that has not been
            // registered yet cannot be advanced past.
            await Task.Delay(1);
        }

        inFlight.IsCompleted.Should().BeTrue(
            "the call must settle once its budget is spent, not wait on a clock that no longer moves");
        return await inFlight;
    }

    /// <summary>The same pump, shaped for an assertion that the settled call THREW.</summary>
    private static Func<Task<T>> ExpireSettleBudgetAsync<T>(Task<T> inFlight, FakeTimeProvider clock) =>
        () => SettleUnderExpiredBudgetAsync(inFlight, clock);

    /// <summary>
    /// A reserve whose thread directory is deleted out from under it must keep yielding, not spin. The retry
    /// arm reached when the create is REFUSED and the record turns out not to be there is the arm that
    /// handles an ordinary collision-then-retraction, and it re-attempts with nothing to wait on — but
    /// <see cref="DirectoryNotFoundException"/> derives from <see cref="IOException"/>, so it is also the arm
    /// a vanished directory lands in, on every attempt, with the read answering "nothing here" synchronously
    /// and the directory never being recreated (it is made once, before the loop). Without a yield that is a
    /// tight synchronous loop holding a core for the entire settle budget and never looking at its
    /// cancellation token — a starvation source inside the very code path that exists to remove one.
    /// <para>
    /// Cancellation is the observable that separates the two: a loop that yields sees the token at its next
    /// delay and stops; a loop that spins cannot see it at all and runs until the budget throws. The store is
    /// held in that loop first by a record name it can never win — the arm above this one, which already
    /// yields — and the tree is then taken away to flip it into the arm under test, so the interleave is
    /// arranged rather than raced.
    /// </para>
    /// <para>
    /// The arrangement is Windows-shaped, which is where the .NET suite runs: an exclusive create against a
    /// name held by a directory is refused there as an access failure. Elsewhere it can be refused as a
    /// collision instead, which parks the call in the reader's own yield — still cancelled promptly, so the
    /// test stands, but pinning the spin is a Windows run.
    /// </para>
    /// <para>
    /// The tree is taken away by an atomic RENAME, and #477 is why. A recursive delete removes the blocking
    /// directory BEFORE the parent that holds it, and in that gap the record name is free under a parent that
    /// still exists — so the exclusive create legally wins, the call returns a successful admission, and this
    /// test's own teardown is what made cancellation stop being the only way the call could settle. The
    /// assertion below over-promised rather than the loop misbehaving: widening that gap by 30 ms reproduces
    /// the reported "no exception was thrown" on every single run. A rename has no such gap — the record's
    /// parent chain is either whole or gone — so the call can only ever settle by seeing the token.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AReserveWhoseDirectoryVanishes_KeepsYieldingRatherThanSpinningUncancellably()
    {
        const string Backing = "vanishing-directory";
        var store = CreateStore(StoreKind.File, Backing);
        var admission = Admission();

        // Take and give back the id purely to learn the record's name, then put a DIRECTORY there: the
        // exclusive create can now never succeed, so the call is pinned in the retry loop and cannot settle
        // on its own before the tree is pulled out from under it.
        (await store.TryReserveAcceptanceAsync(admission)).Should().BeNull();
        var recordFile = SoleAcceptanceRecordFile(Backing);
        (await store.TryReleaseAcceptanceAsync(
                admission.ThreadId,
                admission.InputId,
                admission.ReservationId))
            .Should().BeTrue();
        _ = Directory.CreateDirectory(recordFile);

        using var cancel = new CancellationTokenSource();
        var reserve = store.TryReserveAcceptanceAsync(Admission(), cancel.Token);

        await Task.Delay(100);
        reserve.IsCompleted.Should().BeFalse("the call must still be retrying, or nothing is being tested");

        // One rename, never a recursive delete: see the remarks above. The blocked call holds no handle on
        // anything under the thread directory — its create is refused outright and it never opens the record
        // for reading in this arm — so the rename cannot be contended by the very call it is arranging for.
        Directory.Move(
            Path.Combine(_root, Backing, "thread-1"),
            Path.Combine(_root, Backing + "-detached"));
        await Task.Delay(100);

        cancel.Cancel();
        var settle = async () => await reserve;

        _ = await settle.Should().ThrowAsync<OperationCanceledException>(
            "a retry loop with nothing to wait on must still yield, and a yield is what sees the token");
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
