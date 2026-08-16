using System.Reflection;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     Regression coverage for <c>Program.FindEnvFile()</c>'s explicit-override contract.
///
///     Root problem (published-artifact env discovery): <c>FindEnvFile</c> walks up from
///     <c>AppContext.BaseDirectory</c> looking for a <c>.env</c>/<c>.env.test</c> file, stopping at the
///     first ancestor that has a <c>.sln</c> or <c>.git</c>. A published, standalone artifact (e.g. one
///     produced by <c>publish-launch.ps1</c> under
///     <c>.claude/scratchpad/lmstreaming-standalone-publish/run-*/</c>) is NOT an ancestor/descendant of
///     <c>samples/LmStreaming.Sample/</c>, so that walk hits the repository root's <c>.git</c> and stops
///     WITHOUT ever finding the real <c>samples/LmStreaming.Sample/.env</c> -- even though the
///     launcher's own docs describe <c>.env</c> as loaded.
///
///     Fix under test: an explicit environment variable (<c>LMSTREAMING_ENV_FILE</c>) that names the
///     `.env` file to load directly, set ONLY on the launched child process (never copied into the
///     retained publish artifact) and honored BEFORE the legacy walk-up so a published exe can be told
///     exactly where its `.env` lives without ever needing to search for it.
///
///     These tests call the real, unmodified <c>Program.FindEnvFile()</c> via reflection (same pattern
///     as <see cref="ProgramCodexOptionsTests"/>) so they exercise production code, not a copy.
/// </summary>
public class ProgramEnvFileResolutionTests
{
    private const string OverrideVariable = "LMSTREAMING_ENV_FILE";

    [Fact]
    public void FindEnvFile_PrefersExplicitOverride_WhenTheNamedFileExists()
    {
        // A brand-new, GUID-named temp file cannot possibly be found by the legacy walk-up search
        // (which only ever looks for files literally named ".env" or ".env.test"), so if FindEnvFile
        // returns this exact path, that can only be because it honored the explicit override.
        var tempFile = Path.Combine(Path.GetTempPath(), $"lmstreaming-env-override-{Guid.NewGuid():N}.env");
        File.WriteAllText(tempFile, "MARKER_ONLY_NO_SECRETS=1\n");

        var previous = Environment.GetEnvironmentVariable(OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(OverrideVariable, tempFile);

            InvokeFindEnvFile().Should().Be(tempFile,
                "an explicit LMSTREAMING_ENV_FILE pointing at a real file must win over the ancestor walk-up");
        }
        finally
        {
            Environment.SetEnvironmentVariable(OverrideVariable, previous);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void FindEnvFile_FallsBackToWalkUp_WhenOverridePathDoesNotExist()
    {
        // A misconfigured/stale override (file no longer exists) must not be trusted blindly -- it
        // must be ignored in favor of the same walk-up fallback used when no override is set at all.
        var missingPath = Path.Combine(Path.GetTempPath(), $"lmstreaming-env-missing-{Guid.NewGuid():N}.env");
        File.Exists(missingPath).Should().BeFalse("the path must be guaranteed absent for this test to be meaningful");

        var previous = Environment.GetEnvironmentVariable(OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(OverrideVariable, missingPath);
            var withMissingOverride = InvokeFindEnvFile();

            Environment.SetEnvironmentVariable(OverrideVariable, null);
            var withNoOverride = InvokeFindEnvFile();

            withMissingOverride.Should().NotBe(missingPath, "a non-existent override path must never be returned as-is");
            withMissingOverride.Should().Be(withNoOverride, "a missing override must fall back to exactly the same result as having no override");
        }
        finally
        {
            Environment.SetEnvironmentVariable(OverrideVariable, previous);
        }
    }

    [Fact]
    public void FindEnvFile_DirectRunFallback_IsUnaffectedWhenOverrideIsNotSet()
    {
        // Baseline/regression guard: with no override present, behavior must be identical to the
        // pre-fix walk-up (this test process's AppContext.BaseDirectory has no .env/.env.test in any
        // ancestor up to the repo root, so the legacy search returns null).
        var previous = Environment.GetEnvironmentVariable(OverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(OverrideVariable, null);

            InvokeFindEnvFile().Should().BeNull(
                "with no override set, the test process's own ancestor chain has no .env/.env.test to find");
        }
        finally
        {
            Environment.SetEnvironmentVariable(OverrideVariable, previous);
        }
    }

    private static string? InvokeFindEnvFile()
    {
        var programType = typeof(LmStreaming.Sample.Controllers.DiagnosticsController).Assembly.GetType("Program");
        programType.Should().NotBeNull();
        var method = programType!.GetMethod("FindEnvFile", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();
        return (string?)method!.Invoke(null, null);
    }
}
