using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// A minimal in-memory POSIX-ish filesystem for the claim state-machine model: atomic <c>mkdir</c>
/// election, file read/write, and recursive removal, plus a virtual clock and a record of the command's
/// side effects and each caller's emitted signal. It has just enough behavior to reproduce the RUN
/// wrapper's establish-time race deterministically.
/// </summary>
internal sealed class ClaimFs
{
    private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _emitted = new(StringComparer.Ordinal);

    /// <summary>Virtual wall clock (unix seconds) the model reads for lease math and expiry checks.</summary>
    public long Now { get; set; }

    /// <summary>Ordered list of callers whose command actually ran — one entry per side effect.</summary>
    public List<string> SideEffects { get; } = [];

    /// <summary>Ordered list of callers whose purge/self-recovery actually deleted the operation directory — one entry per delete of an existing directory.</summary>
    public List<string> Deletions { get; } = [];

    /// <summary>Models a purge/self-recovery delete: <c>rm -rf</c> the directory and record the caller, but only when the directory actually existed.</summary>
    public void DeleteOp(string caller, string dir)
    {
        if (!_dirs.Contains(dir))
        {
            return;
        }

        RmRf(dir);
        Deletions.Add(caller);
    }

    /// <summary>Idempotent <c>mkdir -p</c>.</summary>
    public void MkdirP(string dir) => _dirs.Add(dir);

    /// <summary>Atomic election: creates <paramref name="dir"/> and returns <c>true</c> only if it did not already exist.</summary>
    public bool Mkdir(string dir) => _dirs.Add(dir);

    public bool DirExists(string dir) => _dirs.Contains(dir);

    public bool FileExists(string path) => _files.ContainsKey(path);

    public string? Read(string path) => _files.TryGetValue(path, out var value) ? value : null;

    public void Write(string path, string content) => _files[path] = content;

    /// <summary>Deletes a single file (the reclaim's <c>rm -f</c> of one captured stream), if present.</summary>
    public void Remove(string path) => _files.Remove(path);

    /// <summary>Recursive delete of a directory and everything under it (<c>rm -rf</c>).</summary>
    public bool RmRf(string dir)
    {
        _dirs.Remove(dir);
        var prefix = dir + "/";
        foreach (var file in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _files.Remove(file);
        }

        foreach (var nested in _dirs.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _dirs.Remove(nested);
        }

        return true;
    }

    public void RunCommand(string caller) => SideEffects.Add(caller);

    public void Emit(string caller, string kind) => _emitted[caller] = kind;

    public string? EmittedBy(string caller) => _emitted.TryGetValue(caller, out var value) ? value : null;
}

/// <summary>
/// A step-resumable model of <see cref="AchieveAi.LmDotnetTools.Sandbox.Command.CommandScripts.BuildRun"/>'s
/// claim/run algorithm over a <see cref="ClaimFs"/>. Each meaningful boundary is a <c>yield return</c>,
/// so a test can pause one caller mid-flight and interleave another. The single boolean knob selects the
/// pre-fix expired-lease takeover (retained only to prove the double-run it caused) versus the shipped
/// behavior, which never deletes or takes over an existing claim at all.
/// </summary>
internal sealed class ClaimModel
{
    public const string Root = "root";
    public const string Op = "root/op";
    public const string Lease = "root/op/lease";
    public const string Created = "root/op/created";
    public const string DigestFile = "root/op/digest";
    public const string GenerationFile = "root/op/gen";
    public const string StdoutFile = "root/op/stdout";
    public const string StderrFile = "root/op/stderr";
    public const string Manifest = "root/op/manifest.json";

    /// <summary>The sibling per-operation GC lock (<c>&lt;op&gt;.gc</c>) and the owner-token file (<c>"&lt;token&gt; &lt;ts&gt;"</c>) that fences it.</summary>
    public const string GcLock = "root/op.gc";
    public const string GcLockOwner = "root/op.gc/owner";

    /// <summary>Boundary: the caller has just won the <c>mkdir</c> claim but has NOT written its lease yet.</summary>
    public const string WonClaimBeforeLease = "won-claim-before-lease";

    /// <summary>Boundary (pre-fix only): the caller decided to take over an expired lease but has NOT yet run its non-atomic <c>rm -rf</c>+<c>mkdir</c>.</summary>
    public const string DecidedTakeoverBeforeRemove = "decided-takeover-before-remove";

    /// <summary>Boundary: a self-recovery has deleted an abandoned expired claim under the GC lock and released it, but has NOT yet re-elected the new claimant.</summary>
    public const string RecoveredBeforeReElect = "recovered-before-reelect";

    /// <summary>Boundary: a purger/self-recovery has won the per-operation GC lock but has NOT yet re-validated or deleted.</summary>
    public const string WonGcLock = "won-gclock";

    /// <summary>Boundary: a purger lost the GC-lock election (a live holder owns it) and will NOT delete.</summary>
    public const string LostGcLock = "lost-gclock";

