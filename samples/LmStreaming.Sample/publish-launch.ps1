#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build the Vue client, build the server, and launch a paired backend + Vite dev-server
    instance of LmStreaming.Sample with a single command.

.DESCRIPTION
    A repeatable dev-style build-and-launch for the sample. It runs in three phases:

      1. Build client : `dotnet build -p:BuildClientApp=true` runs the MSBuild
                        BuildClientApp target -> `npm ci` (only when node_modules is
                        missing) + `npm run build`, so the Vue SPA and its deps are in
                        place. This also compiles the server.
      2. Resolve ports: the backend and Vite ports are each resolved independently (see
                        -Port / -VitePort below) and reserved against each other so the
                        two never collide.
      3. Launch       : the script starts and directly supervises BOTH the Vite dev
                        server and the backend (`dotnet run --no-build`) as tracked child
                        processes -- not just the backend in the foreground. Vite is
                        launched via `node node_modules/vite/bin/vite.js --strictPort`
                        directly (NOT `npm run dev`): the npm wrapper is two process hops
                        above the real Node/Vite listener, so the PID it returns can keep
                        "existing" after the actual listener (and port) is gone, or vice
                        versa -- this direct invocation makes the recorded PID the one
                        genuinely holding the socket, and `--strictPort` makes Vite refuse
                        to silently rebind if the port turned out to be taken after all.
                        `VITE_AUTO_RUN=false` is set so `Vite.AspNetCore` does not ALSO
                        try to spawn its own dev server (that would be a second supervisor
                        racing this script for the same port).

    This lets you run multiple isolated instances side by side, e.g.:
        ./publish-launch.ps1                                   # backend 5050, Vite 5173
        ./publish-launch.ps1 -Port 5060 -VitePort 5183         # a second, explicit pair

    Port resolution rules:
      * If -Port / -VitePort is OMITTED, its default (5050 / 5173) is tried first; if
        busy, the script scans forward for the next free port and uses that instead
        (auto-fallback). This is what lets a second invocation "just work" alongside a
        first one without any flags.
      * If -Port / -VitePort is EXPLICITLY passed and busy, the script FAILS (so you
        don't accidentally stack a second instance on top of one you meant to reuse),
        unless -Force is also passed, in which case it proceeds anyway.

    Configuration comes from the sample's git-ignored `.env` (port 5050 + the remote
    sandbox gateway at 192.168.11.139). This script pins a few values on the COMMAND LINE
    on purpose -- command-line args outrank both `.env` and appsettings in ASP.NET's
    configuration precedence, so the port, workspace, and webhook host are deterministic no
    matter what `.env` contains:

      * --urls http://0.0.0.0:<resolved backend port> -> the port this invocation owns.
      * --SandboxGateway:WorkspaceBasePath= -> emptied. The `.env` ships a macOS base path
        (/Volumes/...) that is meaningless on this Windows box AND wrong for a REMOTE
        gateway; emptying it makes the app forward just the workspace leaf ("demo") and
        lets the remote gateway own the directory.
      * --Auth:Webhook:PublicBaseUrl=<WebhookBaseUrl> -> the HTTPS base URL the gateway
        calls back for egress/auth/discovery webhooks. MUST be a HOSTNAME, not a bare IP:
        Traefik (host :4543 -> container :443) routes TLS by SNI and only presents the
        router's real cert when the client sends a matching server name. A bare-IP URL
        carries no SNI, so Traefik serves its self-signed DEFAULT cert (SAN=*.traefik.default,
        no IP SAN); the gateway's insecure-TLS bypass waives chain-of-trust but STILL enforces
        SAN/hostname matching, so verification fails and every webhook 502s ("temporarily
        unable to evaluate egress policy"). `lmstreaming.bhakars.internal` is a host Traefik
        has a cert for and is already in the gateway's trusted + insecure-TLS host lists.

    State paths (conversations/, chat-modes/, workflow-index/, logs/, oauth-tokens/,
    notify-waits.db) are rooted at the build output directory and keyed by -Configuration,
    not by port -- two instances launched with the same -Configuration still share that
    state; this is a pre-existing characteristic, not something this script isolates. The
    run-state file below now records the actual resolved output directory and each of
    these paths so this is verifiable rather than a narrative claim.

    Each launch writes a side-car run-state file at
    `.run/instance-<resolvedBackendPort>.json` (git-ignored) describing the checkout,
    branch, commit, both resolved ports, both process IDs, and the resolved state-store
    paths, and removes it again on shutdown. The file exists purely for operator
    visibility (e.g. `cat` it to see what a background instance is doing); nothing in
    this script reads it back.

.PARAMETER Port
    TCP port to bind Kestrel on. Default 5050. Auto-fallback applies only when this
    parameter is omitted (see port resolution rules above).

.PARAMETER VitePort
    TCP port for the Vite dev server. Default 5173. Auto-fallback applies only when this
    parameter is omitted (see port resolution rules above).

.PARAMETER Configuration
    Build configuration (Debug or Release). Default Debug (dev-style).

.PARAMETER WebhookBaseUrl
    HTTPS base URL advertised to the sandbox gateway for egress/auth/discovery webhook
    callbacks. Default https://lmstreaming.bhakars.internal:4543. MUST be a hostname Traefik
    has a cert for (see the description above); a bare IP breaks TLS SAN verification and
    every webhook 502s.

.PARAMETER SkipClientBuild
    Skip phase 1's client build and just rebuild/launch the server. Use for a fast
    relaunch when ClientApp/ has not changed (node_modules must already exist). Regardless
    of this flag, phase 2 always checks whether node_modules is STALE relative to
    package-lock.json (comparing it against node_modules/.package-lock.json, the marker npm
    itself writes on install) and re-runs `npm ci` if so -- installed-but-stale is not the
    same as missing, and a stale install starts Vite successfully while individual module
    imports fail later in the browser (see phase 2 comments).

.PARAMETER Force
    Launch even if an explicitly-passed -Port or -VitePort is already in use (otherwise
    the script stops so you don't stack a second instance on a port you meant to reuse).
    Has no effect on auto-fallback ports (those already skip busy ports on their own).

.EXAMPLE
    ./publish-launch.ps1
    Build the client + server and launch backend on http://localhost:5050 and Vite on
    http://localhost:5173 against the remote gateway.

.EXAMPLE
    ./publish-launch.ps1 -Port 5060 -VitePort 5183 -SkipClientBuild
    Fast relaunch of a second, explicit instance without rebuilding the Vue client.
#>
[CmdletBinding()]
param(
    [int]    $Port           = 5050,
    [int]    $VitePort       = 5173,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration  = 'Debug',
    [string] $WebhookBaseUrl = 'https://lmstreaming.bhakars.internal',
    [switch] $SkipClientBuild,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolve paths from the script location so this works from any working directory.
$ProjectDir  = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir 'LmStreaming.Sample.csproj'
$ClientAppDir = Join-Path $ProjectDir 'ClientApp'
$RunDir      = Join-Path $ProjectDir '.run'

if (-not (Test-Path $ProjectFile)) {
    throw "Could not find LmStreaming.Sample.csproj next to this script ($ProjectFile)."
}

function Write-Phase([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# --- Port helpers -----------------------------------------------------------------------

# Authoritative "can I bind this port" check (matches Program.cs's IsPortAvailable
# convention): a real bind+release, not a Get-NetTCPConnection snapshot (which only tells
# you something is listening RIGHT NOW, not whether a bind would succeed).
#
# Checks EVERY address supplied, not just one family: a listener bound only to IPv6 loopback
# (::1) is invisible to a probe that only tries IPv4 loopback (127.0.0.1) and vice versa --
# this is exactly how a leaked/orphaned Vite process was missed by a prior version of this
# script (it only probed 127.0.0.1, while the orphan was listening on ::1). The port only
# counts as free if a bind+release succeeds on ALL of them.
function Test-PortFree {
    param(
        [Parameter(Mandatory)] [int] $Port,
        [Parameter(Mandatory)] [System.Net.IPAddress[]] $Addresses
    )
    foreach ($address in $Addresses) {
        try {
            $listener = [System.Net.Sockets.TcpListener]::new($address, $Port)
            $listener.Start()
            $listener.Stop()
        } catch [System.Net.Sockets.SocketException] {
            return $false
        }
    }
    return $true
}

# Applies the port-resolution rules documented above: an explicitly-passed port that is
# busy fails fast (unless -Force); an omitted (default) port silently scans forward for
# the next free one. Returns @{ Port; AutoSelected }.
function Resolve-InstancePort {
    param(
        [Parameter(Mandatory)] [int]    $Preferred,
        [Parameter(Mandatory)] [bool]   $WasExplicit,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [System.Net.IPAddress[]] $Addresses,
        [int[]]  $Reserved = @(),
        [switch] $Force,
        [int]    $ScanLimit = 25
    )

    function Test-Candidate([int] $CandidatePort) {
        ($Reserved -notcontains $CandidatePort) -and (Test-PortFree -Port $CandidatePort -Addresses $Addresses)
    }

    if (Test-Candidate $Preferred) {
        return [pscustomobject]@{ Port = $Preferred; AutoSelected = $false }
    }

    if ($WasExplicit) {
        if (-not $Force) {
            $listeners = Get-NetTCPConnection -State Listen -LocalPort $Preferred -ErrorAction SilentlyContinue
            $pidList = if ($listeners) { ($listeners | Select-Object -ExpandProperty OwningProcess -Unique) -join ', ' } else { 'unknown' }
            throw "$Label port $Preferred is already in use (PID(s): $pidList). Stop it first, choose another port, or pass -Force."
        }
        Write-Host "    [$Label] Port $Preferred is busy; -Force set, using it anyway." -ForegroundColor Yellow
        return [pscustomobject]@{ Port = $Preferred; AutoSelected = $false }
    }

    for ($offset = 1; $offset -le $ScanLimit; $offset++) {
        $candidate = $Preferred + $offset
        if (Test-Candidate $candidate) {
            Write-Host "    [$Label] Port $Preferred was busy; auto-selected $candidate instead." -ForegroundColor Yellow
            return [pscustomobject]@{ Port = $candidate; AutoSelected = $true }
        }
    }

    throw "${Label}: could not find a free port in the range $Preferred..$($Preferred + $ScanLimit)."
}

# Polls an HTTP endpoint until it responds or the timeout elapses. Never throws -- the
# caller decides whether a timeout is fatal. Any HTTP-level response (even a 4xx/5xx)
# counts as "ready" (the listener is up); only a connection-level failure keeps polling.
#
# If -Process is supplied and it has already exited, returns $false immediately instead of
# polling out the full timeout -- a dead process can never become ready, and waiting the full
# window just delays surfacing the real error (e.g. a TOCTOU port collision that made
# `vite --strictPort` refuse to start). Callers use $Process.HasExited afterwards to tell
# "died" (fatal) apart from "just slow to start" (soft-warn), see call sites below.
function Wait-HttpReady {
    param(
        [Parameter(Mandatory)] [string] $Uri,
        [System.Diagnostics.Process] $Process,
        [int] $TimeoutSeconds = 20,
        [int] $DelayMs = 300
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if ($Process -and $Process.HasExited) {
            return $false
        }
        try {
            Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop | Out-Null
            return $true
        } catch {
            $isConnectionRefused =
                $_.Exception -is [System.Net.Sockets.SocketException] -or
                $_.Exception.InnerException -is [System.Net.Sockets.SocketException]
            if (-not $isConnectionRefused) {
                return $true
            }
        }
        Start-Sleep -Milliseconds $DelayMs
    } while ((Get-Date) -lt $deadline)
    return $false
}

# Proves the Vite dev server reached through $VitePort is genuinely proxying to a live
# backend, not just answering its own bare "/" -- i.e. that VITE_BACKEND_ORIGIN was wired to
# THIS launch's backend. Requires an actual 200 (unlike Wait-HttpReady, which counts any
# HTTP-level response as ready); a proxy failure surfaces as a 502/504 from Vite, which must
# NOT count as "ready" here.
function Test-ViteProxyReachesBackend {
    param(
        [Parameter(Mandatory)] [int] $VitePort,
        [int] $TimeoutSeconds = 20,
        [int] $DelayMs = 300
    )
    $uri = "http://localhost:$VitePort/api/providers"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
            if ($response.StatusCode -eq 200) {
                return $true
            }
        } catch {
            # Keep polling -- the backend or the proxy target may still be starting up.
        }
        Start-Sleep -Milliseconds $DelayMs
    } while ((Get-Date) -lt $deadline)
    return $false
}

# Detects a STALE ClientApp/node_modules -- installed, but no longer matching
# package-lock.json (e.g. a dependency was added/bumped upstream after node_modules was last
# installed here). This is a genuinely different condition from "missing": Vite's dev server
# starts up and answers "/" successfully either way, so neither -SkipClientBuild's existing
# "node_modules must already exist" contract nor Wait-HttpReady catches it -- the failure only
# surfaces later as an "unresolved import" from the BROWSER's first request for the real app
# module (see Test-ViteEntryModuleResolves below). `npm ci` itself writes/updates
# node_modules/.package-lock.json to record exactly what it installed; comparing its timestamp
# against package-lock.json's is the same staleness signal `npm ci` relies on, so it costs one
# file-timestamp comparison rather than re-running `npm ci` speculatively on every launch.
function Test-ClientDependenciesFresh {
    param([Parameter(Mandatory)] [string] $ClientAppDir)
    $lockFile = Join-Path $ClientAppDir 'package-lock.json'
    if (-not (Test-Path $lockFile)) {
        # No lock file to compare against -- nothing this check can verify; leave the existing
        # "node_modules must already exist" contract as the only guard in that case.
        return $true
    }
    $installMarker = Join-Path $ClientAppDir 'node_modules/.package-lock.json'
    if (-not (Test-Path $installMarker)) {
        return $false
    }
    return (Get-Item $lockFile).LastWriteTimeUtc -le (Get-Item $installMarker).LastWriteTimeUtc
}

# Proves the Vite dev server can actually resolve the real app's entry module, not merely that
# it answers its bare "/" -- Vite transforms/resolves modules ON REQUEST (unlike a production
# build, which statically resolves the whole import graph up front and would fail loudly at
# build time), so an unresolved import (a dependency the lock file references but that isn't
# actually installed) does not fail startup or the "/" response; Vite's import-analysis
# transform only throws (500) when something actually requests the entry module and tries to
# rewrite its import specifiers. Requesting it here reproduces that failure server-side instead
# of leaving it for the first browser tab to discover. The path is prefixed with /dist/ to match
# vite.config.ts's `base: '/dist/'` (confirmed live: with that base set, Vite 404s a bare
# /src/main.ts and only serves it under /dist/src/main.ts -- "/" itself still answers 200
# regardless of base, which is exactly why root-only readiness misses this).
function Test-ViteEntryModuleResolves {
    param(
        [Parameter(Mandatory)] [int] $VitePort,
        [int] $TimeoutSeconds = 20,
        [int] $DelayMs = 300
    )
    $uri = "http://localhost:$VitePort/dist/src/main.ts"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
            if ($response.StatusCode -eq 200) {
                return $true
            }
        } catch {
            # An unresolved-import 500 lands here too (Invoke-WebRequest throws on non-2xx); keep
            # polling in case Vite is still mid-startup, but a failure that persists past the
            # timeout is a real broken-module-graph condition, not a transient one.
        }
        Start-Sleep -Milliseconds $DelayMs
    } while ((Get-Date) -lt $deadline)
    return $false
}

# Resolves the actual build output directory (what AppContext.BaseDirectory will be at
# runtime) by locating the built assembly under bin/<Configuration>/, rather than hardcoding
# the TFM a second time (it already lives in the .csproj) -- avoids the two going stale
# relative to each other if the TFM ever changes.
function Get-BuildOutputDirectory {
    param(
        [Parameter(Mandatory)] [string] $ProjectDir,
        [Parameter(Mandatory)] [string] $Configuration
    )
    $searchRoot = Join-Path $ProjectDir "bin/$Configuration"
    if (-not (Test-Path $searchRoot)) {
        return $null
    }
    $dll = Get-ChildItem -Path $searchRoot -Recurse -Filter 'LmStreaming.Sample.dll' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $dll) {
        return $null
    }
    return $dll.DirectoryName
}

