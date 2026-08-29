using System.Globalization;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Builds the argument vectors the <c>HostGitCommandRunner</c> watchdog tests need — a command that stays
/// silent, one that keeps talking, one that outlives its kill, one that leaves a grandchild holding the
/// output pipe — in a form that runs on Windows as well as on Linux.
/// <para>
/// The PR #451 branch wrote these inline as <c>sleep 30</c> and <c>sh -c '…'</c>. Neither exists on a stock
/// Windows box, and the .NET CI job for this repository is <c>windows-latest</c>, so porting them verbatim
/// would have put the entire watchdog suite in the one place it can never run. That is worse than not
/// porting it: the tests would look like coverage while proving nothing, and the daemon's own supported
/// host is Windows.
/// </para>
/// <para>
/// Each helper materialises a small script FILE rather than passing a command string. On Windows that is
/// not a style preference: <c>cmd.exe /c</c> applies its own quote-stripping rules to a single quoted
/// argument, and a one-liner containing <c>&amp;</c>, <c>&gt;</c> and nested quotes is mangled in ways that
/// are silent until the test hangs. A script file has no quoting to get wrong. Timing on Windows comes from
/// <c>ping</c> rather than <c>timeout</c>, because <c>timeout</c> refuses to run at all when stdin is not a
/// console — which is exactly how a redirected child is started.
/// </para>
/// </summary>
internal sealed class ProcessLab : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crd-proclab-" + Guid.NewGuid().ToString("N"));

    private int _next;

    public ProcessLab() => Directory.CreateDirectory(_root);

    /// <summary>A command that runs for <paramref name="duration"/> and prints NOTHING while it does.</summary>
    public IReadOnlyList<string> SilentFor(TimeSpan duration) =>
        Script(windows: [$"ping -n {Pings(duration)} 127.0.0.1 > nul"], posix: [$"sleep {Seconds(duration)}"]);

    /// <summary>
    /// A command that prints a line roughly once a second, <paramref name="ticks"/> times, so it outlives an
    /// idle timeout shorter than its total runtime while never once going quiet for that long.
    /// </summary>
    public IReadOnlyList<string> ChattyFor(int ticks) =>
        Script(
            windows: [$"for /L %%i in (1,1,{ticks}) do (echo working& ping -n 2 127.0.0.1 > nul)"],
            posix: [$"i=0; while [ $i -lt {ticks} ]; do echo working; sleep 1; i=$((i+1)); done"]
        );

    /// <summary>
    /// A command that waits <paramref name="delay"/> and only then writes <paramref name="markerPath"/>. The
    /// marker's ABSENCE afterwards is what proves the child was killed rather than merely abandoned.
    /// </summary>
    public IReadOnlyList<string> WriteMarkerAfter(TimeSpan delay, string markerPath) =>
        Script(
            windows: [$"ping -n {Pings(delay)} 127.0.0.1 > nul", $"echo alive> \"{markerPath}\""],
            posix: [$"sleep {Seconds(delay)}", $"echo alive > '{markerPath}'"]
        );

    /// <summary>A command that prints <paramref name="text"/> and exits immediately.</summary>
    public IReadOnlyList<string> Echo(string text) => Script(windows: [$"echo {text}"], posix: [$"echo {text}"]);

    /// <summary>
    /// A command that prints <paramref name="text"/>, exits at once, and leaves behind a BACKGROUND
    /// descendant that inherited the output pipe and holds it for <paramref name="lifetime"/>.
    /// <para>
    /// This is the shape that makes "the process has exited so both pipes are at EOF" false. Verified on
    /// both platforms before being relied on: the parent exits in ~40 ms and the read does not reach EOF
    /// until the grandchild goes.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> EchoLeavingAGrandchildHoldingThePipe(string text, TimeSpan lifetime) =>
        Script(
            windows: [$"start /b \"\" ping -n {Pings(lifetime)} 127.0.0.1 > nul", $"echo {text}"],
            posix: [$"sleep {Seconds(lifetime)} &", $"echo {text}"]
        );

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp script is not worth failing a test run over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: a script a killed child still has mapped can refuse removal for a moment.
        }
    }

    /// <summary>
    /// <c>ping -n K</c> sends K requests one second apart, so it blocks for about K-1 seconds. The extra
    /// count is what turns a duration into a request count; rounding up keeps the wait at least as long as
    /// the caller asked for, which is the safe direction for every test here.
    /// </summary>
    private static int Pings(TimeSpan duration) => (int)Math.Ceiling(duration.TotalSeconds) + 1;

    private static string Seconds(TimeSpan duration) =>
        duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private IReadOnlyList<string> Script(IReadOnlyList<string> windows, IReadOnlyList<string> posix)
    {
        var index = Interlocked.Increment(ref _next);

        if (OperatingSystem.IsWindows())
        {
            // `@echo off` first, or cmd echoes every line of the script to stdout — which would make the
            // "silent" command chatty and quietly invert the idle-timeout tests. CRLF because a batch file
            // is read by cmd, which is not reliably LF-tolerant.
            var path = Path.Combine(_root, $"lab{index}.cmd");
            File.WriteAllText(path, string.Join("\r\n", ["@echo off", .. windows, string.Empty]));
            return ["cmd.exe", "/c", path];
        }

        var shPath = Path.Combine(_root, $"lab{index}.sh");
        File.WriteAllText(shPath, string.Join("\n", [.. posix, string.Empty]));

        // Invoked as `sh <path>` rather than executed directly, so the script needs no execute bit.
        return ["sh", shPath];
    }
}
