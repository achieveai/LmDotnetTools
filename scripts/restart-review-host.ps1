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
    # The process name this script owns on $Port. Anything ELSE listening there is a foreign
    # service: it is reported and left alone rather than terminated, because a port collision
    # must not become an outage of something unrelated.
    [string]$ExpectedProcessName = "LmStreaming.Sample",
    # Whole-operation budget for start + certification, enforced as a single deadline across
    # every wait below. This MUST stay under ensure-services.ps1's -StartTimeoutSeconds (240s
    # by default), which KILLS this launcher when it overruns: per-request timeouts alone let
    # the loops compose into far longer than that, so the watchdog would kill certification in
    # progress and leave a launched host running but unreported.
    #
    # The deadline is tested at the top of each loop, so a single in-flight iteration can
    # overrun it - by at most (3s sleep + 30s request) in the collaboration poll and
    # (2s sleep + 10s request) in the readiness wait. Worst case is therefore about
    # 180 + 45 = 225s, which still lands inside 240s. Raising this default without raising
    # -StartTimeoutSeconds to match reintroduces exactly the kill it exists to avoid.
    [int]$CertifyTimeoutSeconds = 180,
    [switch]$Force,
    # Accept a host whose collaboration capability could not be verified. Off by default:
    # an unverified host is the silent-degradation case this script exists to catch, so it
    # must be an explicit operator decision rather than a warning nobody reads.
    [switch]$AllowCollaborationOff
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
    # A conversation whose run-state could NOT be read is not evidence that it is idle.
    # This loop used to `catch { }`, which is the same defect as the outer catch below, one
    # level in: a 15s timeout, an HTTP 500, an auth failure or a response missing
    # isInProgress all left $busy empty, the guard passed, and the host was killed under a
    # live review. Fail CLOSED instead - unreadable is neither busy nor idle, and only
    # -Force may proceed past it.
    #
    # Set-StrictMode is what makes the malformed-response case land here at all: under
    # StrictMode reading a property the payload does not carry throws rather than
    # evaluating to $null (which would have read as "not in progress").
    $unreadable = @()
    foreach ($c in $list) {
        try {
            $rs = Invoke-RestMethod -Uri "http://localhost:$Port/api/conversations/$($c.threadId)/run-state" -TimeoutSec 15
            if ($rs.isInProgress) { $busy += "$($c.threadId) (run $($rs.currentRunId))" }
        }
        catch {
            $unreadable += "$($c.threadId) ($($_.Exception.Message))"
        }
    }
    if ($busy.Count -gt 0) {
        Write-Warning "$($busy.Count) review(s) IN FLIGHT on :$Port -"
        $busy | ForEach-Object { Write-Warning "  $_" }
        if (-not $Force) {
            throw "Refusing to restart: killing these makes them zombies that stall ~30 min. Wait, or re-run with -Force."
        }
        Write-Warning "-Force given; restarting anyway."
    }
    if ($unreadable.Count -gt 0) {
        Write-Warning "$($unreadable.Count) conversation(s) on :$Port could NOT be interrogated -"
        $unreadable | ForEach-Object { Write-Warning "  $_" }
        if (-not $Force) {
            throw "Refusing to restart: $($unreadable.Count) conversation(s) would not report run-state, so no review can be proven idle. Re-run with -Force to restart anyway."
        }
        Write-Warning "-Force given; restarting anyway."
    }
    if ($busy.Count -eq 0 -and $unreadable.Count -eq 0) {
        Write-Host "no reviews in flight - safe to restart"
    }
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
#
# ESTABLISH IDENTITY BEFORE KILLING. Holding the port is a reason to look at a process, not
# a licence to terminate it: on a port collision or a mistyped -Port this script would take
# out an unrelated service, and the survivor check below cannot undo that - by then the
# foreign process is already dead. So kill only what we came for, and refuse otherwise.
$stopFailed = @()
$foreign = @()
Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique |
    ForEach-Object {
        $ownerPid = [int]$_

        # Name, not Path: Path is NULL rather than an error for a process this session cannot
        # open, so a path comparison would silently mismatch and read as foreign. A process
        # that vanished between the socket query and here is simply gone - not our problem.
        $owner = Get-Process -Id $ownerPid -ErrorAction SilentlyContinue
        if ($null -eq $owner) {
            Write-Host "pid $ownerPid on $Port exited on its own"
            return
        }
        if ($owner.ProcessName -ne $ExpectedProcessName) {
            $foreign += "$ownerPid ($($owner.ProcessName))"
            return
        }

        Write-Host "stopping pid $ownerPid ($($owner.ProcessName)) on $Port"
        # NOT SilentlyContinue. A stop that fails - access denied, a process we do not own -
        # leaves the socket held, and the readiness loop below would then see that SURVIVING
        # listener and call the restart a success.
        try { Stop-Process -Id $ownerPid -Force -ErrorAction Stop }
        catch { $stopFailed += "$ownerPid ($($_.Exception.Message))" }
    }

