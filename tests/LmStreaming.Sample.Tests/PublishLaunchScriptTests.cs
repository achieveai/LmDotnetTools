namespace LmStreaming.Sample.Tests;

/// <summary>
/// "Script parser" regression coverage for <c>publish-launch.ps1</c>. There is no PowerShell
/// test harness (e.g. Pester) in this repo, so these tests assert on the raw script SOURCE TEXT
/// rather than executing it. The script's contract today is a STANDALONE PRODUCTION publish +
/// launch pipeline (npm ci -> npm run build -> dotnet publish -p:BuildClientApp=false -> validate
/// artifact -> launch ONLY the published exe) -- there is no paired Vite dev-server mode, no
/// Start-Process-based child spawning, and no in-process Vite proxy/entry-module readiness
/// checks. Each test targets one specific, currently-true structural property of the script so a
/// future edit that reintroduces old (paired-launcher) behavior, or silently drops part of the
/// standalone contract, fails with a pointed message instead of a vague diff.
/// </summary>
public class PublishLaunchScriptTests
{
    private static readonly string ScriptPath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "samples",
            "LmStreaming.Sample",
            "publish-launch.ps1"
        )
    );

    private static string ScriptText => File.ReadAllText(ScriptPath);

    // Several regressions this file guards against (e.g. "no bare WaitForExit() remains", "no
    // CancelKeyPress handler was added") are explicitly discussed in EXPLANATORY COMMENTS right
    // next to their fix, so a blanket ScriptText.Should().NotContain(...) would be a false
    // positive the moment someone writes a comment describing the old bug. This strips every
    // full-line PowerShell comment (confirmed the script has no other trailing "# ..." comments
    // sharing a line with real code) so those checks assert on LIVE CODE only.
    private static string CodeOnlyText =>
        string.Join('\n', ScriptText.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    [Fact]
    public void Script_BuildsClientUnconditionally_NpmCiBeforeNpmRunBuild()
    {
        // The standalone contract runs npm ci then npm run build on EVERY invocation -- there is
        // deliberately no node_modules-freshness shortcut and no -SkipClientBuild opt-out (the
        // published bundle must always reflect current ClientApp sources).
        ScriptText
            .Should()
            .Contain("npm ci --prefix $ClientAppDirectory", "npm ci must run unconditionally in Invoke-ClientBuild");
        ScriptText
            .Should()
            .Contain(
                "npm run build --prefix $ClientAppDirectory",
                "npm run build must run unconditionally in Invoke-ClientBuild"
            );
        ScriptText
            .Should()
            .NotContain(
                "SkipClientBuild",
                "the unconditional standalone build must not have a skip-the-client-build escape hatch"
            );
        ScriptText
            .Should()
            .NotContain(
                "Test-ClientDependenciesFresh",
                "there is no freshness-shortcut check in the unconditional standalone pipeline"
            );

        var npmCiIndex = ScriptText.IndexOf("npm ci --prefix $ClientAppDirectory", StringComparison.Ordinal);
        var npmBuildIndex = ScriptText.IndexOf("npm run build --prefix $ClientAppDirectory", StringComparison.Ordinal);
        npmCiIndex.Should().BeGreaterThan(-1);
        npmBuildIndex.Should().BeGreaterThan(-1);
        npmCiIndex.Should().BeLessThan(npmBuildIndex, "npm ci must run before npm run build");
    }

    [Fact]
    public void Script_PublishesWithBuildClientAppFalseAndNoNoBuildFlag()
    {
        // -p:BuildClientApp=false stops MSBuild's opt-in client-build target from duplicating the
        // npm ci/npm run build this script already ran directly. Critically, there must be no
        // --no-build: `dotnet publish` must build AND produce a complete artifact by itself, not
        // rely on a stale prior build.
        var publishLineMatch = System.Text.RegularExpressions.Regex.Match(ScriptText, @"(?m)^\s*& dotnet publish.*$");
        publishLineMatch.Success.Should().BeTrue("the script must contain a live `dotnet publish` invocation line");
        var publishLine = publishLineMatch.Value;

        publishLine.Should().Contain("$ProjectFile", "publish must target the resolved project file");
        publishLine.Should().Contain("-c $Configuration", "publish must use the resolved configuration");
        publishLine.Should().Contain("-o $OutputDirectory", "publish must target the resolved output directory");
        publishLine
            .Should()
            .Contain(
                "'-p:BuildClientApp=false'",
                "publish must pass BuildClientApp=false so MSBuild does not duplicate the client build"
            );
        // "--no-build" itself still appears in an explanatory comment a few lines above ("no
        // `--no-build`: publish must build the server..."), so asserting on the EXTRACTED
        // invocation LINE (not the whole file) is what makes this non-vacuous.
        publishLine
            .Should()
            .NotContain(
                "--no-build",
                "the live invocation must not pass --no-build -- publish must build the server itself"
            );
    }

    [Fact]
    public void Script_CreatesRunDirectoryUnderRepositoryRootScratchpad()
    {
        // The publish/run directory must be rooted at the REPOSITORY ROOT (resolved via git, not
        // the caller's working directory or the script's own folder), under
        // .claude/scratchpad/lmstreaming-standalone-publish/run-<UTC timestamp>-<PID>/, so runs
        // are inspectable, unique, and retained regardless of where the script is invoked from.
        ScriptText
            .Should()
            .Contain("function New-PublishRunDirectory", "a dedicated helper must resolve the run directory");
        ScriptText
            .Should()
            .Contain(
                "Join-Path $RepositoryRoot '.claude' 'scratchpad' 'lmstreaming-standalone-publish' $runName",
                "the run directory must be rooted at the repository root's scratchpad, not the caller's cwd or the script folder"
            );
        ScriptText
            .Should()
            .MatchRegex(
                @"\$runName\s*=\s*'run-\{0\}-\{1\}'\s*-f\s*\[DateTime\]::UtcNow\.ToString\('yyyyMMdd-HHmmss'\),\s*\$PID",
                "the run directory name must be unique per invocation via a UTC timestamp + PID"
            );
        ScriptText
            .Should()
            .Contain(
                "New-PublishRunDirectory -RepositoryRoot $repositoryRoot",
                "the call site must pass the git-resolved repository root, not $ProjectDir/$PSScriptRoot"
            );
    }

    [Fact]
    public void Script_ValidatesFullArtifactAfterPublishAndBeforeLaunch()
    {
        // Validation must happen strictly between "the server was published" and "the exe was
        // started" -- launching an unvalidated artifact defeats the point of the check.
        var publishCallIndex = ScriptText.IndexOf(
            "Invoke-ApplicationPublish -ProjectFile $ProjectFile -Configuration $Configuration -OutputDirectory $publishDirectory",
            StringComparison.Ordinal
        );
        var validateCallIndex = ScriptText.IndexOf(
            "$artifact = Confirm-PublishArtifact -PublishDirectory $publishDirectory",
            StringComparison.Ordinal
        );
        var startCallIndex = ScriptText.IndexOf("$null = $backendProcess.Start()", StringComparison.Ordinal);

        publishCallIndex.Should().BeGreaterThan(-1);
        validateCallIndex.Should().BeGreaterThan(-1);
        startCallIndex.Should().BeGreaterThan(-1);

        publishCallIndex
            .Should()
            .BeLessThan(validateCallIndex, "the artifact must be published before it is validated");
        validateCallIndex
            .Should()
            .BeLessThan(startCallIndex, "the artifact must be validated before the published exe is launched");
    }

    [Fact]
    public void Script_ValidatesJsCssAssetReferencesWithWwwrootBoundaryContainment()
    {
        // Every local JS/CSS asset index.html references must resolve to a real file INSIDE
        // wwwroot -- using an exact-root-or-separator-boundary comparison, not a raw StartsWith
        // (which would let a sibling directory sharing the same string prefix, e.g.
        // "wwwroot-evil", pass validation).
        ScriptText
            .Should()
            .Contain(
                "function Get-PublishedAssetReferences",
                "a dedicated helper must extract JS/CSS asset references from index.html"
            );
        ScriptText
            .Should()
            .Contain(
                @"'(?:src|href)=[""''](?<path>/dist/[^""''?#]+\.(?:js|css))(?:[?#][^""'']*)?[""'']'",
                "the asset-reference regex must match local /dist/*.js and *.css references"
            );
        ScriptText
            .Should()
            .Contain("function Confirm-PublishArtifact", "a dedicated helper must validate the full publish artifact");
        ScriptText
            .Should()
            .Contain(
                "$wwwrootBoundary = $resolvedWwwroot + [System.IO.Path]::DirectorySeparatorChar",
                "the boundary check must use an OS-appropriate root-plus-separator comparison"
            );
        ScriptText
            .Should()
            .MatchRegex(
                @"\$resolvedAsset\.Equals\(\$resolvedWwwroot,\s*\[System\.StringComparison\]::OrdinalIgnoreCase\)\s*-or\s*\$resolvedAsset\.StartsWith\(\$wwwrootBoundary,\s*\[System\.StringComparison\]::OrdinalIgnoreCase\)",
                "containment must require an exact match OR a match at the separator boundary, closing the sibling-directory bypass"
            );
        ScriptText
            .Should()
            .Contain(
                "throw \"Published asset reference '$asset' resolves outside wwwroot",
                "an asset resolving outside wwwroot must be fatal"
            );
        ScriptText
            .Should()
            .Contain(
                "throw \"Published asset referenced by index.html was not found on disk",
                "a missing asset file must be fatal"
            );
    }

    [Fact]
    public void Script_LaunchesPublishedExecutableViaProcessStartInfo_NeverSourceOrStartProcess()
    {
        // The published exe (never the source project, and never via Start-Process, which the
        // old paired launcher used and which hid the real listener PID behind wrapper processes)
        // must be started via a direct ProcessStartInfo + Process.Start().
        ScriptText
            .Should()
            .Contain(
                "$startInfo = [System.Diagnostics.ProcessStartInfo]::new()",
                "the backend must be launched via a direct ProcessStartInfo"
            );
        ScriptText
            .Should()
            .Contain(
                "$startInfo.FileName = $artifact.ExecutablePath",
                "the FileName must be the validated published executable, not the source project"
            );
        ScriptText.Should().Contain("$backendProcess = [System.Diagnostics.Process]::new()");
        ScriptText.Should().Contain("$null = $backendProcess.Start()");
        ScriptText
            .Should()
            .NotContain(
                "Start-Process",
                "the script must not launch any child via Start-Process (the old wrapper-hop mechanism)"
            );
        ScriptText.Should().NotContain("dotnet run", "the script must never launch the backend from source");
    }

    [Fact]
    public void Script_SetsProductionEnvironmentOnlyOnChildProcess_NeverParentSession()
    {
        // Production must be scoped to the child ProcessStartInfo only -- mutating the parent
        // PowerShell session's environment (e.g. via $env:) would leak Production into anything
        // else the caller runs afterwards in the same session.
        ScriptText
            .Should()
            .Contain(
                "$startInfo.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'",
                "ASPNETCORE_ENVIRONMENT=Production must be scoped to the child process"
            );
        ScriptText
            .Should()
            .Contain(
                "$startInfo.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Production'",
                "DOTNET_ENVIRONMENT=Production must be scoped to the child process"
            );
        ScriptText
            .Should()
            .NotContain(
                "$env:ASPNETCORE_ENVIRONMENT",
                "Production must not be set on the parent PowerShell session's environment"
            );
        ScriptText
            .Should()
            .NotContain(
                "$env:DOTNET_ENVIRONMENT",
                "Production must not be set on the parent PowerShell session's environment"
            );
    }

    [Fact]
    public void Script_SetsExplicitEnvFileOverrideOnlyOnChildProcess_PointingAtProjectDirNotPublishDirectory()
    {
        // Program.FindEnvFile() walks up from AppContext.BaseDirectory (the publish output dir),
        // which is not an ancestor/descendant of $ProjectDir (samples/LmStreaming.Sample/, where
        // the real .env lives) -- the walk-up would otherwise hit the repository root's .git and
        // stop without ever finding it. LMSTREAMING_ENV_FILE is the explicit escape hatch: it must
        // be scoped to the child ProcessStartInfo only (same rule as ASPNETCORE_ENVIRONMENT above)
        // and must point at $ProjectDir's .env, never at the published/retained artifact directory.
        ScriptText
            .Should()
            .Contain(
                "$startInfo.EnvironmentVariables['LMSTREAMING_ENV_FILE'] = (Join-Path $ProjectDir '.env')",
                "the child process must be told exactly where the real .env lives, scoped to $ProjectDir"
            );
        ScriptText
            .Should()
            .NotContain(
                "$env:LMSTREAMING_ENV_FILE",
                "the env-file override must not be set on the parent PowerShell session's environment"
            );
        ScriptText
            .Should()
            .NotContain(
                "EnvironmentVariables['LMSTREAMING_ENV_FILE'] = (Join-Path $publishDirectory",
                "the override must never point at the retained publish artifact directory -- the .env is never copied there"
            );
    }

    [Fact]
    public void Script_ContainsNoViteRuntimeConstructs()
    {
        // The standalone pipeline never spawns, proxies to, or probes a Vite dev server -- these
        // are all constructs from the retired paired backend+Vite launcher and must not reappear
        // as live code (they may still be named in prose explaining that Vite is NOT involved,
        // which the substrings below deliberately do not match).
        ScriptText.Should().NotContain("$viteProcess", "there must be no Vite child process object");
        ScriptText.Should().NotContain("node_modules/vite/bin/vite.js", "Vite must never be launched directly");
        ScriptText.Should().NotContain("--strictPort", "there is no Vite dev server to bind with --strictPort");
        ScriptText
            .Should()
            .NotContain("npm run dev", "the client is built for production (npm run build), never run in dev mode");
        ScriptText
            .Should()
            .NotContain(
                "Test-ViteProxyReachesBackend",
                "there is no Vite-proxy readiness check in the standalone pipeline"
            );
        ScriptText
            .Should()
            .NotContain(
                "Test-ViteEntryModuleResolves",
                "there is no Vite entry-module readiness check in the standalone pipeline"
            );
        ScriptText
            .Should()
            .NotContain("Get-Command npm.cmd", "the script must not resolve npm.cmd on PATH to launch a dev server");
        ScriptText.Should().NotContain("VitePort", "there is no per-run Vite port to resolve");
    }

    [Fact]
    public void Script_HasFatalHttpReadinessChecks()
    {
        // Readiness must be proven, not assumed, and every readiness failure mode must be fatal:
        // a backend that exits immediately, one that never returns a success status within the
        // timeout, and any post-readiness asset that doesn't return an immediate 2xx.
        ScriptText.Should().Contain("function Wait-HttpReady");
        ScriptText.Should().Contain("function Confirm-HttpSuccess");
        ScriptText
            .Should()
            .MatchRegex(
                @"if \(-not \$ready\)\s*\{\s*if \(\$backendProcess\.HasExited\)\s*\{\s*throw",
                "an immediately-exited backend must be treated as fatal during readiness, not a soft warning"
            );
        ScriptText
            .Should()
            .Contain(
                "throw \"Published backend did not return a success status from $readinessUri within the timeout",
                "a readiness timeout must be fatal"
            );
        ScriptText
            .Should()
            .Contain(
                "throw \"Readiness check failed for $Uri (status: $status): $($_.Exception.Message)\"",
                "Confirm-HttpSuccess must throw (not return a bool/warning) on a request failure"
            );
        ScriptText
            .Should()
            .Contain(
                "throw \"Readiness check failed for $Uri`: HTTP $($response.StatusCode).\"",
                "Confirm-HttpSuccess must throw on a non-2xx status"
            );
        ScriptText
            .Should()
            .Contain(
                "Confirm-HttpSuccess -Uri \"http://localhost:$($backend.Port)/dist/index.html\"",
                "the SPA entry point must be confirmed reachable through the SAME backend port"
            );
        ScriptText
            .Should()
            .Contain(
                "Confirm-HttpSuccess -Uri \"http://localhost:$($backend.Port)$asset\"",
                "every referenced JS/CSS asset must be confirmed reachable"
            );
    }

    [Fact]
    public void Script_CleansUpOnlyWithImmutablePositivePid()
    {
        // $backendProcessId must be captured ONCE, immediately after a successful Start(), and
        // the finally-block cleanup gate must check that captured value (> 0), never re-read
        // $backendProcess.Id (which throws/is invalid when Start() itself failed, since
        // $backendProcess is assigned via ::new() before Start() is even attempted).
        ScriptText
            .Should()
            .Contain("$backendProcessId = 0", "the PID must default to 0 before Start() is attempted");
        ScriptText
            .Should()
            .Contain(
                "$backendProcessId = $backendProcess.Id",
                "the PID must be captured immediately after a successful Start()"
            );

        var occurrencesOfIdCapture = System
            .Text.RegularExpressions.Regex.Matches(ScriptText, @"\$backendProcessId\s*=\s*\$backendProcess\.Id")
            .Count;
        occurrencesOfIdCapture.Should().Be(1, "the .Id capture must happen exactly once, right after Start()");

        ScriptText
            .Should()
            .MatchRegex(
                @"if \(\$backendProcessId -gt 0\)\s*\{\s*Stop-OwnedProcessTree -ProcessId \$backendProcessId",
                "cleanup must be gated on the captured immutable PID being a real, positive value"
            );
        ScriptText
            .Should()
            .NotContain(
                "Stop-OwnedProcessTree -ProcessId $backendProcess.Id",
                "cleanup must never re-read .Id from the Process object"
            );
        ScriptText
            .Should()
            .NotContain(
                "$null -ne $backendProcess",
                "the finally gate must not be a bare non-null Process check (true even when Start() failed)"
            );
    }

    [Fact]
    public void Script_SupervisesViaBoundedInterruptibleLoop_NotIndefiniteWaitForExit()
    {
        // A bare, no-timeout WaitForExit() blocks the thread with no return point until the child
        // exits on its own, starving PowerShell's cooperative Ctrl+C handling. The bounded
        // WaitForExit(250) loop returns control to the engine every iteration; no global
        // [Console]::CancelKeyPress handler is added (that would leak past this script's lifetime).
        ScriptText
            .Should()
            .Contain(
                "while (-not $backendProcess.WaitForExit(250)) { }",
                "supervision must poll in short bounded increments, not block indefinitely"
            );
        // "CancelKeyPress" and a bare "WaitForExit()" both still appear in EXPLANATORY COMMENTS
        // describing the fix (and the bug it replaced) -- assert on CodeOnlyText (comments
        // stripped) so this is a real check of the LIVE code, not a vacuous pass/false-positive.
        CodeOnlyText
            .Should()
            .NotContain("CancelKeyPress", "no global Ctrl+C handler may be registered in live code");
        CodeOnlyText
            .Should()
            .NotContain("WaitForExit()", "no live call to the indefinite, no-argument WaitForExit() may remain");
    }

    [Fact]
    public void Script_RunStateRecordsPublishDirectoryAndOmitsViteObject()
    {
        // The side-car run-state file must describe the ACTUAL resolved publish directory and a
        // single "backend" object; there is no Vite process/PID/port to record in the standalone
        // pipeline, so no "vite" property may exist on the run-state object.
        ScriptText
            .Should()
            .MatchRegex(
                @"publishDirectory\s*=\s*\$publishDirectory",
                "run-state must record the resolved publish directory"
            );
        ScriptText
            .Should()
            .MatchRegex(@"backend\s*=\s*\[pscustomobject\]@\{", "run-state must record a single backend object");
        ScriptText
            .Should()
            .Contain(
                "There is no Vite process or Vite port",
                "the run-state note must explicitly document that there is no Vite process/port to record"
            );
        ScriptText.Should().NotMatchRegex(@"(?m)^\s*vite\s*=", "the run-state object must not have a vite property");
        ScriptText
            .Should()
            .NotMatchRegex(@"(?m)^\s*vitePort\s*=", "the run-state object must not have a vitePort property");
    }

    [Fact]
    public void Script_PublishDirectoryIsRetainedNotDeletedAfterRun()
    {
        // The publish directory (and its .run-state.json) is a retained, inspectable artifact --
        // on both success and failure -- not a scratch temp directory the script tidies up on its
        // way out. This asserts the retention message AND the absence of any cleanup call that
        // would delete it.
        ScriptText
            .Should()
            .Contain(
                "Write-Host \"    Publish directory retained at: $publishDirectory\"",
                "the script must report that the publish directory is retained, not deleted"
            );

        // Scope the "never deletes" guarantee to the ORIGINAL standalone publish+launch pipeline
        // (helper functions + Invoke-Main) -- the -DestinationDirectory extension's backup cleanup
        // (Invoke-DestinationDeploy) legitimately Remove-Item's its OWN short-lived backup sibling
        // after a successful atomic swap; it never touches $publishDirectory or $DestinationDirectory.
        var destinationFunctionsStart = ScriptText.IndexOf("function Test-DestinationState", StringComparison.Ordinal);
        destinationFunctionsStart.Should().BeGreaterThan(-1, "the destination-mode functions must exist in the script");
        var originalPipelineText = ScriptText[..destinationFunctionsStart];

        originalPipelineText
            .Should()
            .NotContain("Remove-Item", "the publish directory/run-state must never be deleted by this script");
        ScriptText
            .Should()
            .NotContain("Remove-RunStateFile", "there is no run-state cleanup helper -- the artifact is retained");
        ScriptText
            .Should()
            .NotContain(
                "Remove-Item -LiteralPath $publishDirectory",
                "the publish directory itself must never be deleted, even by the destination extension"
            );
        ScriptText
            .Should()
            .NotContain(
                "Remove-Item -LiteralPath $DestinationDirectory",
                "the destination directory must never be recursively deleted directly"
            );
    }

    [Fact]
    public void Script_PortResolutionFailsFastOnExplicitBusyPortUnlessForced()
    {
        // Verifies the LIVE conditional structure (not just the -Port help prose) that implements
        // the fail-fast-unless--Force rule: an explicitly-passed busy port throws identifying the
        // owning PID(s) unless -Force is set; an omitted (default) port instead auto-scans forward.
        ScriptText
            .Should()
            .MatchRegex(
                @"if \(\$WasExplicit\)\s*\{\s*if \(-not \$Force\)\s*\{",
                "an explicitly-passed busy port must check -Force before failing"
            );
        ScriptText
            .Should()
            .Contain(
                "is already in use (PID(s): $pidList)",
                "the fail-fast error must identify the owning process PID(s)"
            );
        ScriptText
            .Should()
            .Contain(
                "for ($offset = 1; $offset -le $ScanLimit; $offset++) {",
                "an omitted port must auto-scan forward for the next free one"
            );
    }

    [Fact]
    public void Script_RetainsPortForceAndWebhookWorkspaceConfigurationBehavior()
    {
        // Port resolution (default-with-auto-fallback vs explicit-fails-unless--Force), the
        // -WebhookBaseUrl override, and emptying SandboxGateway:WorkspaceBasePath so the remote
        // gateway owns the workspace directory are all retained, unchanged behaviors of the
        // standalone launcher.
        ScriptText
            .Should()
            .Contain("Port resolution rules:", "the -Port parameter help must retain the resolution rules");
        ScriptText
            .Should()
            .Contain(
                "its default (5050) is tried first; if busy, the script scans forward",
                "an omitted -Port must still auto-fall-back to the next free port"
            );
        ScriptText
            .Should()
            .Contain(
                "the script FAILS (so you don't accidentally stack",
                "an explicit, busy -Port must still fail fast unless -Force is passed"
            );
        ScriptText.Should().Contain("[switch] $Force", "the -Force switch parameter must still exist");
        ScriptText
            .Should()
            .Contain(
                "[string] $WebhookBaseUrl = 'https://lmstreaming.bhakars.internal'",
                "the -WebhookBaseUrl default must be retained"
            );
        ScriptText
            .Should()
            .Contain(
                "'--SandboxGateway:WorkspaceBasePath='",
                "the workspace base path must still be emptied on the command line for the remote gateway"
            );
        ScriptText
            .Should()
            .Contain(
                "\"--Auth:Webhook:PublicBaseUrl=$WebhookBaseUrl\"",
                "the resolved webhook base URL must still be passed on the command line"
            );
    }

    [Fact]
    public void Script_TestPortFreeChecksBothIPv4AndIPv6FamiliesForTheBackendPort()
    {
        // The verified repro this guards: a listener bound only to one address family (e.g. IPv6
        // loopback) was invisible to a probe that only tried the other family -- Test-PortFree
        // must require every supplied address to be free, and the backend port resolution must
        // supply all four wildcard/loopback x IPv4/IPv6 combinations.
        ScriptText
            .Should()
            .Contain(
                "[System.Net.IPAddress[]] $Addresses",
                "Test-PortFree/Resolve-InstancePort must accept multiple addresses, not a single one"
            );
        ScriptText.Should().Contain("[System.Net.IPAddress]::Any");
        ScriptText
            .Should()
            .Contain("[System.Net.IPAddress]::IPv6Any", "the backend port check must include the IPv6 wildcard family");
        ScriptText.Should().Contain("[System.Net.IPAddress]::Loopback");
        ScriptText
            .Should()
            .Contain(
                "[System.Net.IPAddress]::IPv6Loopback",
                "the backend port check must include the IPv6 loopback family"
            );
        ScriptText
            .Should()
            .Contain(
                "$backend = Resolve-InstancePort -Preferred $Port -WasExplicit $backendPortExplicit -Label 'Backend' `",
                "the backend port resolution call site must exist"
            );
    }

    [Fact]
    public void Script_HasBalancedBracesAndParentheses()
    {
        // Coarse structural sanity check: this repo has no System.Management.Automation
        // dependency to invoke the real PowerShell parser from a unit test, but an unbalanced
        // brace/paren count is a reliable signal that an edit broke the script's structure.
        var openBraces = ScriptText.Count(c => c == '{');
        var closeBraces = ScriptText.Count(c => c == '}');
        var openParens = ScriptText.Count(c => c == '(');
        var closeParens = ScriptText.Count(c => c == ')');

        openBraces.Should().Be(closeBraces, "publish-launch.ps1 must have balanced { }");
        openParens.Should().Be(closeParens, "publish-launch.ps1 must have balanced ( )");
    }

    // ----------------------------------------------------------------------------------------
    // -DestinationDirectory extension: dot-sourceability + default-path-unchanged structural proof
    // (real-filesystem behavioral coverage for the destination functions themselves lives in
    // PublishLaunchDestinationTests.cs, which dot-sources this same script via a real pwsh process).
    // ----------------------------------------------------------------------------------------

    [Fact]
    public void Script_IsDotSourceableWithoutRunningInvokeMain()
    {
        // The whole default build+publish+launch pipeline must be wrapped in a named function so
        // that dot-sourcing the script (". publish-launch.ps1") only DEFINES functions and never
        // executes npm/dotnet/launch -- the guard at the bottom must only call Invoke-Main when the
        // script was invoked normally (InvocationName is not the dot-source sentinel "."). The call
        // must pass $script:DestinationDirectoryWasExplicit/$script:PortWasExplicit explicitly --
        // those are computed at true script scope (right after the script's own param() block,
        // where $PSBoundParameters genuinely reflects this invocation's real command line) and
        // threaded in as arguments, because $PSBoundParameters read from INSIDE Invoke-Main would
        // only ever reflect Invoke-Main's own (always-empty) bound parameters, never the script's.
        ScriptText
            .Should()
            .Contain(
                "function Invoke-Main {",
                "the default pipeline body must be wrapped in a named Invoke-Main function"
            );
        ScriptText
            .Should()
            .MatchRegex(
                @"(?m)^if\s*\(\$MyInvocation\.InvocationName\s*-ne\s*'\.'\)\s*\{\s*Invoke-Main\s+-DestinationDirectoryWasExplicit\s+\$script:DestinationDirectoryWasExplicit\s+-PortWasExplicit\s+\$script:PortWasExplicit\s*\}\s*$",
                "a dot-source guard must call Invoke-Main only when the script is NOT being dot-sourced, passing the script-scope explicit-binding flags in explicitly"
            );

        var invokeMainDefinitionIndex = ScriptText.IndexOf("function Invoke-Main {", StringComparison.Ordinal);
        var guardIndex = ScriptText.IndexOf("if ($MyInvocation.InvocationName -ne '.')", StringComparison.Ordinal);
        invokeMainDefinitionIndex.Should().BeGreaterThan(-1);
        guardIndex.Should().BeGreaterThan(-1);
        invokeMainDefinitionIndex
            .Should()
            .BeLessThan(
                guardIndex,
                "Invoke-Main must be fully defined before the trailing guard decides whether to call it"
            );
    }

    [Fact]
    public void Script_DefaultPipelineBodyLivesInsideInvokeMain()
    {
        // These are the exact original top-level statements from the pre-extension script (port
        // resolution, publish, launch, final exit) -- proving they now live AFTER the Invoke-Main
        // function-open brace (i.e. they were moved INTO the function, not left at top level or
        // duplicated) is the regression guard that the default (omitted -DestinationDirectory) path
        // is unchanged in substance, just relocated.
        var invokeMainIndex = ScriptText.IndexOf("function Invoke-Main {", StringComparison.Ordinal);
        invokeMainIndex.Should().BeGreaterThan(-1);

        string[] originalTopLevelMarkers =
        [
            "Write-Phase 'Resolve port'",
            "$backend = Resolve-InstancePort -Preferred $Port",
            "$null = $backendProcess.Start()",
            "while (-not $backendProcess.WaitForExit(250)) { }",
            "exit $backendExitCode",
        ];

        foreach (var marker in originalTopLevelMarkers)
        {
            var markerIndex = ScriptText.IndexOf(marker, StringComparison.Ordinal);
            markerIndex.Should().BeGreaterThan(-1, $"marker not found: {marker}");
            markerIndex.Should().BeGreaterThan(invokeMainIndex, $"'{marker}' must now live inside Invoke-Main");
        }
    }

    [Fact]
    public void Script_HasOptionalDestinationDirectoryParameterWithNoDefault()
    {
        // Omitted = today's scratchpad publish+launch behavior, so -DestinationDirectory must be a
        // plain optional string parameter with NO default value (a default would make it always
        // "provided" from Invoke-Main's point of view).
        ScriptText
            .Should()
            .MatchRegex(
                @"(?m)^\s*\[string\]\s*\$DestinationDirectory\s*,?\s*$",
                "-DestinationDirectory must be declared as a bare [string] parameter with no default value"
            );
    }

    [Fact]
    public void Script_DestinationModeBranchesBeforePortResolutionAndNeverLaunches()
    {
        // Provided = publish-only: no port resolution, launch, readiness, or run-state. The
        // destination-mode branch must appear (textually, inside Invoke-Main) BEFORE the port
        // resolution phase begins, and the dedicated destination functions must never reference the
        // launch-pipeline machinery.
        var destinationBranchIndex = ScriptText.IndexOf(
            "Invoke-DestinationPublish -ProjectFile $ProjectFile",
            StringComparison.Ordinal
        );
        var portResolutionIndex = ScriptText.IndexOf("Write-Phase 'Resolve port'", StringComparison.Ordinal);
        destinationBranchIndex
            .Should()
            .BeGreaterThan(
                -1,
                "Invoke-Main must call Invoke-DestinationPublish when -DestinationDirectory is provided"
            );
        portResolutionIndex.Should().BeGreaterThan(-1);
        destinationBranchIndex
            .Should()
            .BeLessThan(
                portResolutionIndex,
                "the destination-mode branch must be checked before port resolution ever runs"
            );

        var destinationFunctionsStart = ScriptText.IndexOf("function Test-DestinationState", StringComparison.Ordinal);
        var invokeMainStart = ScriptText.IndexOf("function Invoke-Main {", StringComparison.Ordinal);
        destinationFunctionsStart.Should().BeGreaterThan(-1);
        invokeMainStart.Should().BeGreaterThan(destinationFunctionsStart);
        var destinationFunctionsRegion = ScriptText[destinationFunctionsStart..invokeMainStart];

        destinationFunctionsRegion
            .Should()
            .NotContain("ProcessStartInfo", "destination-mode functions must never launch the published executable");
        destinationFunctionsRegion
            .Should()
            .NotContain("Resolve-InstancePort", "destination-mode functions must never resolve a port");
        destinationFunctionsRegion
            .Should()
            .NotContain("Wait-HttpReady", "destination-mode functions must never perform readiness polling");
        destinationFunctionsRegion
            .Should()
            .NotContain("Write-RunStateFile", "destination-mode functions must never write a .run-state.json");
    }

    [Fact]
    public void Script_PreserveListConstantMatchesSpecExactly()
    {
        // The preserve-list is an exact, byte-for-byte-carried-over set per the design spec; a
        // structural check here guards against silent drift (an added/removed/reordered entry)
        // independent of the real-filesystem behavioral tests in PublishLaunchDestinationTests.cs.
        // The repo commits this script LF-only (.gitattributes: *.ps1 text eol=lf), but a Windows
        // working tree with core.autocrlf=true checks it out as CRLF on disk -- normalize before
        // comparing so this assertion checks the constant's CONTENT, not the checkout's EOL style.
        var normalizedScriptText = ScriptText.Replace("\r\n", "\n");
        normalizedScriptText
            .Should()
            .Contain(
                "$script:DestinationPreserveList = @(\n"
                    + "    'conversations',\n"
                    + "    'notify-waits.db',\n"
                    + "    'oauth-tokens',\n"
                    + "    'workspaces',\n"
                    + "    'chat-modes',\n"
                    + "    'workflow-index',\n"
                    + "    'logs',\n"
                    + "    'recordings',\n"
                    + "    '.env'\n"
                    + ")",
                "the preserve-list constant must contain exactly the spec's entries, in order"
            );
    }
}