# Best-effort Windows process-TREE kill: `dotnet run` spawns an apphost child, so a plain
# Stop-Process on just the PID we captured would orphan that child holding the port open.
# (Vite is launched directly via `node vite.js` -- a single process, no wrapper hop -- but
# tree-killing it too is harmless and keeps this one helper uniform for both.) Only ever
# called with a PID this invocation itself captured via Start-Process -PassThru.
function Stop-OwnedProcessTree {
    param(
        [Parameter(Mandatory)] [int]    $ProcessId,
        [Parameter(Mandatory)] [string] $Label
    )
    try {
        & taskkill /PID $ProcessId /T /F 2>&1 | Out-Null
    } catch {
        # Already exited is not a failure -- nothing left to clean up.
    }
}

function Write-RunStateFile {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] $State
    )
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    $State | ConvertTo-Json -Depth 6 | Set-Content -Path $Path -Encoding utf8
}

function Remove-RunStateFile {
    param([Parameter(Mandatory)] [string] $Path)
    if (Test-Path $Path) {
        Remove-Item -Path $Path -Force -ErrorAction SilentlyContinue
    }
}

# There is no launchSettings.json, and IsDevelopment() (which drives the SPA redirect and
# the Vite dev-server proxying) is decided at host-build time. Force Development explicitly
# so the launch does not depend on .env load ordering.
$env:ASPNETCORE_ENVIRONMENT = 'Development'

