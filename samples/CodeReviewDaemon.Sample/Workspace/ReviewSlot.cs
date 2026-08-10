using System.Security.Cryptography;
using System.Text;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// One review-checkout slot address handed out by <see cref="ReviewSlotPool"/>. The pool owns only the
/// address and lease; repository creation and validation begin after the slot is mounted through the sandbox SDK.
/// <para>
/// Slots of the same repository share one mount (<see cref="HostPath"/>) and therefore one object store:
/// <see cref="SharedStorePath"/> holds the single real clone, while <see cref="StorePath"/> and
/// <see cref="TargetPath"/> are per-slot <c>git worktree</c>s of it. See <c>RepoWorktreeLayout.md</c>.
/// </para>
/// </summary>
/// <param name="Index">Slot index within its repository. Distinct only per repo, not globally.</param>
/// <param name="HostPath">
/// The repository's mount root — the directory the gateway mounts at <c>/workspace</c>. Shared by every
/// concurrent slot of this repo, because a worktree's pointer files must reach its object store from
/// inside the one mounted directory.
/// </param>
/// <param name="StorePath">
/// Where this slot's store working tree lives: its own worktree of <see cref="SharedStorePath"/>, checked
/// out on the run's per-PR notes branch. Named <c>StorePath</c> because that is what it is to everything
/// downstream — the root the notes are written and committed under.
/// </param>
/// <param name="ScratchPath">This slot's scratch directory, wiped on every prepare.</param>
/// <param name="RepoKey">
/// The normalized identity of the repository this mount serves, so a lease can be matched to a run and the
/// pool can keep one mount per repo. Empty on the pre-worktree single-repo shape.
/// </param>
/// <param name="SharedStorePath">
/// The one real store clone for this repository, which every slot's worktrees hang off. Empty means the
/// caller is on the legacy shape where <see cref="StorePath"/> is itself a full independent clone.
/// </param>
/// <param name="TargetPath">
/// This slot's checkout of the reviewed submodule — a worktree of the copy under
/// <see cref="SharedStorePath"/>, parked at the PR head. This is the directory the review agent gets as
/// its gateway <c>HOME</c>.
/// </param>
/// <param name="SlotDirName">
/// The slot's directory name inside the mount (e.g. <c>slot-0</c>), which is what turns a host path into
/// the container path the agent's tools address: the mount is <c>/workspace</c>, so this slot is
/// <c>/workspace/{SlotDirName}</c>.
/// </param>
internal sealed record ReviewSlot(
    int Index,
    string HostPath,
    string StorePath,
    string ScratchPath,
    string RepoKey = "",
    string SharedStorePath = "",
    string TargetPath = "",
    string SlotDirName = "")
{
    /// <summary>
    /// Whether this slot uses the shared-object-store worktree layout. False for the legacy shape (and for
    /// the test fakes built with the four positional fields), where the slot owns a full independent clone.
    /// </summary>
    public bool UsesSharedStore => !string.IsNullOrEmpty(SharedStorePath);
}

internal interface IReviewSlotPool
{
    /// <summary>
    /// Whether this pool allocates <paramref name="repoKey"/> a mount of its OWN — i.e. whether the mount it
    /// leases for this repository is distinct from the one it leases for any other.
    /// <para>
    /// READ THE LIMIT BEFORE RELYING ON THIS. It is an allocation property, not a contents property. A
    /// <c>true</c> answer says no OTHER repository was given this mount; it does NOT say nothing else is
    /// checked out under it. The two diverge whenever cross-repo siblings are populated, because siblings
    /// land at <c>{mount}/store/repos/*</c> — mount-scoped, so they outlive the run that fetched them and are
    /// still there when the next run leases a slot in the same warm mount.
    /// </para>
    /// <para>
    /// That divergence is not hypothetical and this comment used to deny it. Measured on the live nova store
    /// 2026-08-08: 57 review contexts carried populated siblings, and one of them (run 144, PR 5504919,
    /// <c>2026-08-07T17:28:09Z</c>) belonged to a run classified UNTRUSTED — six minutes after a trusted run
    /// first populated that mount. Contained by luck rather than design: the run never advanced past
    /// ContextReady so it produced no review text, and the siblings were same-org anyway. See task #94.
    /// </para>
    /// <para>
    /// The confidentiality gate wants the contents property. Until sibling checkouts move per-slot — or this
    /// question is answered from the contents rather than the allocation — a caller must not read <c>true</c>
    /// as "co-located with nothing".
    /// </para>
    /// <para>
    /// Deliberately has NO default implementation. A default is a way to forget, and this is the question a
    /// shared-depot layout must answer before it can compile.
    /// </para>
    /// </summary>
    bool MountIsDedicatedTo(string repoKey);

