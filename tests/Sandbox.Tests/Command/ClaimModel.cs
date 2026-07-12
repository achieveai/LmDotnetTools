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
    public const string Manifest = "root/op/manifest.json";

    /// <summary>Boundary: the caller has just won the <c>mkdir</c> claim but has NOT written its lease yet.</summary>
    public const string WonClaimBeforeLease = "won-claim-before-lease";

    /// <summary>Boundary (pre-fix only): the caller decided to take over an expired lease but has NOT yet run its non-atomic <c>rm -rf</c>+<c>mkdir</c>.</summary>
    public const string DecidedTakeoverBeforeRemove = "decided-takeover-before-remove";

    private const long Exec = 120;
    private const long Grace = 300;

    private readonly bool _takeoverEnabled;

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

        // Pre-fix ONLY: the expired-lease takeover. The shipped wrapper has no takeover block — an
        // expired-but-uncommitted claim falls straight through to PENDING and is left for the guarded
        // stale sweep, because rm -rf + mkdir is not atomic against a concurrent contender and can
        // double-run the command.
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

        Fs.Emit(caller, Fs.FileExists(Manifest) ? "MANIFEST" : "PENDING"); // else PENDING
    }

    /// <summary>Mirrors <c>claim_run()</c>: establish lease (first) / created / digest, run the command, commit the manifest.</summary>
    private IEnumerable<string> ClaimRun(string caller)
    {
        var created = Fs.Now;
        var lease = created + Exec + Grace;
        Fs.Write(Lease, lease.ToString());
        Fs.Write(Created, created.ToString());
        Fs.Write(DigestFile, "digest");
        yield return "lease-established";
        Fs.RunCommand(caller);
        Fs.Write(Manifest, "manifest");
        Fs.Emit(caller, "MANIFEST");
    }

    private bool IsExpired()
    {
        var stale = ParseOrZero(Fs.Read(Lease));
        return stale > 0 && Fs.Now > stale;
    }

    private static long ParseOrZero(string? value) => long.TryParse(value, out var parsed) ? parsed : 0;
}