# --- Resolve ports (backend first, then Vite reserved against it) ---------------------
Write-Phase 'Resolve ports'
$backendPortExplicit = $PSBoundParameters.ContainsKey('Port')
$vitePortExplicit    = $PSBoundParameters.ContainsKey('VitePort')

# Every check below probes ALL FOUR addresses (wildcard v4/v6 + loopback v4/v6), not just the
# ones this script's own process binds to. A wildcard (0.0.0.0) bind can succeed on Windows
# even while a DIFFERENT process already holds that same port on a specific loopback address
# (127.0.0.1 / ::1) -- confirmed live: a pre-existing backend bound only to 127.0.0.1/::1 left
# 0.0.0.0 "free", so a wildcard-only probe reported the port available, and Kestrel's own
# wildcard bind then succeeded too, leaving two processes ambiguously listening on the same
# port. Checking every family/scope combination closes that gap for both roles.
$allAddressFamilies = @(
    [System.Net.IPAddress]::Any,
    [System.Net.IPAddress]::IPv6Any,
    [System.Net.IPAddress]::Loopback,
    [System.Net.IPAddress]::IPv6Loopback)

$backend = Resolve-InstancePort -Preferred $Port -WasExplicit $backendPortExplicit -Label 'Backend' `
    -Addresses $allAddressFamilies -Reserved @() -Force:$Force
$vite = Resolve-InstancePort -Preferred $VitePort -WasExplicit $vitePortExplicit -Label 'Vite' `
    -Addresses $allAddressFamilies -Reserved @($backend.Port) -Force:$Force

