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

    /// <summary>Idempotent <c>mkdir -p</c>.</summary>
    public void MkdirP(string dir) => _dirs.Add(dir);

    /// <summary>Atomic election: creates <paramref name="dir"/> and returns <c>true</c> only if it did not already exist.</summary>
    public bool Mkdir(string dir) => _dirs.Add(dir);

    public bool DirExists(string dir) => _dirs.Contains(dir);

    public bool FileExists(string path) => _files.ContainsKey(path);

    public string? Read(string path) => _files.TryGetValue(path, out var value) ? value : null;

    public void Write(string path, string content) => _files[path] = content;

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
/// claim/run/takeover algorithm over a <see cref="ClaimFs"/>. Each meaningful boundary is a
/// <c>yield return</c>, so a test can pause one caller mid-establishment and interleave another. The two
/// boolean knobs select the pre-fix vs shipped behavior for the two properties F2 changes: whether the
/// lease is written first, and whether takeover requires an established lease.
/// </summary>
internal sealed class ClaimModel
{
    public const string Root = "root";
    public const string Op = "root/op";
    public const string Lease = "root/op/lease";
    public const string Created = "root/op/created";
    public const string DigestFile = "root/op/digest";
    public const string Manifest = "root/op/manifest.json";

    /// <summary>Boundary: the caller has just won the <c>mkdir</c> claim but has NOT written its lease yet.</summary>
    public const string WonClaimBeforeLease = "won-claim-before-lease";

    private const long Exec = 120;
    private const long Grace = 300;

    private readonly bool _leaseFirst;
    private readonly bool _leaseGuardedTakeover;

    public ClaimModel(bool leaseFirst, bool leaseGuardedTakeover)
    {
        _leaseFirst = leaseFirst;
        _leaseGuardedTakeover = leaseGuardedTakeover;
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

        if (TryTakeover()) // if [ -f "$OP/lease" ] ... rm -rf + mkdir; then claim_run; exit 0
        {
            foreach (var step in ClaimRun(caller))
            {
                yield return step;
            }

            yield break;
        }

        Fs.Emit(caller, Fs.FileExists(Manifest) ? "MANIFEST" : "PENDING"); // else PENDING
    }

    /// <summary>Mirrors <c>claim_run()</c>: establish lease/created/digest, run the command, commit the manifest.</summary>
    private IEnumerable<string> ClaimRun(string caller)
    {
        var created = Fs.Now;
        var lease = created + Exec + Grace;
        if (_leaseFirst)
        {
            Fs.Write(Lease, lease.ToString());
        }

        Fs.Write(Created, created.ToString());
        if (!_leaseFirst)
        {
            Fs.Write(Lease, lease.ToString());
        }

        Fs.Write(DigestFile, "digest");
        yield return "lease-established";
        Fs.RunCommand(caller);
        Fs.Write(Manifest, "manifest");
        Fs.Emit(caller, "MANIFEST");
    }

    /// <summary>Mirrors the takeover block; the shipped variant refuses to take over a claim with no established lease.</summary>
    private bool TryTakeover()
    {
        if (_leaseGuardedTakeover)
        {
            if (!Fs.FileExists(Lease))
            {
                return false;
            }

            var stale = ParseOrZero(Fs.Read(Lease));
            return stale > 0 && Fs.Now > stale && Fs.RmRf(Op) && Fs.Mkdir(Op);
        }

        // Pre-fix: `STALE=$(cat "$OP/lease" 2>/dev/null || echo 0)` — a missing lease reads as 0, i.e.
        // always "expired", which is exactly the hole.
        var staleOld = ParseOrZero(Fs.Read(Lease) ?? "0");
        return Fs.Now > staleOld && Fs.RmRf(Op) && Fs.Mkdir(Op);
    }

    private static long ParseOrZero(string? value) => long.TryParse(value, out var parsed) ? parsed : 0;
}