    /// <summary>Boundary (pre-fix only): an unlocked purger re-validated the claim as stale but has NOT yet deleted — the serialization hole the GC lock closes.</summary>
    public const string DecidedPurgeWithoutLock = "decided-purge-without-lock";

    private const long Exec = 120;
    private const long Grace = 300;
    private const long GcLockTtl = CommandArtifactLayout.GcLockStaleSeconds;
    private const long StaleAge = CommandArtifactLayout.StaleAgeSeconds;

    private readonly bool _takeoverEnabled;
    private int _generationCounter;

    public ClaimModel(bool takeoverEnabled)
    {
        _takeoverEnabled = takeoverEnabled;
    }

    public ClaimFs Fs { get; } = new();

    /// <summary>Mirrors the main body of the RUN wrapper for one caller.</summary>
    public IEnumerable<string> Run(string caller)
    {
        Fs.MkdirP(Root); // mkdir -p "$ROOT"
        if (Fs.FileExists(Manifest)) // if [ -f "$MAN" ]; then emit_manifest; exit 0
        {
            Fs.Emit(caller, "MANIFEST");
            yield break;
        }

        // F3: claim creation respects a live GC lock — never race an in-progress purge's delete. A stale
        // lock is reclaimed through the ownership protocol (gclock_try then a token-checked release),
        // never a blind rm that could delete a successor's freshly-elected lock.
        if (Fs.DirExists(GcLock))
        {
            if (GcLockIsLive())
            {
                Fs.Emit(caller, Fs.FileExists(Manifest) ? "MANIFEST" : "PENDING");
                yield break;
            }

            if (GcLockTry(caller))
            {
                GcLockRelease(caller);
            }
        }

        if (Fs.Mkdir(Op)) // if mkdir "$OP"; then claim_run; exit 0
        {
            yield return WonClaimBeforeLease;
            foreach (var step in ClaimRun(caller))
            {
                yield return step;
            }

            yield break;
        }

        if (Fs.FileExists(Manifest)) // if [ -f "$MAN" ]; then emit_manifest; exit 0
        {
            Fs.Emit(caller, "MANIFEST");
            yield break;
        }

        // Pre-fix ONLY: the expired-lease takeover — a non-atomic rm -rf + mkdir that can double-run.
        if (_takeoverEnabled && Fs.FileExists(Lease) && IsExpired())
        {
            yield return DecidedTakeoverBeforeRemove;
            Fs.RmRf(Op);
            if (Fs.Mkdir(Op))
            {
                foreach (var step in ClaimRun(caller))
                {
                    yield return step;
                }

                yield break;
            }
        }

        // Shipped (F2): self-recover an abandoned claim — an ESTABLISHED, EXPIRED lease with no manifest.
        // The delete + re-election happen under the per-operation GC lock, re-validating the CURRENT
        // state under the lock, so exactly one new claimant runs and a still-active/replaced claim is
        // never destroyed. A lease-less or unexpired (active) claim is never recovered.
        if (!_takeoverEnabled && Fs.FileExists(Lease) && IsExpired() && GcLockTry(caller))
        {
            yield return WonGcLock;
            if (!Fs.FileExists(Manifest) && Fs.FileExists(Lease) && IsExpired() && GcLockOwned(caller))
            {
                Fs.DeleteOp(caller, Op);
            }

            GcLockRelease(caller);
            yield return RecoveredBeforeReElect;
            if (Fs.Mkdir(Op)) // re-elect exactly one new claimant via the atomic mkdir
            {
                yield return WonClaimBeforeLease;
                foreach (var step in ClaimRun(caller))
                {
                    yield return step;
                }

                yield break;
            }
        }

        Fs.Emit(caller, Fs.FileExists(Manifest) ? "MANIFEST" : "PENDING"); // else PENDING
    }

    /// <summary>
    /// Mirrors <see cref="AchieveAi.LmDotnetTools.Sandbox.Command.CommandScripts.BuildGcPurge"/>: a
    /// stale-sweep purger. When <paramref name="lockGuarded"/> (the shipped behavior) the delete happens
    /// ONLY under the per-operation GC lock and ONLY after re-validating, under that lock, that the claim
    /// is still expired AND strictly past the 24h retention window — so a purger that loses the election
    /// never deletes, and a replacement active claim created after the winner released is never deleted.
    /// The unlocked variant is retained to reproduce the double-delete hole the lock closes.
    /// </summary>
    public IEnumerable<string> Purge(string caller, bool lockGuarded)
    {
        if (lockGuarded)
        {
            if (!GcLockTry(caller))
            {
                yield return LostGcLock; // a live holder owns the lock: never delete
                yield break;
            }

            yield return WonGcLock;
            if (RevalidateStaleForSweep() && GcLockOwned(caller))
            {
                Fs.DeleteOp(caller, Op);
            }

            GcLockRelease(caller);
            yield break;
        }

        // Pre-fix: no lock. Re-validate then delete with a gap in between, so a second unlocked purger
        // can pass re-validation on the OLD claim and later delete a replacement created in the gap.
        if (RevalidateStaleForSweep())
        {
            yield return DecidedPurgeWithoutLock;
            Fs.DeleteOp(caller, Op);
        }
    }