Write-Host "    Backend port: $($backend.Port)$(if ($backend.AutoSelected) { ' (auto-selected)' })" -ForegroundColor DarkGray
Write-Host "    Vite port:    $($vite.Port)$(if ($vite.AutoSelected) { ' (auto-selected)' })" -ForegroundColor DarkGray

# --- Phase 1: build the Vue client + the server ---------------------------------------
if ($SkipClientBuild) {
    Write-Phase "Build (server only; skipping client build) [$Configuration]"
    dotnet build $ProjectFile -c $Configuration
} else {
    Write-Phase "Build client + server (npm ci/build via BuildClientApp) [$Configuration]"
    dotnet build $ProjectFile -c $Configuration -p:BuildClientApp=true
}
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

# --- Phase 2: launch and supervise Vite + backend --------------------------------------
# VITE_DEV_PORT / VITE_BACKEND_ORIGIN pair the Vue dev server with this invocation's
# resolved ports (vite.config.ts reads both). VITE_AUTO_RUN=false stops Vite.AspNetCore
# from ALSO spawning its own `npm run dev` -- this script is the one and only supervisor.
$env:VITE_DEV_PORT       = $vite.Port
$env:VITE_BACKEND_ORIGIN = "http://localhost:$($backend.Port)"
$env:VITE_AUTO_RUN       = 'false'

