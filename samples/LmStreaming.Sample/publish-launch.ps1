#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build the Vue client, build the server, and launch LmStreaming.Sample.

.DESCRIPTION
    A repeatable dev-style build-and-launch for the sample. It runs in three phases:

      1. Build client : `dotnet build -p:BuildClientApp=true` runs the MSBuild
                        BuildClientApp target -> `npm ci` (only when node_modules is
                        missing) + `npm run build`, so the Vue SPA and its deps are in
                        place. This also compiles the server.
      2. (Publish)    : there is no separate publish step in dev mode -- the built
                        client output (wwwroot/dist) IS the published client. In the
                        Development environment the app auto-runs the Vite dev server and
                        serves the SPA live; the built dist is the static fallback.
      3. Launch       : `dotnet run --no-build` (reuses phase-1 binaries) in the
                        Development environment.

    Configuration comes from the sample's git-ignored `.env` (port 5050 + the remote
    sandbox gateway at 192.168.11.139). This script pins a few values on the COMMAND LINE
    on purpose -- command-line args outrank both `.env` and appsettings in ASP.NET's
    configuration precedence, so the port, workspace, and webhook host are deterministic no
    matter what `.env` contains:

      * --urls http://0.0.0.0:<Port>       -> the port the script owns (default 5050).
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

.PARAMETER Port
    TCP port to bind Kestrel on. Default 5050.

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
    Launch even if the port is already in use (otherwise the script stops so you don't
    stack a second instance).

.EXAMPLE
    ./publish-launch.ps1
    Build the client + server and launch on http://localhost:5050 against the remote gateway.

.EXAMPLE
    ./publish-launch.ps1 -Port 5060 -SkipClientBuild
    Fast relaunch on 5060 without rebuilding the Vue client.
#>
[CmdletBinding()]
param(
    [int]    $Port           = 5050,
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

if (-not (Test-Path $ProjectFile)) {
    throw "Could not find LmStreaming.Sample.csproj next to this script ($ProjectFile)."
}

function Write-Phase([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# There is no launchSettings.json, and IsDevelopment() (which drives Vite auto-run + the
# SPA redirect) is decided at host-build time. Force Development explicitly so the launch
# does not depend on .env load ordering.
$env:ASPNETCORE_ENVIRONMENT = 'Development'

# --- Pre-flight: refuse to stack a second instance on an in-use port ------------------
$inUse = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
if ($inUse -and -not $Force) {
    $pids = ($inUse | Select-Object -ExpandProperty OwningProcess -Unique) -join ', '
    throw "Port $Port is already in use (PID(s): $pids). Stop it first, choose another -Port, or pass -Force."
}

# --- Phase 1: build the Vue client + the server ---------------------------------------
if ($SkipClientBuild) {
    Write-Phase "Build (server only; skipping client build) [$Configuration]"
    dotnet build $ProjectFile -c $Configuration
} else {
    Write-Phase "Build client + server (npm ci/build via BuildClientApp) [$Configuration]"
    dotnet build $ProjectFile -c $Configuration -p:BuildClientApp=true
}
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

# --- Phase 2/3: launch (reuse the phase-1 build; do not compile again) -----------------
Write-Phase "Launch on http://localhost:$Port  (Development; gateway from .env)"
Write-Host  "    Open:    http://localhost:$Port" -ForegroundColor Green
Write-Host  "    Webhook: $WebhookBaseUrl" -ForegroundColor DarkGray
Write-Host  "    Stop:    Ctrl+C" -ForegroundColor DarkGray
Write-Host  ''

# Command-line args (after --) reach WebApplication.CreateBuilder(args) and outrank
# .env / appsettings: they own the port, neutralize the stale WorkspaceBasePath, and pin the
# webhook host (a hostname Traefik has a SAN-matching cert for -- a bare IP 502s every webhook).
dotnet run --project $ProjectFile -c $Configuration --no-build -- `
    --urls "http://0.0.0.0:$Port" `
    --SandboxGateway:WorkspaceBasePath= `
    --Auth:Webhook:PublicBaseUrl="$WebhookBaseUrl"

exit $LASTEXITCODE
