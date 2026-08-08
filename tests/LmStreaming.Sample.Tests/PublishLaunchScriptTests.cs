namespace LmStreaming.Sample.Tests;

/// <summary>
/// "Script parser" regression coverage for <c>publish-launch.ps1</c>. There is no PowerShell
/// test harness (e.g. Pester) in this repo, so these tests assert on the raw script SOURCE TEXT
/// rather than executing it -- they pin the shape of the fix for a verified multi-instance
/// launcher defect: an orphaned Vite process (real PID hidden behind the npm.cmd wrapper's PID)
/// on an IPv6-loopback-only listener was invisible to an IPv4-only port-free check, so a second
/// launcher instance mis-detected the port as free and wrote a run-state pairing that didn't
/// match reality. Each test targets one specific regression so a future edit that reintroduces
/// the old behavior fails with a pointed message instead of a vague diff.
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
            "publish-launch.ps1"));

    private static string ScriptText => File.ReadAllText(ScriptPath);

    [Fact]
    public void Script_DoesNotLaunchViteViaNpmWrapper()
    {
        // The npm.cmd wrapper is two process hops above the real Node/Vite listener, so the PID
        // Start-Process captured from it did not correspond to the process actually holding the
        // socket -- root cause #1 of the orphaned-Vite defect. "npm.cmd" itself may still appear
        // in explanatory comments describing the old bug, so assert on the actual invocation
        // shape rather than banning the substring outright.
        ScriptText.Should().NotContain("Get-Command npm.cmd", "the script must no longer resolve npm.cmd on PATH to launch Vite");
        ScriptText.Should().NotContain("-FilePath $npmCmd", "Vite must no longer be started via the npm.cmd wrapper");
        ScriptText.Should().NotContain("'run', 'dev'", "the 'npm run dev' argument list must be gone");
    }

    [Fact]
    public void Script_LaunchesViteDirectlyViaNodeWithStrictPort()
    {
        ScriptText.Should().Contain("node_modules/vite/bin/vite.js", "Vite must be launched directly via node so the captured PID is the real listener");
        ScriptText.Should().Contain("'--strictPort'", "Vite must be started with --strictPort so it refuses to silently rebind on a lost port race");
    }

    [Fact]
    public void Script_HasExitedCheckImmediatelyAfterViteSpawn()
    {
        // A dead-on-arrival Vite process (e.g. it lost a TOCTOU race for the port despite our
        // own pre-check) must be detected and treated as fatal, not silently left running.
        ScriptText.Should().Contain("$viteProcess.HasExited", "the script must check Vite's process liveness after spawning it");
        ScriptText.Should().MatchRegex(
            @"if \(\$viteProcess\.HasExited\)\s*\{\s*throw",
            "a Vite process that exited immediately must be treated as fatal (throw), not a soft warning");
    }

    [Fact]
    public void Script_TestPortFreeChecksBothIPv4AndIPv6Families()
    {
        // The verified repro: a listener bound only to IPv6 loopback (::1) was invisible to a
        // probe that only tried IPv4 loopback (127.0.0.1) -- Test-PortFree must now require
        // every supplied address to be free, and both port-resolution call sites must supply
        // both address families.
        ScriptText.Should().Contain("[System.Net.IPAddress[]] $Addresses", "Test-PortFree/Resolve-InstancePort must accept multiple addresses, not a single one");
        ScriptText.Should().Contain("[System.Net.IPAddress]::IPv6Loopback", "the Vite port check must include the IPv6 loopback family");
        ScriptText.Should().Contain("[System.Net.IPAddress]::IPv6Any", "the backend port check must include the IPv6 any family");
    }

    [Fact]
    public void Script_HasProxyReadinessCheckProvingViteReachesThisBackend()
    {
        // Readiness must prove THIS Vite instance proxies to THIS backend, not merely that some
        // HTTP server answers -- a stale/leaked Vite instance would also answer "/" but proxy
        // elsewhere.
        ScriptText.Should().Contain("function Test-ViteProxyReachesBackend", "a dedicated proxy-identity readiness check must exist");
        ScriptText.Should().Contain("/api/providers", "the proxy check must exercise the real backend API through the Vite proxy");
        ScriptText.Should().Contain("Test-ViteProxyReachesBackend -VitePort $vite.Port", "the proxy check must actually be invoked during launch");
    }

    [Fact]
    public void Script_RunStateIncludesResolvedStatePaths()
    {
        // Run-state previously only carried a narrative claim about state paths; it must now
        // report the actual resolved build-output directory and state-store paths.
        ScriptText.Should().Contain("function Get-BuildOutputDirectory", "a helper resolving the real build output directory must exist");
        ScriptText.Should().Contain("paths          = $statePaths", "the run-state object must include a concrete paths field");
        ScriptText.Should().Contain("outputDirectory = $buildOutputDir", "the paths object must report the resolved build output directory");
    }

    [Fact]
    public void Script_ProcessSpawnsAreWrappedInTryFinallyForCleanup()
    {
        // Any exception between starting a child process and writing the run-state file must
        // not leak an unsupervised process: both Start-Process calls must live inside the same
        // try/finally that tree-kills them.
        var viteStartIndex = ScriptText.IndexOf("$viteProcess = Start-Process", StringComparison.Ordinal);
        var tryIndex = ScriptText.IndexOf("\ntry {", StringComparison.Ordinal);
        var backendStartIndex = ScriptText.IndexOf("$backendProcess = Start-Process", StringComparison.Ordinal);
        var finallyIndex = ScriptText.IndexOf("} finally {", StringComparison.Ordinal);

        viteStartIndex.Should().BeGreaterThan(-1);
        backendStartIndex.Should().BeGreaterThan(-1);
        tryIndex.Should().BeGreaterThan(-1);
        finallyIndex.Should().BeGreaterThan(-1);

        tryIndex.Should().BeLessThan(viteStartIndex, "the try block must open before Vite is started");
        viteStartIndex.Should().BeLessThan(backendStartIndex);
        backendStartIndex.Should().BeLessThan(finallyIndex, "both Start-Process calls must be inside the try block");
    }

    [Fact]
    public void Script_DetectsStaleClientDependenciesAgainstPackageLockMarker()
    {
        // A dependency added/bumped upstream (e.g. after a `git pull`) can leave an existing
        // node_modules installed-but-STALE relative to package-lock.json -- a materially
        // different condition from "missing" that -SkipClientBuild's existing contract does not
        // cover. node_modules/.package-lock.json is the marker npm itself writes on `npm ci`, so
        // comparing its timestamp against package-lock.json's is the same staleness signal `npm
        // ci` relies on internally.
        ScriptText.Should().Contain("function Test-ClientDependenciesFresh", "a dedicated dependency-freshness check must exist");
        ScriptText.Should().Contain("node_modules/.package-lock.json", "freshness must be measured against npm's own install marker file");
        ScriptText.Should().Contain("LastWriteTimeUtc -le", "freshness must compare package-lock.json's timestamp against the install marker's");
    }

    [Fact]
    public void Script_SelfHealsStaleClientDependenciesBeforeStartingVite()
    {
        // The self-heal must run regardless of -SkipClientBuild (that flag only skips the
        // client BUILD, not a freshness check), and it must run before Vite is started so a
        // stale install is fixed before the dev server would otherwise start against it.
        var freshCheckIndex = ScriptText.IndexOf("Test-ClientDependenciesFresh -ClientAppDir $ClientAppDir", StringComparison.Ordinal);
        var npmCiIndex = ScriptText.IndexOf("npm ci --prefix $ClientAppDir", StringComparison.Ordinal);
        var viteStartIndex = ScriptText.IndexOf("$viteProcess = Start-Process", StringComparison.Ordinal);

        freshCheckIndex.Should().BeGreaterThan(-1, "the self-heal call site must invoke the freshness check");
        npmCiIndex.Should().BeGreaterThan(-1, "the self-heal must actually run npm ci when stale");
        viteStartIndex.Should().BeGreaterThan(-1);

        freshCheckIndex.Should().BeLessThan(npmCiIndex, "the freshness check must gate the npm ci call");
        npmCiIndex.Should().BeLessThan(viteStartIndex, "dependencies must be refreshed before Vite is started");
    }

    [Fact]
    public void Script_HasEntryModuleReadinessCheckMatchingViteBasePath()
    {
        // Vite transforms/resolves modules on request (unlike a static build), so a stale or
        // broken node_modules can start Vite and answer "/" successfully while the app's actual
        // entry module 500s with an unresolved import -- only surfacing in the browser. This
        // check must probe the entry module under the SAME base path vite.config.ts configures
        // (`base: '/dist/'`); a bare /src/main.ts 404s under that base and would be a false
        // positive for "broken", not a true readiness signal.
        ScriptText.Should().Contain("function Test-ViteEntryModuleResolves", "a dedicated entry-module readiness check must exist");
        ScriptText.Should().Contain("/dist/src/main.ts", "the probed path must match vite.config.ts's base: '/dist/'");
    }

    [Fact]
    public void Script_TreatsUnresolvedEntryModuleAsFatalNotAWarning()
    {
        // Unlike the softer "continuing anyway" warnings elsewhere in phase 2, an unresolved
        // entry module means the app cannot load in ANY browser -- declaring "Ready" anyway
        // would hide a genuinely broken launch until someone opened a tab.
        ScriptText.Should().MatchRegex(
            @"if \(-not \(Test-ViteEntryModuleResolves -VitePort \$vite\.Port\)\)\s*\{\s*throw",
            "a failed entry-module resolution must be fatal (throw), not a soft warning");

        var entryCheckIndex = ScriptText.IndexOf("Test-ViteEntryModuleResolves -VitePort $vite.Port", StringComparison.Ordinal);
        var backendStartIndex = ScriptText.IndexOf("Write-Phase \"Start backend on", StringComparison.Ordinal);
        entryCheckIndex.Should().BeGreaterThan(-1);
        backendStartIndex.Should().BeGreaterThan(-1);
        entryCheckIndex.Should().BeLessThan(backendStartIndex, "the entry-module check must run before the backend is started");
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
}