# Resolve everything fallible (git metadata, build-output paths) BEFORE either child process
# starts, so a failure here can't leak an already-running, un-cleaned-up Vite/backend -- the
# try/finally below wraps every step from here on, including these two Start-Process calls.
$runStatePath = Join-Path $RunDir "instance-$($backend.Port).json"
$checkoutRoot = (git -C $ProjectDir rev-parse --show-toplevel).Trim()
$branch       = (git -C $ProjectDir rev-parse --abbrev-ref HEAD).Trim()
$commit       = (git -C $ProjectDir rev-parse HEAD).Trim()
$commitShort  = (git -C $ProjectDir rev-parse --short HEAD).Trim()

# Concrete, resolved state-store paths (mirrors Program.cs's own
# Path.Combine(AppContext.BaseDirectory, ...) calls exactly) rather than a narrative
# description -- lets an operator see whether two instances are actually sharing state (same
# -Configuration = same output dir = shared store).
$buildOutputDir = Get-BuildOutputDirectory -ProjectDir $ProjectDir -Configuration $Configuration
$statePaths = if ($buildOutputDir) {
    [pscustomobject]@{
        outputDirectory = $buildOutputDir
        conversations   = Join-Path $buildOutputDir 'conversations'
        chatModes       = Join-Path $buildOutputDir 'chat-modes'
        workflowIndex   = Join-Path $buildOutputDir 'workflow-index'
        logs            = Join-Path $buildOutputDir 'logs'
        oauthTokens     = Join-Path $buildOutputDir 'oauth-tokens'
        notifyWaitsDb   = Join-Path $buildOutputDir 'notify-waits.db'
    }
} else {
    [pscustomobject]@{
        outputDirectory = "<could not locate LmStreaming.Sample.dll under bin/$Configuration -- build may have failed to produce output>"
    }
}

