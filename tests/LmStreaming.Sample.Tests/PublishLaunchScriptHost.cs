using System.Diagnostics;

namespace LmStreaming.Sample.Tests;

/// <summary>
/// Shells out to a real <c>pwsh</c> process to dot-source <c>publish-launch.ps1</c> and invoke its
/// internal helper/orchestration functions directly -- no Pester harness exists in this repo (see
/// <see cref="PublishLaunchScriptTests"/>'s source-text approach for the rest of the script), and no
/// npm/dotnet build ever runs as part of this: the script's dot-source guard (see
/// <c>Script_IsDotSourceableWithoutRunningInvokeMain</c>) means loading it via ". &lt;path&gt;" only
/// defines functions and never executes the build/publish/launch pipeline, so tests here call
/// destination-only helpers (e.g. <c>Test-DestinationState</c>, <c>Invoke-DestinationDeploy</c>)
/// against real, test-created fixture directories on disk.
///
/// <para>
/// Neither entry point prepends <c>$ErrorActionPreference = 'Stop'</c> to the command, on purpose.
/// It used to, which silently made every test here immune to the script's OWN
/// <c>$ErrorActionPreference = 'Stop'</c> line being deleted: a non-terminating cmdlet error would
/// still have terminated under the harness-supplied preference, so no test could ever have caught
/// the regression. The script sets it at top level, so dot-sourcing already establishes it -- the
/// harness contributing a second copy bought nothing and cost the coverage.
/// </para>
/// </summary>
internal static class PublishLaunchScriptHost
{
    public static readonly string ScriptPath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "samples",
            "LmStreaming.Sample",
            "publish-launch.ps1"));

    /// <summary>
    /// Dot-sources the script (defining every function, running no top-level pipeline) and then
    /// evaluates <paramref name="expression"/>, converting whatever it returns to compact JSON so
    /// the result can cross the process boundary. Use for functions that return a value (e.g.
    /// <c>Test-DestinationState</c>).
    /// </summary>
    public static PwshResult InvokeForJson(string expression, TimeSpan? timeout = null)
    {
        var command =
            $". '{QuoteSingle(ScriptPath)}'; {CaptureStructurally($"({expression}) | ConvertTo-Json -Depth 12 -Compress")}";
        return Run(command, timeout);
    }

    /// <summary>
    /// Dot-sources the script and evaluates <paramref name="expression"/> for its side effects only
    /// (e.g. <c>Copy-ReplaceSet</c>, <c>Invoke-DestinationDeploy</c> when the caller only cares about
    /// the resulting filesystem state or a thrown error, not a captured return value).
    /// </summary>
    public static PwshResult InvokeForEffect(string expression, TimeSpan? timeout = null)
    {
        var command = $". '{QuoteSingle(ScriptPath)}'; {CaptureStructurally($"{expression} | Out-Null")}";
        return Run(command, timeout);
    }

    /// <summary>
    /// Wraps <paramref name="body"/> so a terminating error reaches stderr as the exact message the
    /// script composed, rather than as pwsh's rendering of an ErrorRecord.
    ///
    /// <para>
    /// This is the whole of the fix for issue #340. pwsh's default ConciseView error formatter does
    /// two things to a thrown message on its way to stderr: it word-wraps at the host width -- a
    /// fixed 120 columns once stdout is redirected, whatever the parent terminal is -- and it
    /// decorates every fragment with ANSI SGR escapes. A wrap landing on a space inside an absolute
    /// path, routine on any machine whose profile directory contains one (C:\Users\Some Name\...),
    /// therefore splits that path and stuffs escape sequences into the gap, so
    /// <c>StandardError.Should().Contain(thatPath)</c> fails against perfectly correct output.
    /// Collapsing the whitespace back -- the mitigation this replaces -- cannot repair it: the
    /// escapes are injected AT the wrap point and survive the collapse.
    /// </para>
    ///
    /// <para>
    /// Writing through <see cref="Console.Error"/> bypasses the PowerShell formatter completely: no
    /// wrap, no gutter, no escapes, at any host width and for any path. Assertions then read the
    /// message the script DECIDED to compose instead of how a shell chose to render it, so they no
    /// longer depend on the profile path, the console width, or the terminal.
    /// </para>
    ///
    /// <para>
    /// Note what this deliberately does NOT do: it sets no <c>$ErrorActionPreference</c>. A
    /// try/catch changes no preference, so the script's own top-level
    /// <c>$ErrorActionPreference = 'Stop'</c> stays the only thing promoting a non-terminating error
    /// to a terminating one; deleting that line still fails the affected tests, via a zero exit
    /// code, exactly as it did before -- see this class's remarks above.
    /// </para>
    /// </summary>
    private static string CaptureStructurally(string body) =>
        $"try {{ {body} }} catch {{ [Console]::Error.WriteLine($_.Exception.Message); exit 1 }}";

    public static PwshResult Run(string command, TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Belt-and-braces for the raw Run() entry point, which has no try/catch to route a message
        // around the formatter: NO_COLOR is pwsh 7.2+'s host-level opt out of ANSI decoration, so
        // what the formatter does emit is at least plain text and legible in an xUnit failure
        // message. It does not stop the wrapping -- only CaptureStructurally does that.
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start pwsh process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(90);
        if (!process.WaitForExit((int)effectiveTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort: the process may have exited between the timeout check and Kill().
            }

            throw new TimeoutException($"pwsh command timed out after {effectiveTimeout}: {command}");
        }

        // Process.WaitForExit(int) does not guarantee redirected streams are fully drained; the
        // parameterless overload does. Calling it after the timed overload returns true blocks only
        // long enough to flush what is already an exited process's buffers.
        process.WaitForExit();

        return new PwshResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>
    /// Quotes a path for safe interpolation inside a single-quoted PowerShell string literal
    /// (doubles any embedded single quotes).
    /// </summary>
    public static string QuoteSingle(string value) => value.Replace("'", "''");
}

internal sealed record PwshResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}
