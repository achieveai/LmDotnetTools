namespace LmStreaming.Sample.Tests;

/// <summary>
/// Regression coverage for the certification-marker timestamp handling in
/// <c>scripts/ensure-services.ps1</c> (<c>Get-ReviewHostUncertifiedReason</c>).
///
/// <para>
/// The bug: the launcher writes <c>hostStartedAtUtc</c> as an ISO-8601 'Z' string, and under
/// pwsh 7 <c>ConvertFrom-Json</c> ALREADY parses that into a <c>[datetime]</c> with Kind=Utc.
/// The old code fed that DateTime to <c>[datetime]::Parse</c>, which coerced it through
/// <c>ToString()</c> - dropping the sub-second digits and the Kind - so the re-parse came back
/// Unspecified and <c>ToUniversalTime()</c> re-applied the local offset. On a UTC+7 box every
/// certified host then read as started 7 hours in the future, failed the 2-second start-time
/// tolerance, and was reported as "the pid was reused"; the watchdog restarted a healthy review
/// host on every backoff expiry for five days (412 logged restart attempts, 2026-08-24..29).
/// </para>
///
/// <para>
/// Two tests, because neither alone closes the hole:
/// the execution test proves the fixed round trip certifies for real, but ON A UTC BOX (like the
/// GitHub-hosted CI runner) the pre-fix expression ALSO passes it - a zero local offset makes the
/// Kind loss harmless and the sub-second truncation sits inside the 2s tolerance - so that test
/// alone would go green over a reintroduction of the bug. The source-text test (the same
/// technique <see cref="PublishLaunchScriptTests"/> uses, since no Pester harness exists in this
/// repo) fails everywhere if the naked Parse comes back, and is itself no proof the current
/// expression WORKS - which is what the execution test supplies.
/// </para>
/// </summary>
public class EnsureServicesScriptTests
{
    private static readonly string ScriptPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "scripts", "ensure-services.ps1")
    );

    private static string ScriptText => File.ReadAllText(ScriptPath);

    // The fix's own explanatory comment names "[datetime]::Parse" while describing the old
    // behavior, so the NotContain assertions below must run against LIVE CODE only. The script
    // uses full-line comments throughout (verified: no trailing "# ..." shares a line with code
    // in Get-ReviewHostUncertifiedReason), so stripping full-line comments is sufficient.
    private static string CodeOnlyText =>
        string.Join('\n', ScriptText.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    [Fact]
    public void CertTimestamp_IsNeverFedToDatetimeParseDirectly_AndStringsParseWithRoundtripKind()
    {
        // The pre-fix expression. On a UTC box the execution test below cannot catch its
        // reintroduction (see class remarks), so this is the assertion that fails everywhere.
        CodeOnlyText
            .Should()
            .NotContain(
                "[datetime]::Parse($cert.hostStartedAtUtc)",
                "hostStartedAtUtc may already be a [datetime] (pwsh 7 ConvertFrom-Json), and Parse "
                    + "of a DateTime coerces through ToString(), losing sub-seconds and Kind"
            );

        // The fix's two branches: an already-parsed [datetime] is used directly...
        CodeOnlyText
            .Should()
            .Contain(
                "$cert.hostStartedAtUtc -is [datetime]",
                "the value ConvertFrom-Json hands over under pwsh 7 is already a [datetime] and must "
                    + "not be re-parsed"
            );

        // ...and a genuine string parses with RoundtripKind so a trailing 'Z' survives as Kind=Utc
        // instead of Unspecified (which ToUniversalTime() would shift by the local offset).
        CodeOnlyText
            .Should()
            .Contain(
                "[System.Globalization.DateTimeStyles]::RoundtripKind",
                "a string timestamp must round-trip its Kind so ToUniversalTime() does not re-apply "
                    + "the local offset"
            );
    }

    [SkippableFact]
    public void Marker_RoundTrippedThroughJson_WithZuluTimestamp_Certifies()
    {
        // Get-ReviewHostUncertifiedReason resolves the port owner via Get-NetTCPConnection, which
        // exists only on Windows. The only .NET CI leg runs on Windows, so this executes there.
        Skip.IfNot(OperatingSystem.IsWindows(), "Get-ReviewHostUncertifiedReason uses Get-NetTCPConnection");

        // The pwsh child certifies ITSELF: it opens a real listener on an ephemeral loopback port,
        // writes a certification marker for its own pid/path/start time - the timestamp as the
        // launcher writes it, an ISO-8601 'o' string with a trailing 'Z', pushed through the same
        // ConvertTo-Json | ConvertFrom-Json round trip the marker file undergoes in production -
        // and then asks the dot-sourced function whether that host is certified. Pre-fix, on any
        // box west or east of UTC, this returned "the pid was reused"; the fixed expression must
        // return no reason at all. (On a UTC box this passes either way - the source-text test
        // above is the guard that works there.)
        var command = $$"""
            . '{{PublishLaunchScriptHost.QuoteSingle(ScriptPath)}}'
            try {
                $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("ensure-services-test-" + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $tmp | Out-Null
                $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
                $listener.Start()
                try {
                    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
                    $proc = Get-Process -Id $PID
                    [pscustomobject]@{
                        hostPid          = $PID
                        hostPath         = $proc.Path
                        hostStartedAtUtc = $proc.StartTime.ToUniversalTime().ToString('o')
                    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $tmp "review-host-$port.certified.json") -Encoding utf8
                    $reason = Get-ReviewHostUncertifiedReason -Port $port -RunDir $tmp `
                        -ExpectedProcessName $proc.ProcessName -ExpectedPath $proc.Path
                    if ($null -eq $reason) { 'CERTIFIED' } else { "UNCERTIFIED: $reason" }
                }
                finally {
                    $listener.Stop()
                    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
            catch {
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 1
            }
            """;

        var result = PublishLaunchScriptHost.Run(command);

        result.StandardError.Should().BeEmpty();
        result.Succeeded.Should().BeTrue("the certification probe itself must not fail");
        result
            .StandardOutput.Trim()
            .Should()
            .Be(
                "CERTIFIED",
                "a marker whose 'Z' timestamp matches the live process's start time must certify; "
                    + "'the pid was reused' here means the timestamp's Kind was mangled on the way "
                    + "back from JSON"
            );
    }
}