$viteProcess = $null
$backendProcess = $null
try {
    Write-Phase "Start Vite dev server on http://localhost:$($vite.Port)"
    # Launch Vite via `node` directly against its own bin script rather than the `npm run dev`
    # wrapper: `npm run dev` -> npm.cmd -> the `vite` shim -> the real Node/Vite process is TWO
    # hops down, so the PID this script captured (npm.cmd's) did not correspond to the process
    # actually holding the listening socket -- npm.cmd could exit while that real process (and
    # the port) lived on as an unsupervised orphan (root cause of a prior defect: a leaked
    # orphan Vite process outlived its launcher and was silently mismatched with a later one).
    # Invoking `node <vite.js>` directly collapses that to one hop: the PID captured here IS
    # the process holding the listening socket.
    $nodeCmd = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (-not $nodeCmd) {
        throw 'Could not resolve node on PATH. Install Node.js and re-run.'
    }
    $viteBin = Join-Path $ClientAppDir 'node_modules/vite/bin/vite.js'
    if (-not (Test-Path $viteBin)) {
        throw "Vite CLI not found at $viteBin. Run 'npm ci' in ClientApp (or omit -SkipClientBuild) first."
    }
    # Self-heal a STALE (as opposed to missing) node_modules -- see Test-ClientDependenciesFresh's
    # doc comment. Runs regardless of -SkipClientBuild: it is cheap (a timestamp check) when
    # nothing is stale, and it is precisely the -SkipClientBuild path (straight to the Vite dev
    # server, no `npm run build`/Rollup static import resolution to catch this first) where a
    # stale install would otherwise go undetected until a browser hit an unresolved import.
    if (-not (Test-ClientDependenciesFresh -ClientAppDir $ClientAppDir)) {
        Write-Phase 'Install client dependencies (package-lock.json changed since node_modules was last installed)'
        & npm ci --prefix $ClientAppDir
        if ($LASTEXITCODE -ne 0) {
            throw "npm ci failed (exit $LASTEXITCODE) while refreshing stale ClientApp dependencies."
        }
    }
    # --strictPort mirrors vite.config.ts's own `server.strictPort: true` -- belt and suspenders:
    # Vite must hard-fail rather than silently rebinding to a different port if this script's
    # own port-free check above lost a race (TOCTOU) with something else claiming the port.
    $viteProcess = Start-Process -FilePath $nodeCmd -ArgumentList $viteBin, '--port', $vite.Port, '--strictPort' `
        -WorkingDirectory $ClientAppDir -NoNewWindow -PassThru

    # Give a fast-failing `--strictPort` refusal a brief moment to happen before polling HTTP --
    # an immediate exit means the port was NOT actually free (our earlier check lost a race),
    # and no amount of HTTP polling will ever turn that into success.
    Start-Sleep -Milliseconds 300
    $viteReady = Wait-HttpReady -Uri "http://localhost:$($vite.Port)/" -Process $viteProcess
    if (-not $viteReady) {
        if ($viteProcess.HasExited) {
            throw "Vite dev server exited immediately (exit code $($viteProcess.ExitCode)) on port $($vite.Port) -- it likely lost a race for that port (see --strictPort) or node_modules is stale. Not proceeding with a dead Vite process."
        }
        Write-Host '    Warning: Vite dev server did not respond within the timeout; continuing anyway (it may still be starting on a cold cache).' -ForegroundColor Yellow
    }

    # Fatal (not a soft warning like the checks below): an unresolved import means the app
    # cannot load in ANY browser, not merely that this launch's wiring might be off. The
    # freshness self-heal above already handles the common case (stale node_modules); a failure
    # here means something is still genuinely broken (e.g. npm ci itself couldn't resolve a
    # dependency) and declaring "Ready" anyway would just hide that until someone opens a tab.
    if (-not (Test-ViteEntryModuleResolves -VitePort $vite.Port)) {
        throw "Vite on port $($vite.Port) could not resolve the app's entry module (/src/main.ts) -- likely an unresolved import from a dependency that failed to install. Check the 'npm ci' output above, or run it manually in ClientApp."
    }

    Write-Phase "Start backend on http://localhost:$($backend.Port)"
    # Command-line args (after --) reach WebApplication.CreateBuilder(args) and outrank
    # .env / appsettings: they own the port, neutralize the stale WorkspaceBasePath, and pin the
    # webhook host (a hostname Traefik has a SAN-matching cert for -- a bare IP 502s every webhook).
    $backendArgs = @(
        'run', '--project', $ProjectFile, '-c', $Configuration, '--no-build', '--',
        '--urls', "http://0.0.0.0:$($backend.Port)",
        '--SandboxGateway:WorkspaceBasePath=',
        "--Auth:Webhook:PublicBaseUrl=$WebhookBaseUrl"
    )
    $backendProcess = Start-Process -FilePath 'dotnet' -ArgumentList $backendArgs -NoNewWindow -PassThru

    $backendReady = Wait-HttpReady -Uri "http://localhost:$($backend.Port)/api/providers" -Process $backendProcess
    if (-not $backendReady) {
        if ($backendProcess.HasExited) {
            throw "Backend exited immediately (exit code $($backendProcess.ExitCode)) on port $($backend.Port) -- check for startup errors above. Not proceeding with a dead backend process."
        }
        Write-Host '    Warning: backend did not respond within the timeout; check for startup errors above.' -ForegroundColor Yellow
    }

    # Proves THIS Vite instance is proxying to THIS backend (not merely that some HTTP server
    # answers on the Vite port) -- the readiness gap a stale/leaked Vite instance could hide
    # behind, since it too would answer "/" but proxy to whatever backend IT was started with.
    if (-not (Test-ViteProxyReachesBackend -VitePort $vite.Port)) {
        Write-Host "    Warning: Vite on port $($vite.Port) did not successfully proxy /api to this launch's backend (port $($backend.Port)) within the timeout -- the pairing may be miswired. Check VITE_BACKEND_ORIGIN and for a stale Vite instance already holding this port." -ForegroundColor Yellow
    }

    # --- Run-state side-car (operator visibility only; nothing reads this back) -----------
    $runState = [pscustomobject]@{
        startedAtUtc  = (Get-Date).ToUniversalTime().ToString('o')
        checkout      = $checkoutRoot
        branch        = $branch
        commit        = $commit
        configuration = $Configuration
        backend       = [pscustomobject]@{
            preferredPort    = $Port
            port             = $backend.Port
            portAutoSelected = $backend.AutoSelected
            pid              = $backendProcess.Id
            url              = "http://localhost:$($backend.Port)"
        }
        vite          = [pscustomobject]@{
            preferredPort    = $VitePort
            port             = $vite.Port
            portAutoSelected = $vite.AutoSelected
            pid              = $viteProcess.Id
            url              = "http://localhost:$($vite.Port)"
        }
        webhookBaseUrl = $WebhookBaseUrl
        paths          = $statePaths
        note           = 'paths.* are keyed by -Configuration (not port): two instances launched with the same -Configuration still share that state -- this is a pre-existing characteristic, not something this script isolates.'
    }
    Write-RunStateFile -Path $runStatePath -State $runState

    Write-Phase 'Ready'
    Write-Host "    Checkout: $checkoutRoot" -ForegroundColor DarkGray
    Write-Host "    Branch:   $branch" -ForegroundColor DarkGray
    Write-Host "    Commit:   $commitShort" -ForegroundColor DarkGray
    Write-Host "    Backend:  http://localhost:$($backend.Port)  (PID $($backendProcess.Id))" -ForegroundColor Green
    Write-Host "    Vite:     http://localhost:$($vite.Port)  (PID $($viteProcess.Id), real node process)" -ForegroundColor Green
    Write-Host "    Webhook:  $WebhookBaseUrl" -ForegroundColor DarkGray
    Write-Host "    State paths: $($statePaths.outputDirectory)" -ForegroundColor DarkGray
    Write-Host "    Run-state:   .run/instance-$($backend.Port).json" -ForegroundColor DarkGray
    Write-Host '    Stop:     Ctrl+C' -ForegroundColor DarkGray
    Write-Host ''

    $backendProcess.WaitForExit()
} finally {
    Write-Phase 'Shutting down'
    if ($null -ne $viteProcess) {
        Stop-OwnedProcessTree -ProcessId $viteProcess.Id -Label 'Vite'
    }
    if ($null -ne $backendProcess) {
        Stop-OwnedProcessTree -ProcessId $backendProcess.Id -Label 'Backend'
    }
    Remove-RunStateFile -Path $runStatePath
}

exit $LASTEXITCODE
