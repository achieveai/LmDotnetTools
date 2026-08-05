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
                        server (`npm run dev`) and the backend (`dotnet run --no-build`)
                        as tracked child processes -- not just the backend in the
                        foreground. `VITE_AUTO_RUN=false` is set so `Vite.AspNetCore`
                        does not ALSO try to spawn its own `npm run dev` (that would be a
                        second supervisor racing this script for the same port).

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
    notify-waits.db) are UNCHANGED by this script: they remain rooted at the build output
    directory and keyed by -Configuration, not by port. Two instances launched with the
    same -Configuration still share that state -- this is a pre-existing characteristic,
    not something this script isolates; it is simply recorded in the run-state file below
    so it isn't a surprise.

    Each launch writes a side-car run-state file at
    `.run/instance-<resolvedBackendPort>.json` (git-ignored) describing the checkout,
    branch, commit, both resolved ports, and both process IDs, and removes it again on
    shutdown. The file exists purely for operator visibility (e.g. `cat` it to see what a
    background instance is doing); nothing in this script reads it back.

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
    relaunch when ClientApp/ has not changed (node_modules must already exist).

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
function Test-PortFree {
    param(
        [Parameter(Mandatory)] [int] $Port,
        [Parameter(Mandatory)] [System.Net.IPAddress] $Address
    )
    try {
        $listener = [System.Net.Sockets.TcpListener]::new($Address, $Port)
        $listener.Start()
        $listener.Stop()
        return $true
    } catch [System.Net.Sockets.SocketException] {
        return $false
    }
}

# Applies the port-resolution rules documented above: an explicitly-passed port that is
# busy fails fast (unless -Force); an omitted (default) port silently scans forward for
# the next free one. Returns @{ Port; AutoSelected }.
function Resolve-InstancePort {
    param(
        [Parameter(Mandatory)] [int]    $Preferred,
        [Parameter(Mandatory)] [bool]   $WasExplicit,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [System.Net.IPAddress] $Address,
        [int[]]  $Reserved = @(),
        [switch] $Force,
        [int]    $ScanLimit = 25
    )

    function Test-Candidate([int] $CandidatePort) {
        ($Reserved -notcontains $CandidatePort) -and (Test-PortFree -Port $CandidatePort -Address $Address)
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
function Wait-HttpReady {
    param(
        [Parameter(Mandatory)] [string] $Uri,
        [int] $TimeoutSeconds = 20,
        [int] $DelayMs = 300
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
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

# Best-effort Windows process-TREE kill: `dotnet run` spawns an apphost child and
# `npm run dev` spawns a node/vite child, so a plain Stop-Process on just the PID we
# captured would orphan those children holding the port open. Only ever called with a PID
# this invocation itself captured via Start-Process -PassThru.
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

$backend = Resolve-InstancePort -Preferred $Port -WasExplicit $backendPortExplicit -Label 'Backend' `
    -Address ([System.Net.IPAddress]::Any) -Reserved @() -Force:$Force
$vite = Resolve-InstancePort -Preferred $VitePort -WasExplicit $vitePortExplicit -Label 'Vite' `
    -Address ([System.Net.IPAddress]::Loopback) -Reserved @($backend.Port) -Force:$Force

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

Write-Phase "Start Vite dev server on http://localhost:$($vite.Port)"
# Windows has no bare `npm.exe`; only `npm.cmd`/`npm.ps1` exist, and Start-Process
# -NoNewWindow forces UseShellExecute=$false, which does NOT do the PATHEXT resolution a
# shell would -- so `npm.cmd` must be resolved explicitly.
$npmCmd = (Get-Command npm.cmd -ErrorAction SilentlyContinue).Source
if (-not $npmCmd) {
    throw 'Could not resolve npm.cmd on PATH. Install Node.js (npm) and re-run.'
}
$viteProcess = Start-Process -FilePath $npmCmd -ArgumentList 'run', 'dev' `
    -WorkingDirectory $ClientAppDir -NoNewWindow -PassThru

if (-not (Wait-HttpReady -Uri "http://localhost:$($vite.Port)/")) {
    Write-Host '    Warning: Vite dev server did not respond within the timeout; continuing anyway (it may still be starting on a cold cache).' -ForegroundColor Yellow
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

if (-not (Wait-HttpReady -Uri "http://localhost:$($backend.Port)/api/providers")) {
    Write-Host '    Warning: backend did not respond within the timeout; check for startup errors above.' -ForegroundColor Yellow
}

# --- Run-state side-car (operator visibility only; nothing reads this back) -----------
$runStatePath = Join-Path $RunDir "instance-$($backend.Port).json"
$checkoutRoot = (git -C $ProjectDir rev-parse --show-toplevel).Trim()
$branch       = (git -C $ProjectDir rev-parse --abbrev-ref HEAD).Trim()
$commit       = (git -C $ProjectDir rev-parse HEAD).Trim()
$commitShort  = (git -C $ProjectDir rev-parse --short HEAD).Trim()

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
    note           = 'State paths (conversations/, chat-modes/, workflow-index/, logs/, oauth-tokens/, notify-waits.db) are UNCHANGED by this script: rooted at the build output dir, keyed by configuration (not port). Two same-configuration instances still share that state.'
}
Write-RunStateFile -Path $runStatePath -State $runState

Write-Phase 'Ready'
Write-Host "    Checkout: $checkoutRoot" -ForegroundColor DarkGray
Write-Host "    Branch:   $branch" -ForegroundColor DarkGray
Write-Host "    Commit:   $commitShort" -ForegroundColor DarkGray
Write-Host "    Backend:  http://localhost:$($backend.Port)  (PID $($backendProcess.Id))" -ForegroundColor Green
Write-Host "    Vite:     http://localhost:$($vite.Port)  (PID $($viteProcess.Id))" -ForegroundColor Green
Write-Host "    Webhook:  $WebhookBaseUrl" -ForegroundColor DarkGray
Write-Host "    State paths (conversations/chat-modes/logs/etc.) are unchanged -- see .run/instance-$($backend.Port).json" -ForegroundColor DarkGray
Write-Host '    Stop:     Ctrl+C' -ForegroundColor DarkGray
Write-Host ''

try {
    $backendProcess.WaitForExit()
} finally {
    Write-Phase 'Shutting down'
    Stop-OwnedProcessTree -ProcessId $viteProcess.Id -Label 'Vite'
    Stop-OwnedProcessTree -ProcessId $backendProcess.Id -Label 'Backend'
    Remove-RunStateFile -Path $runStatePath
}

exit $LASTEXITCODE
