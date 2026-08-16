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
            $". '{QuoteSingle(ScriptPath)}'; ({expression}) | ConvertTo-Json -Depth 12 -Compress";
        return Run(command, timeout);
    }

    /// <summary>
    /// Dot-sources the script and evaluates <paramref name="expression"/> for its side effects only
    /// (e.g. <c>Copy-ReplaceSet</c>, <c>Invoke-DestinationDeploy</c> when the caller only cares about
    /// the resulting filesystem state or a thrown error, not a captured return value).
    /// </summary>
    public static PwshResult InvokeForEffect(string expression, TimeSpan? timeout = null)
    {
        var command = $". '{QuoteSingle(ScriptPath)}'; {expression} | Out-Null";
        return Run(command, timeout);
    }

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