if ($foreign.Count -gt 0) {
    $foreign | ForEach-Object { Write-Warning "port $Port is held by a FOREIGN process: $_" }
    if (-not $Force) {
        throw "Refusing to restart: port $Port is held by $($foreign -join ', '), not '$ExpectedProcessName'. Killing it would take out an unrelated service. Check the port, or re-run with -Force."
    }
    Write-Warning "-Force given; terminating the foreign holder(s) anyway."
    foreach ($f in $foreign) {
        $fid = [int]($f -split ' ')[0]
        try { Stop-Process -Id $fid -Force -ErrorAction Stop }
        catch { $stopFailed += "$fid ($($_.Exception.Message))" }
    }
}

Start-Sleep -Seconds 3

# Never launch into an occupied port. The new host would fail to bind and exit, and
# "something is listening on 5051" is precisely the signal the watchdog reads as health -
# so a survivor here becomes a healthy-looking review host that no daemon can use.
$survivorIds = @(
    Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique |
        ForEach-Object { [int]$_ }
)
if ($survivorIds.Count -gt 0) {
    $stopFailed | ForEach-Object { Write-Warning "failed to stop pid $_" }
    throw "Port $Port is still held by pid(s) $($survivorIds -join ', ') after the stop attempt; refusing to launch a host that cannot bind."
}

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

# ONE deadline for everything after the launch. Bounding each request separately is not the
# same as bounding the operation: the listen wait, the readiness wait and the collaboration
# poll compose, and their sum has to fit inside the watchdog's patience with this script.
$launchedAt = (Get-Date)
$certifyDeadline = $launchedAt.AddSeconds($CertifyTimeoutSeconds)
$outOfTime = { (Get-Date) -ge $certifyDeadline }

