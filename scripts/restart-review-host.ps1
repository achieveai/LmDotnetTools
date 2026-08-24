#!/usr/bin/env pwsh
# Restart the LmStreaming review host on 5051 (the host the review daemons read).
#
# TRACKED COPY. This script used to exist only as an untracked file inside the gitignored
# .run/ directory, beside its sibling watchdog. The watchdog was deleted and was
# unrecoverable, while a Windows scheduled task kept firing at its corpse every five
# minutes; this launcher was the only survivor. Operational scripts belong in the
# repository. .run/ is ignored for its multi-GB databases and logs, not for its code.
#
# Why this script exists rather than a plain `dotnet run`:
#
#   AgentCollaboration:Enabled is deliberately FALSE in appsettings.json - #244 is
#   opt-in, and turning it on grants peer messaging, a hierarchy roster, and
#   transcript reads. The review daemon REQUIRES it: with collaboration off the
#   host builds no collaboration state, so every /agents/{id}/transcript read
#   answers 404 collaboration_unavailable and every per-sub-agent findings
#   artifact ends in a bare 404 instead of a transcript.
#
#   The host runs as Environment=Test, so appsettings.Development.json is never
#   loaded and cannot carry the override. That leaves environment variables as
#   the only place to put it - which means a restart that forgets them turns A2A
#   back off SILENTLY: the host starts fine, serves every other route, and only
#   the transcript reads degrade. Hence: always restart through this script.
#
#   Depth 2 / 64 agents is headroom over the shipped defaults (1 / 32). Real
#   review threads observed max depth 1, but enabling collaboration also STARTS
#   ENFORCING those nesting limits, which are inert while it is off.
#
# NOTE FOR CALLERS: this script sets ASPNETCORE_URLS and ASPNETCORE_ENVIRONMENT in its own
# process so the launched host inherits them. Anything that runs this script and then
# launches ANOTHER service from the same process inherits them too - and ASPNETCORE_URLS
# would pin that second service to :5051. scripts/ensure-services.ps1 therefore invokes
# this file in a child pwsh rather than calling it in-process.

param(
    # Directory holding LmStreaming.Sample.exe: the review host's OWN published deployment.
    #
    # This used to default to the WT2 worktree's bin/Debug output, which is precisely the
    # dependency this script being tracked exists to remove - it pinned a live service to a
    # gitignored build inside a worktree, so that worktree could never be reused or reset.
    # It must equally NOT point at B:\published\LmStreaming.Sample: that is the interactive
    # chat host on :5050, and starting a second instance out of its deployment makes this
    # process exit code 0 immediately (observed 2026-08-23).
    [string]$BinDir = "B:\published\review-host",
    [int]$Port = 5051,
    # Where the redirected stdout/stderr logs land. Gitignored on purpose: this directory
    # carries multi-GB live state.
    [string]$RunDir = "B:\sources\LmDotnetTools\.run",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
# Matches both siblings in this directory. Without it a typo'd variable reads as $null and
# this script silently restarts the wrong thing, or nothing.
Set-StrictMode -Version Latest

$exe = Join-Path $BinDir "LmStreaming.Sample.exe"
if (-not (Test-Path $exe)) { throw "Host binary not found: $exe" }

$run = $RunDir
if (-not (Test-Path $run)) { New-Item -ItemType Directory -Path $run -Force | Out-Null }

# REFUSE to restart while a review is mid-flight.
#
# Killing the host under a running review does not fail the review - it makes it
# a ZOMBIE. The loop dies with the process, but the run's PERSISTED status stays
# "InProgress", and the daemon polls /status (persisted) rather than /run-state
# (live), so it keeps politely polling a corpse until its 30-minute overall
# timeout fires, then retries. Nothing errors, nothing alerts; the review is just
# silently half an hour late. Observed on run 222, 2026-08-04.
#
# /run-state is the live signal and is what makes this checkable at all: for the
# zombie it already reports isInProgress=false, because it reflects the loop
# rather than the record.
try {
    $convs = Invoke-RestMethod -Uri "http://localhost:$Port/api/conversations" -TimeoutSec 30
    $list = if ($convs -is [array]) { $convs } elseif ($convs.conversations) { $convs.conversations } else { @() }
    $busy = @()
    foreach ($c in $list) {
        try {
            $rs = Invoke-RestMethod -Uri "http://localhost:$Port/api/conversations/$($c.threadId)/run-state" -TimeoutSec 15
            if ($rs.isInProgress) { $busy += "$($c.threadId) (run $($rs.currentRunId))" }
        }
        catch { }
    }
    if ($busy.Count -gt 0) {
        Write-Warning "$($busy.Count) review(s) IN FLIGHT on :$Port -"
        $busy | ForEach-Object { Write-Warning "  $_" }
        if (-not $Force) {
            throw "Refusing to restart: killing these makes them zombies that stall ~30 min. Wait, or re-run with -Force."
        }
        Write-Warning "-Force given; restarting anyway."
    }
    else { Write-Host "no reviews in flight - safe to restart" }
}
catch [System.Net.WebException], [System.Net.Http.HttpRequestException] {
    # An HTTP ERROR STATUS is not the same as an unreachable host, and conflating them is
    # dangerous here. PowerShell 7 throws HttpResponseException for a non-2xx response, and
    # HttpResponseException DERIVES FROM HttpRequestException - so a live host answering 500
    # on /api/conversations lands in this same catch. Treating that as "nothing in flight"
    # skips the guard entirely and restarts a host that may be mid-review, which is exactly
    # the zombie this block exists to prevent (the run's persisted status stays InProgress
    # and the daemon polls a corpse for ~30 minutes).
    #
    # So: only a genuine connection failure proves nothing is in flight. A reachable host we
    # could not interrogate proves nothing either way, and must refuse without -Force.
    $status = $null
    if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
        $status = $_.Exception.Response.StatusCode
    }

    if ($null -ne $status) {
        Write-Warning "host on :$Port answered HTTP $([int]$status) - it is UP, but its in-flight reviews could not be read."
        if (-not $Force) {
            throw "Refusing to restart: cannot prove no review is in flight (host answered HTTP $([int]$status)). Re-run with -Force to restart anyway."
        }
        Write-Warning "-Force given; restarting anyway."
    }
    else {
        Write-Host "host not reachable on :$Port - nothing in flight to protect"
    }
}