    /// <summary>
    /// Leases a slot on <paramref name="repoKey"/>'s mount, so concurrent reviews of one repository share
    /// its object store. Implementations that predate the per-repo pool fall back to the unkeyed lease.
    /// </summary>
    Task<ReviewSlot> LeaseAsync(string repoKey, CancellationToken cancellationToken) =>
        LeaseAsync(cancellationToken);

    Task<ReviewSlot> LeaseAsync(CancellationToken cancellationToken);

    Task ReturnAsync(ReviewSlot slot, CancellationToken cancellationToken);
}

/// <summary>
/// A bounded pool of stable workspace addresses. By default it gives each reviewed repository its own mount;
/// constructed with a <c>sharedDepotKey</c> it gives every repository ONE mount keyed on that store, so a
/// single depot serves them all and its object store is cloned once rather than once per repo.
/// <para>
/// Which layout is in force is construction state, not a per-call argument: the pool answers
/// <see cref="MountIsDedicatedTo"/> about the layout it actually implements, and the trust decision stays at
/// the call site that already owns it.
/// </para>
/// <para>
/// Concurrency is capped globally (a single semaphore over <c>maxSlots</c>) while slot <i>indexes</i> are
/// allocated per MOUNT. Both halves matter: the cap is about how many reviews may run at once across the
/// daemon, whereas the index only has to be unique within the mount it names a directory in. Per-mount is the
/// load-bearing wording — under the per-repo layout it reads the same as per-repo, but under a depot two
/// repositories sharing an index would be handed the same directory.
/// </para>
/// </summary>
internal sealed class ReviewSlotPool : IReviewSlotPool
{
    private readonly string _hostRoot;
    private readonly string _scratchDirName;
    private readonly string _slotDirPrefix;
    private readonly string _sharedDepotKey;
    private readonly SemaphoreSlim _gate;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<string, RepoIndexes> _byMount = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds the pool. <c>sharedDepotKey</c> selects the layout: null or blank keeps the per-repo mount,
    /// while a value — normally the cross-repo store URL — makes ONE depot mount serve every reviewed
    /// repository. It is deliberately the STORE's key rather than a boolean, because the mount leaf is
    /// derived from it, so two daemons pointed at one store find the same directory already on disk.
    /// </summary>
    public ReviewSlotPool(
        int maxSlots,
        string? hostRoot,
        string scratchDirName,
        ILogger<ReviewSlotPool> logger,
        string slotDirPrefix = "slot-",
        string? sharedDepotKey = null)
    {
        if (maxSlots < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSlots), maxSlots, "At least one slot is required.");
        }

        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirName);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotDirPrefix);

        _hostRoot = hostRoot ?? Path.Combine(AppContext.BaseDirectory, "review-pool");
        _scratchDirName = scratchDirName;
        _slotDirPrefix = slotDirPrefix;
        _sharedDepotKey = string.IsNullOrWhiteSpace(sharedDepotKey) ? string.Empty : sharedDepotKey.Trim();
        _gate = new SemaphoreSlim(maxSlots, maxSlots);
    }

    /// <summary>
    /// The mount directory name for a repository — the leaf the gateway mounts at <c>/workspace</c>. On a
    /// depot-keyed pool this IGNORES its argument and names the store instead, which is what makes one
    /// directory serve every repository.
    /// </summary>
    public string MountDirectoryName(string repoKey) =>
        $"{_slotDirPrefix}{RepoSlug(_sharedDepotKey.Length > 0 ? _sharedDepotKey : repoKey)}";

    /// <summary>
    /// The mount leaf for a repository that must NOT share a depot — always keyed on the repo, whatever
    /// layout this pool is configured for. On a pool with no depot key this is the same name
    /// <see cref="MountDirectoryName"/> returns, which is why the per-repo layout needs no special case.
    /// </summary>
    public string DedicatedMountDirectoryName(string repoKey) =>
        $"{_slotDirPrefix}{RepoSlug(repoKey)}";

    /// <summary>
    /// Whether <paramref name="slot"/> was leased onto a mount dedicated to its own repository. DERIVED from
    /// the mount leaf the slot actually carries rather than recorded on it, for the same reason
    /// <see cref="MountIsDedicatedTo"/> is derived: a flag set at lease time can be set wrongly, whereas the
    /// directory name is what the agent will really be mounted on.
    /// </summary>
    public bool WasLeasedDedicated(ReviewSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        return !string.IsNullOrEmpty(slot.RepoKey)
            && string.Equals(
                Path.GetFileName(slot.HostPath),
                DedicatedMountDirectoryName(slot.RepoKey),
                StringComparison.Ordinal);
    }

    /// <summary>
    /// DERIVED, never declared: the mount leaf is a pure function of the repo key, so this pool gives one
    /// repository one mount exactly as long as that name actually VARIES with the key. Probing it with a
    /// second key answers the question from the code that will still be here after someone changes it,
    /// rather than from a boolean they have to remember to update.
    /// <para>
    /// That layout now exists and is selected by construction: a pool built with a <c>sharedDepotKey</c> makes
    /// <see cref="MountDirectoryName"/> ignore its argument, both probes collide, and this returns false for
    /// every repository — with nothing in this method changed to make it happen. The untrusted path then
    /// engages at the point of use rather than a run quietly gaining read access to every repo in the depot.
    /// </para>
    /// <para>
    /// The <c>true</c> answer is weaker than it looks, and the limit is stated on
    /// <see cref="IReviewSlotPool.MountIsDedicatedTo"/>: this probes ALLOCATION, and a mount allocated to one
    /// repository can still have siblings checked out under it.
    /// </para>
    /// </summary>
    public bool MountIsDedicatedTo(string repoKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoKey);

        // The probe key contains characters no repo identity carries, so it can never BE a real neighbour.
        return !string.Equals(
            MountDirectoryName(repoKey),
            MountDirectoryName(repoKey + " \0dedication-probe"),
            StringComparison.Ordinal);
    }

    /// <summary>The slot's directory name inside its repository's mount.</summary>
    public static string SlotDirectoryName(int index) => $"slot-{index}";

    /// <summary>
    /// A filesystem-safe, collision-resistant leaf for a repository identity. The readable part is only a
    /// label — the trailing hash of the FULL key is what makes it unique, because normalization alone maps
    /// genuinely different repos onto the same text (two ADO projects can each hold a <c>Nova</c>, and the
    /// truncation below would finish the job). Output is lowercase alphanumerics and '-' only, which is
    /// exactly the character set the workspace-leaf sanitizer leaves untouched, so the name a mount is
    /// created under survives the round trip through the workspace API unchanged.
    /// </summary>
    public static string RepoSlug(string repoKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoKey);

        var label = new StringBuilder(repoKey.Length);
        foreach (var c in repoKey.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                _ = label.Append(c);
            }
            else if (label.Length > 0 && label[^1] != '-')
            {
                _ = label.Append('-');
            }
        }

        // Prefer the TAIL of the key: identities read host-first ("dev.azure.com/O365Exchange/.../Nova"),
        // so the leading segments are the part every repo shares and the distinguishing name is last.
        var trimmed = label.ToString().Trim('-');
        const int MaxLabel = 40;
        if (trimmed.Length > MaxLabel)
        {
            trimmed = trimmed[^MaxLabel..].TrimStart('-');
        }

        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(repoKey.Trim().ToLowerInvariant())))[..8]
            .ToLowerInvariant();

        return trimmed.Length == 0 ? digest : $"{trimmed}-{digest}";
    }

    public Task<ReviewSlot> LeaseAsync(CancellationToken cancellationToken) =>
        LeaseAsync(string.Empty, cancellationToken);

    public Task<ReviewSlot> LeaseAsync(string repoKey, CancellationToken cancellationToken) =>
        LeaseCoreAsync(repoKey, dedicated: false, cancellationToken);

    /// <summary>
    /// Leases a slot on a mount holding <paramref name="repoKey"/> ALONE, overriding any depot layout. This
    /// is what an untrusted run gets: the depot's saving is a convenience, and a run whose diff comes from
    /// outside the trust domain does not get to buy it at the cost of being co-located with other repos.
    /// <para>
    /// A separate entry point rather than a flag on <see cref="LeaseAsync(string, CancellationToken)"/>
    /// because the caller has to have decided: the pool cannot see a run's trust and must never guess it.
    /// </para>
    /// </summary>
    public Task<ReviewSlot> LeaseDedicatedAsync(string repoKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoKey);

        return LeaseCoreAsync(repoKey, dedicated: true, cancellationToken);
    }

    private async Task<ReviewSlot> LeaseCoreAsync(
        string repoKey, bool dedicated, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var key = string.IsNullOrWhiteSpace(repoKey) ? string.Empty : repoKey;
        var index = TakeIndex(key, dedicated);
        var slot = BuildSlot(key, index, dedicated);
        try
        {
            _ = Directory.CreateDirectory(slot.HostPath);
            _ = Directory.CreateDirectory(slot.ScratchPath);
            return slot;
        }
        catch
        {
            ReleaseIndex(key, dedicated, index);
            _ = _gate.Release();
            throw;
        }
    }

    public Task ReturnAsync(ReviewSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ReleaseIndex(slot.RepoKey ?? string.Empty, WasLeasedDedicated(slot), slot.Index);
        _ = _gate.Release();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The index space a repository's slots are drawn from. Indexes name a directory INSIDE the mount, so the
    /// space has to be the mount — not the repo. The two coincide on the per-repo layout, which is why the
    /// distinction never mattered before; under a depot, keying on the repo would hand two concurrent reviews
    /// of different repositories the same <c>slot-0</c> directory in the same mount.
    /// <para>
    /// A dedicated lease draws from its repository's own space even on a depot pool, because that is the
    /// mount it is being given.
    /// </para>
    /// </summary>
    private string MountKeyFor(string repoKey, bool dedicated) =>
        repoKey.Length == 0 ? string.Empty
        : dedicated ? repoKey
        : _sharedDepotKey.Length > 0 ? _sharedDepotKey
        : repoKey;

    private int TakeIndex(string repoKey, bool dedicated)
    {
        var mountKey = MountKeyFor(repoKey, dedicated);
        lock (_stateLock)
        {
            if (!_byMount.TryGetValue(mountKey, out var state))
            {
                state = new RepoIndexes();
                _byMount[mountKey] = state;
            }

            return state.Free.Count > 0 ? state.Free.Pop() : state.Next++;
        }
    }

    private void ReleaseIndex(string repoKey, bool dedicated, int index)
    {
        var mountKey = MountKeyFor(repoKey, dedicated);
        lock (_stateLock)
        {
            if (!_byMount.TryGetValue(mountKey, out var state))
            {
                state = new RepoIndexes();
                _byMount[mountKey] = state;
            }

            state.Free.Push(index);
        }
    }

    private ReviewSlot BuildSlot(string repoKey, int index, bool dedicated)
    {
        // An empty key means the caller never adopted the per-repo pool. Keep the flat pre-worktree layout
        // for it rather than inventing a mount named after nothing: a slot with no repo has no object store
        // to share, so the shared-store fields stay empty and the slot owns a full clone as it always did.
        if (repoKey.Length == 0)
        {
            var flat = Path.Combine(_hostRoot, $"{_slotDirPrefix}{index}");
            return new ReviewSlot(
                index,
                flat,
                Path.Combine(flat, "store"),
                Path.Combine(flat, _scratchDirName),
                SlotDirName: string.Empty);
        }

        var mountLeaf = dedicated ? DedicatedMountDirectoryName(repoKey) : MountDirectoryName(repoKey);
        var mount = Path.Combine(_hostRoot, mountLeaf);
        var slotDir = SlotDirectoryName(index);
        var slotRoot = Path.Combine(mount, slotDir);
        return new ReviewSlot(
            Index: index,
            HostPath: mount,
            StorePath: Path.Combine(slotRoot, "notes"),
            ScratchPath: Path.Combine(slotRoot, _scratchDirName),
            RepoKey: repoKey,
            SharedStorePath: Path.Combine(mount, "store"),
            TargetPath: Path.Combine(slotRoot, "repo"),
            SlotDirName: slotDir);
    }

    private sealed class RepoIndexes
    {
        public Stack<int> Free { get; } = new();

        public int Next { get; set; }
    }
}