    /// <summary>The command digest the model's <c>claim_run</c> persists; a reclaim must present this exact value to match.</summary>
    public const string Digest = "digest";

    /// <summary>Mirrors <c>gclock_owned</c>: true only while the lock's owner token still equals <paramref name="token"/>.</summary>
    public bool GcLockOwned(string token)
    {
        var owner = Fs.Read(GcLockOwner);
        return owner is not null && OwnerToken(owner) == token;
    }

    /// <summary>
    /// Mirrors <c>gclock_is_live</c>: a lock whose owner token is not yet present is treated LIVE (the
    /// <c>mkdir</c>→token establishment gap — favour safety over liveness); a lock WITH a token is fresh
    /// only while stamped within the bounded TTL.
    /// </summary>
    public bool GcLockIsLive()
    {
        if (!Fs.DirExists(GcLock))
        {
            return false;
        }

        var owner = Fs.Read(GcLockOwner);
        if (owner is null)
        {
            return true;
        }

        var stamp = OwnerTs(owner);
        return stamp > 0 && Fs.Now - stamp <= GcLockTtl;
    }

    /// <summary>Mirrors <c>gclock_try</c>: win the atomic mkdir (writing the owner token), or reclaim a stale lock and re-elect a single owner. Returns whether THIS token now owns the lock.</summary>
    public bool GcLockTry(string token)
    {
        if (Fs.Mkdir(GcLock))
        {
            GcLockWriteOwner(token);
            return GcLockOwned(token);
        }

        if (GcLockIsLive())
        {
            return false;
        }

        Fs.RmRf(GcLock);
        if (Fs.Mkdir(GcLock))
        {
            GcLockWriteOwner(token);
            return GcLockOwned(token);
        }

        return false;
    }

    /// <summary>Mirrors <c>gclock_release</c>: remove the lock ONLY while still owned, so a delayed old owner never deletes a successor's lock.</summary>
    public void GcLockRelease(string token)
    {
        if (GcLockOwned(token))
        {
            Fs.RmRf(GcLock);
        }
    }

    /// <summary>
    /// Mirrors <see cref="AchieveAi.LmDotnetTools.Sandbox.Command.CommandScripts.BuildReclaim"/>: under the
    /// GC lock, drop the large streams ONLY when the directory's CURRENT generation and digest still match
    /// the ones the caller verified AND we still own the lock. A delayed reclaim from an expired old
    /// execution (<paramref name="expectedGeneration"/> stale) therefore never touches a newer re-execution.
    /// </summary>
    public void Reclaim(string caller, string expectedGeneration, string expectedDigest)
    {
        if (!GcLockTry(caller))
        {
            return;
        }

        if (
            Fs.FileExists(Manifest)
            && Fs.Read(GenerationFile) == expectedGeneration
            && Fs.Read(DigestFile) == expectedDigest
            && GcLockOwned(caller)
        )
        {
            Fs.Remove(StdoutFile);
            Fs.Remove(StderrFile);
        }

        GcLockRelease(caller);
    }

    private void GcLockWriteOwner(string token) => Fs.Write(GcLockOwner, $"{token} {Fs.Now}");

    private static string OwnerToken(string owner) => owner.Split(' ')[0];

    private static long OwnerTs(string owner)
    {
        var parts = owner.Split(' ');
        return parts.Length > 1 && long.TryParse(parts[1], out var ts) ? ts : 0;
    }

    /// <summary>The stale-sweep re-validation: expired lease AND strictly past the 24h retention window, read at delete time.</summary>
    private bool RevalidateStaleForSweep()
    {
        var lease = ParseOrZero(Fs.Read(Lease));
        var created = ParseOrZero(Fs.Read(Created));
        return lease > 0 && created > 0 && Fs.Now > lease && Fs.Now - created > StaleAge;
    }

    /// <summary>Mirrors <c>claim_run()</c>: establish lease (first) / created / digest / generation, write the captured streams, run the command, commit the manifest.</summary>
    private IEnumerable<string> ClaimRun(string caller)
    {
        var created = Fs.Now;
        var lease = created + Exec + Grace;
        Fs.Write(Lease, lease.ToString());
        Fs.Write(Created, created.ToString());
        Fs.Write(DigestFile, Digest);
        Fs.Write(GenerationFile, NextGeneration());
        Fs.Write(StdoutFile, "out");
        Fs.Write(StderrFile, "err");
        yield return "lease-established";
        Fs.RunCommand(caller);
        Fs.Write(Manifest, "manifest");
        Fs.Emit(caller, "MANIFEST");
    }

    /// <summary>A fresh per-execution generation id — distinct for every claim_run, exactly like the wrapper's <c>lmsbx_uid</c>.</summary>
    private string NextGeneration() => $"gen-{++_generationCounter}";

    private bool IsExpired()
    {
        var stale = ParseOrZero(Fs.Read(Lease));
        return stale > 0 && Fs.Now > stale;
    }

    private static long ParseOrZero(string? value) => long.TryParse(value, out var parsed) ? parsed : 0;
}