# Stop whatever holds the port. Do this by LISTENER, not by a remembered pid:
# a stale pid file points at nothing after a crash, and the thing that actually
# blocks the rebind is whoever owns the socket right now. This is also why the check
# is by PORT rather than by process name: the host runs as an APPHOST
# (LmStreaming.Sample.exe), not under dotnet.exe, so a name filter on 'dotnet' finds
# nothing and concludes, wrongly, that the port is free.
Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique |
    ForEach-Object { Write-Host "stopping pid $_ on $Port"; Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 3

$env:ASPNETCORE_ENVIRONMENT = "Test"
$env:ASPNETCORE_URLS = "http://localhost:$Port"

# Pin the discovery callback to THIS host's own port. Both appsettings.json and
# appsettings.Development.json default Auth:Webhook:PublicBaseUrl to
# http://127.0.0.1:5000, so on any other port the sandbox gateway delivers the
# context-discovery catalog to a port nobody is listening on, no sub-agent
# templates ever arrive, and reviews complete with zero children and a 38-char
# "No new findings since the last review." - indistinguishable from a clean PR.
$env:Auth__Webhook__PublicBaseUrl = "http://127.0.0.1:$Port"

$env:AgentCollaboration__Enabled = "true"
$env:AgentCollaboration__MaxDelegationDepth = "2"
$env:AgentCollaboration__MaxTotalAgents = "64"

$p = Start-Process -FilePath $exe -WorkingDirectory $BinDir -PassThru -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $run "review-host-$Port.out.log") `
    -RedirectStandardError  (Join-Path $run "review-host-$Port.err.log")

# Tracked explicitly rather than left implicit. The watchdog calls this script and has to
# be able to tell "started" from "never came up"; the original fell out of the loop after
# 120 seconds without a word and returned success either way, which is precisely the shape
# of failure this whole exercise is about.
$listening = $false

for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 2
    if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
        $listening = $true
        Write-Host "LISTENING on $Port (pid $($p.Id)) after $($i * 2)s"

        # Verify collaboration actually ATTACHED, rather than trusting that the
        # env bound. A live thread answers 403 unknown_target for an agent id
        # that does not exist; a collaboration-off host answers 404
        # collaboration_unavailable for the same request. That pair is the only
        # cheap signal that distinguishes "host is up" from "host is up and A2A
        # works", and they differ precisely in the way that matters here.
        try {
            $tid = (Invoke-RestMethod -Method Post -Uri "http://localhost:$Port/api/conversations" `
                    -ContentType "application/json" `
                    -Body '{"workspaceId":"default","providerId":"test","modeId":"default"}').threadId
            Invoke-RestMethod -Method Post -Uri "http://localhost:$Port/api/conversations/$tid/messages" `
                -ContentType "application/json" -Body '{"text":"hi"}' | Out-Null
            Start-Sleep -Seconds 8
            try {
                Invoke-RestMethod -Uri "http://localhost:$Port/api/conversations/$tid/agents/nonexistent/transcript" | Out-Null
                Write-Warning "collaboration probe returned 200 for a nonexistent agent - investigate"
            }
            catch {
                $code = $_.Exception.Response.StatusCode.value__
                if ($code -eq 403) { Write-Host "collaboration ATTACHED (403 unknown_target)" }
                elseif ($code -eq 404) { Write-Warning "collaboration OFF (404) - the env override did not bind" }
                else { Write-Warning "collaboration probe returned $code" }
            }
        }
        catch { Write-Warning "collaboration probe failed: $($_.Exception.Message)" }

        # Discovery fan-out cannot be probed by a request - it is a push from the
        # gateway - and the announce line comes from SandboxSessionRegistry when a
        # sandbox session is created, NOT at startup. The probe conversation above
        # uses workspace/mode "default", which creates no session, so at this point
        # the line is legitimately absent and its absence proves nothing. Report
        # only if it is already present and WRONG; otherwise hand the operator the
        # grep to run once a real review has started.
        $log = Join-Path $run "review-host-$Port.out.log"
        $deliver = Select-String -Path $log -Pattern "gateway will deliver discoveries to" -ErrorAction SilentlyContinue |
            Select-Object -Last 1
        if ($null -eq $deliver) {
            Write-Host "discovery callback: not announced yet (emitted on the first sandbox session)."
            Write-Host "  after a real review starts, confirm with:"
            Write-Host "  Select-String -Path '$log' -Pattern 'deliver discoveries to' | Select-Object -Last 1"
        }
        elseif ($deliver.Line -match [regex]::Escape(":$Port/")) {
            Write-Host "discovery callback pinned to :$Port"
        }
        else {
            Write-Warning "discovery callback points elsewhere: $($deliver.Line)"
        }
        break
    }
    if ($p.HasExited) { throw "host EXITED code=$($p.ExitCode); see review-host-$Port.err.log" }
}

if (-not $listening) {
    throw "host never LISTENED on $Port within 120s (pid $($p.Id)); see $run\review-host-$Port.err.log"
}