while (-not (& $outOfTime)) {
    Start-Sleep -Seconds 2

    # Check OUR CHILD before the port. The old order asked "is anything listening?" first
    # and broke out of the loop on the first yes, so the exit check at the bottom was never
    # reached when a foreign listener held the port - a dead child was reported as a
    # successful start.
    if ($p.HasExited) { throw "host EXITED code=$($p.ExitCode); see review-host-$Port.err.log" }

    $ownerIds = @(
        Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique |
            ForEach-Object { [int]$_ }
    )
    if ($ownerIds.Count -gt 0) {
        # The listener must be the process THIS script launched. Port occupancy is what the
        # watchdog uses for liveness, and it is exactly what must not be conflated with
        # "the host came up" - otherwise an unrelated process on 5051 is handed the daemons.
        if ($ownerIds -notcontains [int]$p.Id) {
            throw "port $Port is owned by pid(s) $($ownerIds -join ', '), not the host this script launched (pid $($p.Id)); refusing to report a foreign listener as a successful restart."
        }
        $listening = $true
        Write-Host "LISTENING on $Port (pid $($p.Id)) after $([int]((Get-Date) - $launchedAt).TotalSeconds)s"

        # Verify collaboration actually ATTACHED, rather than trusting that the
        # env bound. A live thread answers 403 unknown_target for an agent id
        # that does not exist; a collaboration-off host answers 404
        # collaboration_unavailable for the same request. That pair is the only
        # cheap signal that distinguishes "host is up" from "host is up and A2A
        # works", and they differ precisely in the way that matters here.
        # This probe GATES the restart; it is not advisory. A collaboration-off host starts
        # cleanly, serves every other route, and degrades only the transcript reads - so
        # warning and exiting 0 hands the daemons a host whose reviews finish with zero
        # sub-agents and a 38-char "No new findings", indistinguishable from a clean PR.
        # That silent degradation is the entire reason this script exists.
        #
        # The throw lands AFTER the host is listening, so the host stays up: this reports a
        # restart that cannot be certified, it does not undo one. ensure-services.ps1 logs
        # START FAILED, and its next tick sees the port healthy and leaves it alone - so a
        # flaky probe cannot produce a restart loop.
        $collabFailure = $null

        # Wait for the host to be SERVING, not merely LISTENING, before probing. The socket
        # binds well before the first request can be answered - that warm-up window is the
        # documented one where daemons see connection-refused and then 503 from
        # CreateWorkspaceAsync - so an immediate probe can fail for a reason that says nothing
        # about collaboration. That now decides the exit code, so it has to be the real signal.
        #
        # Every call below is also BOUNDED. Invoke-RestMethod has no default timeout: a probe
        # the host never answers would block this script forever, and with it the watchdog run
        # that invoked it. A bound is cheap; an unbounded supervision run is not recoverable.
        $ready = $false
        while (-not (& $outOfTime)) {
            try {
                Invoke-RestMethod -Uri "http://localhost:$Port/api/conversations" -TimeoutSec 10 | Out-Null
                $ready = $true
                break
            }
            catch { Start-Sleep -Seconds 2 }
        }
        if (-not $ready) {
            $collabFailure = "host listened on $Port but never answered /api/conversations within the ${CertifyTimeoutSeconds}s certification budget, so collaboration could not be probed"
        }

        # Declared before the try so the cleanup below can see it even when the POST that
        # assigns it is what failed. Under StrictMode an undeclared $tid there is an error,
        # not an empty string.
        $tid = $null
        try {
            if (-not $ready) { throw "host not serving" }
            $tid = (Invoke-RestMethod -Method Post -Uri "http://localhost:$Port/api/conversations" `
                    -ContentType "application/json" -TimeoutSec 30 `
                    -Body '{"workspaceId":"default","providerId":"test","modeId":"default"}').threadId
            Invoke-RestMethod -Method Post -Uri "http://localhost:$Port/api/conversations/$tid/messages" `
                -ContentType "application/json" -TimeoutSec 30 -Body '{"text":"hi"}' | Out-Null
            # 404 is AMBIGUOUS. It is what a collaboration-OFF host answers, and it is equally
            # what a collaboration-ON host answers for a thread whose run has not attached
            # collaboration state yet. Reading once after a fixed sleep therefore reads a
            # race - survivable while this was a warning, NOT survivable now that a 404 fails
            # the restart and the watchdog reports a failed start. So poll: a 403 is a
            # definite attach and ends it early, and only a 404 that persists for the whole
            # budget is reported as collaboration off. Measured 2026-08-24 on a healthy host:
            # 403 on the first read, ~2s after the message POST.
            $collabAttached = $false
            $lastCode = $null
            $probeError = $null
            while (-not (& $outOfTime)) {
                Start-Sleep -Seconds 3
                try {
                    Invoke-RestMethod -Uri "http://localhost:$Port/api/conversations/$tid/agents/nonexistent/transcript" -TimeoutSec 30 | Out-Null
                    $lastCode = 200
                    break
                }
                catch {
                    # Read the status defensively for the same reason as the in-flight guard
                    # above: a transport failure carries no Response at all, and under
                    # StrictMode reaching through a null one throws instead of yielding $null.
                    $lastCode = $null
                    $probeError = $_.Exception.Message
                    if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
                        $lastCode = [int]$_.Exception.Response.StatusCode
                    }
                    if ($lastCode -eq 403) { $collabAttached = $true; break }
                    # Anything other than the ambiguous 404 is already conclusive.
                    if ($lastCode -ne 404) { break }
                }
            }

            if ($collabAttached) { Write-Host "collaboration ATTACHED (403 unknown_target)" }
            elseif ($lastCode -eq 200) { $collabFailure = "probe returned 200 for a nonexistent agent - the 403/404 signal no longer distinguishes anything" }
            elseif ($lastCode -eq 404) { $collabFailure = "collaboration OFF (404 collaboration_unavailable for the whole certification budget) - the AgentCollaboration__Enabled override did not bind" }
            elseif ($null -eq $lastCode) { $collabFailure = "probe could not reach the host: $probeError" }
            else { $collabFailure = "probe returned unexpected HTTP $lastCode" }
        }
        # Do not overwrite a more specific diagnosis: when the readiness wait above already
        # explained why the probe could not run, the rethrow that lands here carries only its
        # own placeholder message.
        catch { if (-not $collabFailure) { $collabFailure = "probe failed before it could read a transcript: $($_.Exception.Message)" } }

        # Clean up after the probe. It is a MUTATING workflow used as a capability check: it
        # creates a real conversation and starts a real run, so without this every restart -
        # and the watchdog restarts on its own schedule - leaves another user-visible thread
        # behind. Eight had already accumulated on :5051 before this was added, and they are
        # not inert: the in-flight guard at the top of this script interrogates every
        # conversation the host reports, so probe litter makes each subsequent restart slower
        # and gives it more chances to hit an unreadable thread and refuse.
        #
        # Best-effort by design, and BEFORE the certification verdict below: a cleanup failure
        # must not change whether the restart is certified, and the throw must not skip it.
        if ($tid) {
            try {
                Invoke-RestMethod -Method Delete -Uri "http://localhost:$Port/api/conversations/$tid" -TimeoutSec 15 | Out-Null
                Write-Host "probe conversation $tid deleted"
            }
            catch { Write-Warning "could not delete probe conversation ${tid}: $($_.Exception.Message)" }
        }

        if ($collabFailure) {
            Write-Warning "collaboration probe: $collabFailure"
            if (-not $AllowCollaborationOff) {
                throw "Refusing to certify the restart: $collabFailure. The host on :$Port is running but its A2A capability is unproven, so reviews may complete with no sub-agents. Re-run with -AllowCollaborationOff to accept it anyway."
            }
            Write-Warning "-AllowCollaborationOff given; accepting an uncertified host."
        }

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
}

if (-not $listening) {
    throw "host never LISTENED on $Port within the ${CertifyTimeoutSeconds}s certification budget (pid $($p.Id)); see $run\review-host-$Port.err.log"
}
