#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build, publish, and launch a standalone Production instance of LmStreaming.Sample with a
    single command -- no Vite development server, no prior manual build.

.DESCRIPTION
    One pipeline, run unconditionally on every invocation:

        npm ci -> npm run build -> dotnet publish -> validate artifact -> launch published exe

    Phases:

      1. Resolve ports : the backend port is resolved (see -Port below).
      2. Build client   : `npm ci` then `npm run build` run directly in `ClientApp/` (NOT through
                          the project's opt-in `BuildClientApp` MSBuild target) so the Vite
                          production bundle lands in `wwwroot/dist/` before publish runs.
      3. Publish server : `dotnet publish -p:BuildClientApp=false` builds and publishes the
                          server into a fresh run directory under the repository's
                          `.claude/scratchpad/lmstreaming-standalone-publish/` -- passing
                          `BuildClientApp=false` avoids MSBuild duplicating the client build this
                          script just ran directly.
      4. Validate       : the publish directory must contain the executable, `wwwroot/dist/index.html`,
                          and every local `.js`/`.css` asset that HTML references. A validation
                          failure is fatal and identifies exactly what is missing.
      5. Launch         : only the published executable is started (never the source project directly), with `ASPNETCORE_ENVIRONMENT=Production` and
                          `DOTNET_ENVIRONMENT=Production` set for that process only (the parent
                          session's environment is left untouched). Readiness is proven, not
                          assumed: the script polls `GET /api/providers` for a success status,
                          then requires an HTTP 2xx from `/dist/index.html` and every referenced
                          JS/CSS asset on that SAME ASP.NET Core port. Any readiness failure is
                          fatal.

    This script owns the whole operation -- a caller does not run a separate build first, and
    there is no Vite dev-server mode to opt into. Developers who want hot reload use the
    repository's ordinary frontend/backend development commands directly (see this sample's
    CLAUDE.md / README.md), not this script.

    Configuration comes from the sample's git-ignored `.env` (port 5050 + the remote sandbox
    gateway at 192.168.11.139). This script pins a few values on the COMMAND LINE on purpose --
    command-line args outrank both `.env` and appsettings in ASP.NET's configuration precedence,
    so the port, workspace, and webhook host are deterministic no matter what `.env` contains:

      * --urls http://0.0.0.0:<resolved backend port> -> the port this invocation owns.
      * --SandboxGateway:WorkspaceBasePath= -> emptied. The `.env` ships a macOS base path
        (/Volumes/...) that is meaningless on this Windows box AND wrong for a REMOTE gateway;
        emptying it makes the app forward just the workspace leaf ("demo") and lets the remote
        gateway own the directory.
      * --Auth:Webhook:PublicBaseUrl=<WebhookBaseUrl> -> the HTTPS base URL the gateway calls back
        for egress/auth/discovery webhooks. MUST be a HOSTNAME, not a bare IP: Traefik (host :4543
        -> container :443) routes TLS by SNI and only presents the router's real cert when the
        client sends a matching server name. A bare-IP URL carries no SNI, so Traefik serves its
        self-signed DEFAULT cert (SAN=*.traefik.default, no IP SAN); the gateway's insecure-TLS
        bypass waives chain-of-trust but STILL enforces SAN/hostname matching, so verification
        fails and every webhook 502s ("temporarily unable to evaluate egress policy").
        `lmstreaming.bhakars.internal` is a host Traefik has a cert for and is already in the
        gateway's trusted + insecure-TLS host lists.

    The publish directory is a temporary demonstration artifact under this repository's
    `.claude/scratchpad/lmstreaming-standalone-publish/run-<UTC-yyyyMMdd-HHmmss>-<PID>/`. It
    remains on disk (untracked) after both success and failure so it can be inspected; the script
    prints its exact path. A side-car `.run-state.json` is written inside it (after all readiness
    checks pass) describing the checkout, branch, commit, configuration, publish directory, and
    resolved backend port/PID/URL -- there is no Vite port, PID, or URL to record.

.PARAMETER Port
    TCP port to bind Kestrel on. Default 5050.

    Port resolution rules:
      * If -Port is OMITTED, its default (5050) is tried first; if busy, the script scans forward
        for the next free port and uses that instead (auto-fallback).
      * If -Port is EXPLICITLY passed and busy, the script FAILS (so you don't accidentally stack
        a second instance on top of one you meant to reuse), unless -Force is also passed, in
        which case it proceeds anyway.

.PARAMETER Configuration
    Build/publish configuration (Debug or Release). Default Debug.

.PARAMETER WebhookBaseUrl
    HTTPS base URL advertised to the sandbox gateway for egress/auth/discovery webhook callbacks.
    Default https://lmstreaming.bhakars.internal. MUST be a hostname Traefik has a cert for (see
    the description above); a bare IP breaks TLS SAN verification and every webhook 502s.

.PARAMETER Force
    Launch even if an explicitly-passed -Port is already in use (otherwise the script stops so
    you don't stack a second instance on a port you meant to reuse). Has no effect on the
    auto-fallback port (that already skips busy ports on its own). See -Port above for the full
    port-resolution rules.

.EXAMPLE
    ./publish-launch.ps1
    Build the client, publish the server, and launch the standalone Production artifact on
    http://localhost:5050 against the remote gateway.

.EXAMPLE
    ./publish-launch.ps1 -Configuration Release -Port 5060
    Publish a Release artifact and launch it on an explicit port.
#>
[CmdletBinding()]
param(
    [int]    $Port           = 5050,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration  = 'Debug',
    [string] $WebhookBaseUrl = 'https://lmstreaming.bhakars.internal',
    [switch] $Force,
    [string] $DestinationDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Captured HERE, at true script scope, immediately after the script's own param() block --
# $PSBoundParameters at this point reflects exactly what THIS invocation bound on its real command
# line. Invoke-Main (below) declares no parameters of its own on purpose (it is called with zero
# arguments today) but that means ITS internal $PSBoundParameters is a different, always-empty
# object -- reading $PSBoundParameters inside a function only ever reflects that function's own
# bound parameters, never the enclosing script's. These two booleans are the one true record of
# "was this explicitly passed on the command line" and must be threaded into Invoke-Main as
# explicit arguments rather than recomputed (incorrectly) from inside it.
$script:DestinationDirectoryWasExplicit = $PSBoundParameters.ContainsKey('DestinationDirectory')
$script:PortWasExplicit = $PSBoundParameters.ContainsKey('Port')

function Write-Phase([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# --- Port helpers -----------------------------------------------------------------------

# Authoritative "can I bind this port" check (matches Program.cs's IsPortAvailable convention): a
# real bind+release, not a Get-NetTCPConnection snapshot (which only tells you something is
# listening RIGHT NOW, not whether a bind would succeed).
#
# Checks EVERY address supplied, not just one family: a listener bound only to IPv6 loopback
# (::1) is invisible to a probe that only tries IPv4 loopback (127.0.0.1) and vice versa -- this
# is exactly how a leaked/orphaned process was missed by a prior version of this script (it only
# probed 127.0.0.1, while the orphan was listening on ::1). The port only counts as free if a
# bind+release succeeds on ALL of them.
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

# Applies the port-resolution rules documented above: an explicitly-passed port that is busy
# fails fast (unless -Force); an omitted (default) port silently scans forward for the next free
# one. Returns @{ Port; AutoSelected }.
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

# Polls an HTTP endpoint until it returns an HTTP SUCCESS status (2xx) or the timeout elapses.
# Unlike a bare "is anything listening" probe, a 4xx/5xx does NOT count as ready here -- readiness
# for the standalone pipeline means the endpoint actually works, not merely that a socket accepted
# a connection. Never throws -- the caller decides whether a timeout/process-exit is fatal (it
# always is, for this script).
function Wait-HttpReady {
    param(
        [Parameter(Mandatory)] [string] $Uri,
        [Parameter(Mandatory)] [System.Diagnostics.Process] $Process,
        [int] $TimeoutSeconds = 60,
        [int] $DelayMs = 300
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) {
            return $false
        }
        try {
            $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                return $true
            }
        } catch {
            # Keep polling: a connection refusal / non-2xx just means it isn't ready yet.
        }
        Start-Sleep -Milliseconds $DelayMs
    } while ((Get-Date) -lt $deadline)
    return $false
}

# Requires an immediate HTTP 2xx from $Uri. Used for the post-readiness asset checks, where the
# backend is already known to be up (Wait-HttpReady above already proved that) -- a non-2xx here
# is a real, immediate failure, not a "still starting" condition to poll through. Fatal by design:
# throws with the URI and the observed status/error rather than returning a bool, so a caller can
# never accidentally downgrade this to a warning.
function Confirm-HttpSuccess {
    param([Parameter(Mandatory)] [string] $Uri)
    try {
        $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    } catch {
        $status = if ($_.Exception.PSObject.Properties['Response'] -and $_.Exception.Response) {
            [int]$_.Exception.Response.StatusCode
        } else {
            'no response'
        }
        throw "Readiness check failed for $Uri (status: $status): $($_.Exception.Message)"
    }
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "Readiness check failed for $Uri`: HTTP $($response.StatusCode)."
    }
}

# --- Build / publish / artifact helpers --------------------------------------------------

# Unconditional direct client build -- there is deliberately no `node_modules` freshness
# shortcut. Every invocation runs `npm ci` then `npm run build` so the published bundle always
# reflects the current ClientApp sources.
function Invoke-ClientBuild {
    param([Parameter(Mandatory)][string] $ClientAppDirectory)

    & npm ci --prefix $ClientAppDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "npm ci failed with exit code $LASTEXITCODE."
    }

    & npm run build --prefix $ClientAppDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "npm run build failed with exit code $LASTEXITCODE."
    }
}

# Creates a fresh, unique demonstration directory under the REPOSITORY ROOT's scratchpad (not the
# caller's working directory) -- .claude/scratchpad/lmstreaming-standalone-publish/run-<UTC
# timestamp>-<PID>/. This becomes the `dotnet publish` output directory and is retained after the
# run (success or failure) as the inspectable artifact.
function New-PublishRunDirectory {
    param([Parameter(Mandatory)][string] $RepositoryRoot)

    $runName = 'run-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'), $PID
    $directory = Join-Path $RepositoryRoot '.claude' 'scratchpad' 'lmstreaming-standalone-publish' $runName
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    return $directory
}

# Publishes the server directly (no `--no-build`: publish must build the server and produce a
# complete artifact by itself). `-p:BuildClientApp=false` stops MSBuild's opt-in client-build
# target from duplicating the `npm ci`/`npm run build` this script already ran in Invoke-ClientBuild
# -- the Vite bundle it already wrote to wwwroot/dist/ is what gets published.
function Invoke-ApplicationPublish {
    param(
        [Parameter(Mandatory)][string] $ProjectFile,
        [Parameter(Mandatory)][string] $Configuration,
        [Parameter(Mandatory)][string] $OutputDirectory
    )

    & dotnet publish $ProjectFile -c $Configuration -o $OutputDirectory '-p:BuildClientApp=false'
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

# Parses every local (non-remote) `.js`/`.css` reference out of the published index.html --
# accepts Vite's `/dist/...` base path and trailing query strings/fragments, rejects absolute
# remote URLs, and returns distinct app-relative paths. Does not assume any fixed filename/count
# (Vite's content hash changes on every build); fails unless at least one JavaScript reference is
# present, since CSS is not guaranteed to exist for every build.
function Get-PublishedAssetReferences {
    param([Parameter(Mandatory)][string] $IndexPath)

    $html = [System.IO.File]::ReadAllText($IndexPath)
    $matches = [regex]::Matches($html, '(?:src|href)=["''](?<path>/dist/[^"''?#]+\.(?:js|css))(?:[?#][^"'']*)?["'']')
    $assets = @($matches | ForEach-Object { $_.Groups['path'].Value } | Sort-Object -Unique)

    if (-not ($assets | Where-Object { $_ -match '\.js$' })) {
        throw "No JavaScript asset reference was found in $IndexPath."
    }

    return $assets
}

# Validates the FULL publish artifact before launch: the executable, wwwroot/dist/index.html, and
# every local JS/CSS asset that HTML references (converting each `/dist/assets/x.js` reference to
# `<publish>/wwwroot/dist/assets/x.js` and rejecting any that resolves outside wwwroot). Throws a
# focused error identifying exactly what is missing; returns the resolved paths for the launch
# and readiness phases so they never have to re-derive them.
function Confirm-PublishArtifact {
    param([Parameter(Mandatory)][string] $PublishDirectory)

    $executablePath = Join-Path $PublishDirectory 'LmStreaming.Sample.exe'
    if (-not (Test-Path $executablePath)) {
        throw "Published executable not found at $executablePath. The publish step did not produce the expected standalone artifact."
    }

    $wwwrootDirectory = Join-Path $PublishDirectory 'wwwroot'
    $indexPath = Join-Path $wwwrootDirectory (Join-Path 'dist' 'index.html')
    if (-not (Test-Path $indexPath)) {
        throw "Published SPA entry point not found at $indexPath. The Vite build output was not copied into the publish directory."
    }

    $assetReferences = Get-PublishedAssetReferences -IndexPath $indexPath

    $resolvedWwwroot = [System.IO.Path]::GetFullPath($wwwrootDirectory).TrimEnd('\', '/')
    $wwwrootBoundary = $resolvedWwwroot + [System.IO.Path]::DirectorySeparatorChar
    foreach ($asset in $assetReferences) {
        $relative = $asset.TrimStart('/') -replace '/', [System.IO.Path]::DirectorySeparatorChar
        $candidatePath = Join-Path $wwwrootDirectory $relative
        $resolvedAsset = [System.IO.Path]::GetFullPath($candidatePath)
        # Exact-root-or-boundary comparison, NOT a raw StartsWith($resolvedWwwroot): a bare
        # StartsWith lets a sibling directory that merely shares the prefix (e.g. a published
        # "<publish>/wwwroot-evil/...") pass, because "wwwroot-evil".StartsWith("wwwroot") is
        # true. Requiring either an exact match or a match followed by the OS directory separator
        # closes that gap.
        $isWithinWwwroot =
            $resolvedAsset.Equals($resolvedWwwroot, [System.StringComparison]::OrdinalIgnoreCase) -or
            $resolvedAsset.StartsWith($wwwrootBoundary, [System.StringComparison]::OrdinalIgnoreCase)
        if (-not $isWithinWwwroot) {
            throw "Published asset reference '$asset' resolves outside wwwroot ($resolvedAsset). Refusing to validate or serve it."
        }
        if (-not (Test-Path $resolvedAsset)) {
            throw "Published asset referenced by index.html was not found on disk: $resolvedAsset (referenced as $asset)."
        }
    }

    return [pscustomobject]@{
        ExecutablePath = $executablePath
        IndexPath      = $indexPath
        AssetPaths     = $assetReferences
    }
}

# Best-effort Windows process-TREE kill for the one process this script owns and supervises (the
# published executable). Only ever called with a PID this invocation itself captured at Start().
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

# --- Git metadata helpers ----------------------------------------------------------------

# Runs `git -C $RepoDir <Arguments>` and requires a clean, non-empty result. Every call site that
# feeds the run-state side-car and the console banner must fail fast and specifically here: a
# bare `$LASTEXITCODE` check on only the FIRST git call (as this script used to do) lets a LATER
# git failure through with a blank/stale value, writing a corrupt .run-state.json instead of
# stopping the launcher.
function Invoke-GitMetadata {
    param(
        [Parameter(Mandatory)] [string]   $RepoDir,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [string]   $Description
    )
    $output = & git -C $RepoDir @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not resolve $Description (git $($Arguments -join ' ') exited $LASTEXITCODE): $output"
    }
    $value = ($output | Out-String).Trim()
    if (-not $value) {
        throw "Could not resolve $Description (git $($Arguments -join ' ') produced no output)."
    }
    return $value
}

# --- Destination deploy helpers (extension: -DestinationDirectory) -----------------------
#
# Publish-only mode: builds the client + server exactly like the default pipeline, then
# deploys the validated output into a caller-supplied directory via an atomic same-volume
# sibling rename instead of launching anything. See
# docs/superpowers/specs/2026-08-10-lmstreaming-standalone-publish-design.md ("Extension:
# -DestinationDirectory") for the full design this implements.

# Exact, ordered, byte-for-byte-carried-over set of top-level destination entries that must
# survive a Recognized-deployment update untouched. Everything else in the staged output
# replaces the destination's current content (deny-by-default). ".env" is the ONLY preserve
# entry that a fresh publish never produces on its own -- it is carried over from the
# EXISTING destination only, never from staging.
$script:DestinationPreserveList = @(
    'conversations',
    'notify-waits.db',
    'oauth-tokens',
    'workspaces',
    'chat-modes',
    'workflow-index',
    'logs',
    'recordings',
    '.env'
)

# notify-waits.db's SQLite sidecar names: Copy-PreserveSet already carries these over correctly
# FROM THE EXISTING DESTINATION (see below), but Copy-ReplaceSet must ALSO refuse to copy them
# from staged -- otherwise a stray sidecar present in a staged publish output could leak straight
# into the destination whenever the existing destination has no sidecar of its own to overwrite
# it with.
$script:NotifyWaitsSidecarNames = @(
    'notify-waits.db-wal',
    'notify-waits.db-shm',
    'notify-waits.db-journal'
)

$script:DefaultProcessEnumerationDelegate = { Get-Process -ErrorAction SilentlyContinue }
$script:DefaultMoveDelegate = { param($From, $To) [System.IO.Directory]::Move($From, $To) }

# Classifies a destination directory using ONLY cheap filesystem checks -- this always runs
# BEFORE any build/publish work so an Unrecognized destination is rejected before npm/dotnet
# ever runs. Four states: Missing (path does not exist), Empty (directory exists with zero
# entries -- hidden/system files still count as entries), Recognized (all three markers
# present: the executable, appsettings.json, and wwwroot/dist/index.html), Unrecognized
# (non-empty and missing at least one marker, including exactly two of the three).
function Test-DestinationState {
    param([Parameter(Mandatory)] [string] $DestinationDirectory)

    if (-not (Test-Path -LiteralPath $DestinationDirectory)) {
        return [pscustomobject]@{ State = 'Missing' }
    }

    $entries = @(Get-ChildItem -LiteralPath $DestinationDirectory -Force)
    if ($entries.Count -eq 0) {
        return [pscustomobject]@{ State = 'Empty' }
    }

    $hasExecutable  = Test-Path -LiteralPath (Join-Path $DestinationDirectory 'LmStreaming.Sample.exe')
    $hasAppsettings = Test-Path -LiteralPath (Join-Path $DestinationDirectory 'appsettings.json')
    $hasIndexHtml   = Test-Path -LiteralPath (Join-Path $DestinationDirectory (Join-Path 'wwwroot' (Join-Path 'dist' 'index.html')))

    if ($hasExecutable -and $hasAppsettings -and $hasIndexHtml) {
        return [pscustomobject]@{ State = 'Recognized' }
    }

    return [pscustomobject]@{ State = 'Unrecognized' }
}

# Checkpoint primitive: true if any enumerated process's executable path resolves to
# $ExecutablePath. Access-denied-tolerant (some processes refuse to reveal .Path) and
# exact-path (case-insensitive, full-path-resolved) rather than a name match, so it cannot be
# fooled by an unrelated process that merely shares the published exe's file name elsewhere.
function Test-ProcessHoldsPath {
    param(
        [Parameter(Mandatory)] [string]      $ExecutablePath,
        [Parameter(Mandatory)] [scriptblock] $ProcessEnumerationDelegate
    )

    $resolvedTarget = [System.IO.Path]::GetFullPath($ExecutablePath)
    $processes = & $ProcessEnumerationDelegate
    foreach ($process in $processes) {
        $candidatePath = $null
        try { $candidatePath = $process.Path } catch { $candidatePath = $null }
        if (-not $candidatePath) { continue }
        try {
            $resolvedCandidate = [System.IO.Path]::GetFullPath($candidatePath)
        } catch {
            continue
        }
        if ($resolvedCandidate.Equals($resolvedTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

# Candidate/backup names are always same-parent, same-volume SIBLINGS of the destination (not
# of the staged/publish directory, which may live on a different drive) -- this is what makes
# the final swap an atomic, near-instantaneous rename rather than a cross-volume copy.
function New-CandidateDirectory {
    param([Parameter(Mandatory)] [string] $DestinationDirectory)
    $parent = Split-Path -Parent $DestinationDirectory
    $name   = Split-Path -Leaf $DestinationDirectory
    $candidatePath = Join-Path $parent ('{0}.candidate-{1}-{2}' -f $name, [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), $PID)
    [System.IO.Directory]::CreateDirectory($candidatePath) | Out-Null
    return $candidatePath
}

function New-BackupPath {
    param([Parameter(Mandatory)] [string] $DestinationDirectory)
    $parent = Split-Path -Parent $DestinationDirectory
    $name   = Split-Path -Leaf $DestinationDirectory
    return Join-Path $parent ('{0}.backup-{1}-{2}' -f $name, [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), $PID)
}

# Copies every top-level staged entry into the candidate EXCEPT names on the preserve list
# (deny-by-default): this is what makes ".env" structurally unable to come from staging -- it
# is simply never copied here, only ever by Copy-PreserveSet below.
function Copy-ReplaceSet {
    param(
        [Parameter(Mandatory)] [string] $StagedDirectory,
        [Parameter(Mandatory)] [string] $CandidateDirectory
    )

    Get-ChildItem -LiteralPath $StagedDirectory -Force | ForEach-Object {
        if ($script:DestinationPreserveList -contains $_.Name) {
            return
        }
        if ($script:NotifyWaitsSidecarNames -contains $_.Name) {
            return
        }
        $targetPath = Join-Path $CandidateDirectory $_.Name
        if ($_.PSIsContainer) {
            Copy-Item -LiteralPath $_.FullName -Destination $targetPath -Recurse -Force
        } else {
            Copy-Item -LiteralPath $_.FullName -Destination $targetPath -Force
        }
    }
}

# Copies every preserve-list entry that exists on the EXISTING destination into the
# candidate, byte-for-byte. notify-waits.db's SQLite sidecars (-wal/-shm/-journal) are copied
# alongside it, but only whichever of those sidecar files is actually present.
function Copy-PreserveSet {
    param(
        [Parameter(Mandatory)] [string] $DestinationDirectory,
        [Parameter(Mandatory)] [string] $CandidateDirectory
    )

    foreach ($name in $script:DestinationPreserveList) {
        $sourcePath = Join-Path $DestinationDirectory $name
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            continue
        }
        $targetPath = Join-Path $CandidateDirectory $name
        $item = Get-Item -LiteralPath $sourcePath -Force
        if ($item.PSIsContainer) {
            Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Recurse -Force
            continue
        }

        Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force

        if ($name -eq 'notify-waits.db') {
            foreach ($suffix in @('-wal', '-shm', '-journal')) {
                $sidecarSource = $sourcePath + $suffix
                if (Test-Path -LiteralPath $sidecarSource) {
                    Copy-Item -LiteralPath $sidecarSource -Destination ($targetPath + $suffix) -Force
                }
            }
        }
    }
}

# Thin, injectable wrapper around the one primitive every atomic swap/backup/rollback move in
# this feature goes through -- so a test can deterministically fail exactly the Nth rename
# (e.g. the second rename, candidate -> destination) without touching the filesystem itself.
function Invoke-AtomicMove {
    param(
        [Parameter(Mandatory)] [string]      $From,
        [Parameter(Mandatory)] [string]      $To,
        [Parameter(Mandatory)] [scriptblock] $MoveDelegate
    )
    & $MoveDelegate $From $To
}

# Orchestrates one deploy of a validated staged publish output into $DestinationDirectory.
# Never builds/publishes/launches anything itself -- StagedDirectory must already be a
# validated publish artifact. Classifies first and rejects Unrecognized immediately. Missing
# and Empty destinations never invoke process enumeration (there is nothing recognized that
# could be running). A Recognized destination is guarded by three checkpoints -- immediately
# before any candidate/staging work (Checkpoint A), immediately before reading the preserve
# list from the live destination (Checkpoint B), and immediately before the final swap
# (Checkpoint C) -- and only its second rename (candidate -> destination) has a rollback path:
# if it fails after the first rename (existing -> backup) already succeeded, the backup is
# renamed straight back onto the destination path (byte-identical to the pre-run state) and
# the fully-assembled candidate is retained on disk for inspection, never deleted.
function Invoke-DestinationDeploy {
    param(
        [Parameter(Mandatory)] [string]      $StagedDirectory,
        [Parameter(Mandatory)] [string]      $DestinationDirectory,
        [scriptblock] $ProcessEnumerationDelegate = $script:DefaultProcessEnumerationDelegate,
        [scriptblock] $MoveDelegate = $script:DefaultMoveDelegate
    )

    $state = (Test-DestinationState -DestinationDirectory $DestinationDirectory).State
    $executablePath = Join-Path $DestinationDirectory 'LmStreaming.Sample.exe'

    if ($state -eq 'Unrecognized') {
        throw "Destination '$DestinationDirectory' is Unrecognized (non-empty, but missing one or more of the required markers: LmStreaming.Sample.exe, appsettings.json, wwwroot/dist/index.html). Refusing to deploy -- nothing on disk was touched."
    }

    if ($state -eq 'Missing') {
        # Assembled as a same-parent, same-volume SIBLING candidate of the destination (via
        # New-CandidateDirectory), then a single rename from THAT candidate into place -- NOT a
        # raw move of $StagedDirectory itself. $StagedDirectory may live on a different volume
        # than the destination (e.g. this repo's own scratchpad tree vs. an external
        # -DestinationDirectory), and Directory.Move cannot cross volumes; renaming staged itself
        # would also consume it, unlike the Empty and Recognized branches, which both leave
        # $StagedDirectory intact for the caller.
        $candidateDirectory = New-CandidateDirectory -DestinationDirectory $DestinationDirectory
        Copy-ReplaceSet -StagedDirectory $StagedDirectory -CandidateDirectory $candidateDirectory
        Confirm-PublishArtifact -PublishDirectory $candidateDirectory | Out-Null
        Invoke-AtomicMove -From $candidateDirectory -To $DestinationDirectory -MoveDelegate $MoveDelegate
        return
    }

    if ($state -eq 'Empty') {
        # Same two-move shape as Recognized (existing -> backup, then candidate -> destination), via
        # a freshly assembled candidate copied from staged -- NOT a raw move of $StagedDirectory
        # itself, so the staged directory is left intact for the caller exactly like every other
        # branch. There is nothing to preserve (the destination was empty), so the candidate is just
        # the staged replace-set.
        $candidateDirectory = New-CandidateDirectory -DestinationDirectory $DestinationDirectory
        Copy-ReplaceSet -StagedDirectory $StagedDirectory -CandidateDirectory $candidateDirectory
        Confirm-PublishArtifact -PublishDirectory $candidateDirectory | Out-Null

        $backupPath = New-BackupPath -DestinationDirectory $DestinationDirectory
        Invoke-AtomicMove -From $DestinationDirectory -To $backupPath -MoveDelegate $MoveDelegate

        try {
            Invoke-AtomicMove -From $candidateDirectory -To $DestinationDirectory -MoveDelegate $MoveDelegate
        } catch {
            Invoke-AtomicMove -From $backupPath -To $DestinationDirectory -MoveDelegate $MoveDelegate
            throw "Swapping the candidate into place failed and has been rolled back to the pre-existing destination (byte-identical to before this run): $($_.Exception.Message). The assembled candidate is retained at '$candidateDirectory' for inspection."
        }

        try {
            Remove-Item -LiteralPath $backupPath -Recurse -Force
        } catch {
            Write-Warning "Deploy succeeded, but removing the temporary backup '$backupPath' failed: $($_.Exception.Message)"
        }
        return
    }

    # $state -eq 'Recognized' from here on.
    if (Test-ProcessHoldsPath -ExecutablePath $executablePath -ProcessEnumerationDelegate $ProcessEnumerationDelegate) {
        throw "Checkpoint A: the destination executable at '$executablePath' is currently running. Stop it before deploying, then retry."
    }

    $candidateDirectory = New-CandidateDirectory -DestinationDirectory $DestinationDirectory
    Copy-ReplaceSet -StagedDirectory $StagedDirectory -CandidateDirectory $candidateDirectory

    if (Test-ProcessHoldsPath -ExecutablePath $executablePath -ProcessEnumerationDelegate $ProcessEnumerationDelegate) {
        throw "Checkpoint B: the destination executable at '$executablePath' started running before its preserved data could be read. The assembled candidate is retained at '$candidateDirectory' for inspection; the destination was not touched."
    }

    Copy-PreserveSet -DestinationDirectory $DestinationDirectory -CandidateDirectory $candidateDirectory
    Confirm-PublishArtifact -PublishDirectory $candidateDirectory | Out-Null

    if (Test-ProcessHoldsPath -ExecutablePath $executablePath -ProcessEnumerationDelegate $ProcessEnumerationDelegate) {
        throw "Checkpoint C: the destination executable at '$executablePath' started running immediately before the swap. The assembled candidate is retained at '$candidateDirectory' for inspection; the destination was not touched."
    }

    $backupPath = New-BackupPath -DestinationDirectory $DestinationDirectory
    Invoke-AtomicMove -From $DestinationDirectory -To $backupPath -MoveDelegate $MoveDelegate

    try {
        Invoke-AtomicMove -From $candidateDirectory -To $DestinationDirectory -MoveDelegate $MoveDelegate
    } catch {
        Invoke-AtomicMove -From $backupPath -To $DestinationDirectory -MoveDelegate $MoveDelegate
        throw "Swapping the candidate into place failed and has been rolled back to the pre-existing destination (byte-identical to before this run): $($_.Exception.Message). The assembled candidate is retained at '$candidateDirectory' for inspection."
    }

    try {
        Remove-Item -LiteralPath $backupPath -Recurse -Force
    } catch {
        Write-Warning "Deploy succeeded, but removing the temporary backup '$backupPath' failed: $($_.Exception.Message)"
    }
}

# Real orchestrator for `-DestinationDirectory`: builds the client + server exactly like the
# default pipeline (reusing Invoke-ClientBuild/New-PublishRunDirectory/Invoke-ApplicationPublish/
# Confirm-PublishArtifact verbatim), then deploys via Invoke-DestinationDeploy instead of
# launching anything. Classifies the destination BEFORE any of that build work runs, so an
# Unrecognized destination is rejected before npm/dotnet ever start. For a Recognized
# destination, Checkpoint A (the running-process check) ALSO runs here, before the client build
# phase -- not only inside Invoke-DestinationDeploy, which by itself would only catch a running
# process AFTER the full (potentially minutes-long) build+publish had already completed.
function Invoke-DestinationPublish {
    param(
        [Parameter(Mandatory)] [string] $ProjectFile,
        [Parameter(Mandatory)] [string] $ClientAppDirectory,
        [Parameter(Mandatory)] [string] $Configuration,
        [Parameter(Mandatory)] [string] $DestinationDirectory,
        [Parameter(Mandatory)] [string] $RepositoryRoot,
        [scriptblock] $ProcessEnumerationDelegate = $script:DefaultProcessEnumerationDelegate
    )

    Write-Phase "Destination mode: publish-only -> $DestinationDirectory"

    $state = (Test-DestinationState -DestinationDirectory $DestinationDirectory).State
    if ($state -eq 'Unrecognized') {
        throw "Destination '$DestinationDirectory' is Unrecognized (non-empty, but missing one or more of the required markers: LmStreaming.Sample.exe, appsettings.json, wwwroot/dist/index.html). Refusing to publish -- nothing was built."
    }
    Write-Host "    Destination state: $state" -ForegroundColor DarkGray

    if ($state -eq 'Recognized') {
        $executablePath = Join-Path $DestinationDirectory 'LmStreaming.Sample.exe'
        if (Test-ProcessHoldsPath -ExecutablePath $executablePath -ProcessEnumerationDelegate $ProcessEnumerationDelegate) {
            throw "Checkpoint A: the destination executable at '$executablePath' is currently running. Stop it before deploying, then retry."
        }
    }

    Write-Phase "Build client (npm ci + npm run build) in $ClientAppDirectory"
    Invoke-ClientBuild -ClientAppDirectory $ClientAppDirectory

    $stagedDirectory = New-PublishRunDirectory -RepositoryRoot $RepositoryRoot
    Write-Host "    Staged directory: $stagedDirectory" -ForegroundColor DarkGray

    Write-Phase "Publish server [$Configuration] -> $stagedDirectory"
    Invoke-ApplicationPublish -ProjectFile $ProjectFile -Configuration $Configuration -OutputDirectory $stagedDirectory

    Write-Phase 'Validate staged artifact'
    $artifact = Confirm-PublishArtifact -PublishDirectory $stagedDirectory
    Write-Host "    Executable: $($artifact.ExecutablePath)" -ForegroundColor DarkGray
    Write-Host "    Index:      $($artifact.IndexPath)" -ForegroundColor DarkGray

    Write-Phase "Deploy -> $DestinationDirectory"
    Invoke-DestinationDeploy -StagedDirectory $stagedDirectory -DestinationDirectory $DestinationDirectory -ProcessEnumerationDelegate $ProcessEnumerationDelegate

    Write-Phase 'Destination publish complete'
    Write-Host "    Destination: $DestinationDirectory" -ForegroundColor Green
}

# The default build+publish+launch pipeline, wrapped in a named function so dot-sourcing this
# script (". publish-launch.ps1", used by PublishLaunchScriptHost in the test suite) only
# DEFINES functions and never executes npm/dotnet/launch as a side effect -- see the dot-source
# guard at the very bottom of this file.
function Invoke-Main {
    # These two MUST be passed in explicitly by the caller (see the dot-source guard at the bottom
    # of this file) -- Invoke-Main deliberately declares no [Parameter] bound to the script's own
    # -DestinationDirectory/-Port, because $PSBoundParameters INSIDE this function only ever
    # reflects what THIS function itself was called with, never what the enclosing script's own
    # top-level command line bound. Reading $PSBoundParameters.ContainsKey(...) here was the exact
    # bug: it was unconditionally false on every real invocation, so a genuine
    # "-DestinationDirectory X" on the command line was silently ignored and the default
    # build+launch pipeline ran instead of destination-only publish mode.
    param(
        [bool] $DestinationDirectoryWasExplicit = $false,
        [bool] $PortWasExplicit = $false
    )

    # Resolve paths from the script location so this works from any working directory.
    $ProjectDir  = $PSScriptRoot
    $ProjectFile = Join-Path $ProjectDir 'LmStreaming.Sample.csproj'
    $ClientAppDir = Join-Path $ProjectDir 'ClientApp'

    if (-not (Test-Path $ProjectFile)) {
        throw "Could not find LmStreaming.Sample.csproj next to this script ($ProjectFile)."
    }

    if ($DestinationDirectoryWasExplicit -and $DestinationDirectory) {
        $repositoryRoot = Invoke-GitMetadata -RepoDir $ProjectDir -Arguments @('rev-parse', '--show-toplevel') -Description 'the repository root'
        Invoke-DestinationPublish -ProjectFile $ProjectFile -ClientAppDirectory $ClientAppDir -Configuration $Configuration -DestinationDirectory $DestinationDirectory -RepositoryRoot $repositoryRoot
        return
    }

# --- Resolve backend port ---------------------------------------------------------------
Write-Phase 'Resolve port'
$backendPortExplicit = $PortWasExplicit

# Probes ALL FOUR addresses (wildcard v4/v6 + loopback v4/v6), not just the ones this script's own
# process binds to. A wildcard (0.0.0.0) bind can succeed on Windows even while a DIFFERENT process
# already holds that same port on a specific loopback address (127.0.0.1 / ::1) -- confirmed live:
# a pre-existing backend bound only to 127.0.0.1/::1 left 0.0.0.0 "free", so a wildcard-only probe
# reported the port available, and Kestrel's own wildcard bind then succeeded too, leaving two
# processes ambiguously listening on the same port. Checking every family/scope combination closes
# that gap.
$allAddressFamilies = @(
    [System.Net.IPAddress]::Any,
    [System.Net.IPAddress]::IPv6Any,
    [System.Net.IPAddress]::Loopback,
    [System.Net.IPAddress]::IPv6Loopback)

$backend = Resolve-InstancePort -Preferred $Port -WasExplicit $backendPortExplicit -Label 'Backend' `
    -Addresses $allAddressFamilies -Reserved @() -Force:$Force

Write-Host "    Backend port: $($backend.Port)$(if ($backend.AutoSelected) { ' (auto-selected)' })" -ForegroundColor DarkGray

# --- Resolve repository metadata ----------------------------------------------------------
$repositoryRoot = Invoke-GitMetadata -RepoDir $ProjectDir -Arguments @('rev-parse', '--show-toplevel') -Description 'the repository root'
$branch          = Invoke-GitMetadata -RepoDir $ProjectDir -Arguments @('rev-parse', '--abbrev-ref', 'HEAD') -Description 'the current branch'
$commit          = Invoke-GitMetadata -RepoDir $ProjectDir -Arguments @('rev-parse', 'HEAD') -Description 'the current commit'
$commitShort     = Invoke-GitMetadata -RepoDir $ProjectDir -Arguments @('rev-parse', '--short', 'HEAD') -Description 'the current short commit'

# The fresh directory is created before npm starts so a failed phase still has a reported
# inspection location.
$publishDirectory = New-PublishRunDirectory -RepositoryRoot $repositoryRoot
Write-Host "    Publish directory: $publishDirectory" -ForegroundColor DarkGray

$backendProcess = $null
$backendProcessId = 0
$backendExitCode = 1
try {
    # --- Phase: build the Vue client directly ------------------------------------------
    Write-Phase "Build client (npm ci + npm run build) in $ClientAppDir"
    Invoke-ClientBuild -ClientAppDirectory $ClientAppDir

    # --- Phase: publish the server -----------------------------------------------------
    Write-Phase "Publish server [$Configuration] -> $publishDirectory"
    Invoke-ApplicationPublish -ProjectFile $ProjectFile -Configuration $Configuration -OutputDirectory $publishDirectory

    # --- Phase: validate the full artifact ----------------------------------------------
    Write-Phase 'Validate publish artifact'
    $artifact = Confirm-PublishArtifact -PublishDirectory $publishDirectory
    Write-Host "    Executable: $($artifact.ExecutablePath)" -ForegroundColor DarkGray
    Write-Host "    Index:      $($artifact.IndexPath)" -ForegroundColor DarkGray
    foreach ($asset in $artifact.AssetPaths) {
        Write-Host "    Asset:      $asset" -ForegroundColor DarkGray
    }

    # --- Phase: launch the published executable -----------------------------------------
    Write-Phase "Launch published executable on http://localhost:$($backend.Port)"
    $backendArgs = @(
        '--urls', "http://0.0.0.0:$($backend.Port)",
        '--SandboxGateway:WorkspaceBasePath=',
        "--Auth:Webhook:PublicBaseUrl=$WebhookBaseUrl"
    )

    # Environment is set ONLY on the child ProcessStartInfo, not via $env: -- this must not
    # mutate the parent PowerShell session's environment (which could otherwise leak Production
    # into anything else the caller runs afterwards in the same session).
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $artifact.ExecutablePath
    foreach ($arg in $backendArgs) { $startInfo.ArgumentList.Add($arg) }
    $startInfo.WorkingDirectory = $publishDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $startInfo.EnvironmentVariables['DOTNET_ENVIRONMENT'] = 'Production'
    # Tell the published exe exactly where its .env lives: Program.FindEnvFile() walks up from
    # AppContext.BaseDirectory (the publish output dir), which is NOT an ancestor/descendant of
    # $ProjectDir (publish output lives under the repo's .claude/scratchpad tree), so that walk-up
    # would otherwise hit the repository root's .git and stop without ever finding the real .env.
    # Only a PATH STRING crosses into the child's environment here -- the .env file itself is never
    # copied into the retained publish artifact, and this variable is set ONLY on this child
    # ProcessStartInfo (never via $env:), so it cannot leak into the parent PowerShell session either.
    $startInfo.EnvironmentVariables['LMSTREAMING_ENV_FILE'] = (Join-Path $ProjectDir '.env')

    $backendProcess = [System.Diagnostics.Process]::new()
    $backendProcess.StartInfo = $startInfo
    $null = $backendProcess.Start()
    # Capture the PID once, right after a successful Start(), into an immutable local -- NOT
    # re-read from $backendProcess.Id later. Reading .Id on a Process object that was never
    # successfully started throws (it has no associated OS process), so if Start() itself throws,
    # `finally` below must not touch $backendProcess.Id at all; it checks this captured value
    # instead and only attempts cleanup when it is a real, positive PID.
    $backendProcessId = $backendProcess.Id

    # --- Phase: readiness ---------------------------------------------------------------
    Write-Phase 'Readiness: /api/providers'
    $readinessUri = "http://localhost:$($backend.Port)/api/providers"
    $ready = Wait-HttpReady -Uri $readinessUri -Process $backendProcess -TimeoutSeconds 60
    if (-not $ready) {
        if ($backendProcess.HasExited) {
            throw "Published backend exited immediately (exit code $($backendProcess.ExitCode)) before becoming ready on port $($backend.Port). Check the console output above for a startup error."
        }
        throw "Published backend did not return a success status from $readinessUri within the timeout. Not proceeding -- a readiness failure is fatal, not a warning."
    }

    Write-Phase 'Readiness: static assets'
    Confirm-HttpSuccess -Uri "http://localhost:$($backend.Port)/dist/index.html"
    Write-Host "    OK: /dist/index.html" -ForegroundColor DarkGray
    foreach ($asset in $artifact.AssetPaths) {
        Confirm-HttpSuccess -Uri "http://localhost:$($backend.Port)$asset"
        Write-Host "    OK: $asset" -ForegroundColor DarkGray
    }

    # --- Run-state side-car (written only after every readiness check has passed) --------
    $runState = [pscustomobject]@{
        startedAtUtc      = (Get-Date).ToUniversalTime().ToString('o')
        checkout          = $repositoryRoot
        branch            = $branch
        commit            = $commit
        configuration     = $Configuration
        publishDirectory  = $publishDirectory
        backend           = [pscustomobject]@{
            preferredPort    = $Port
            port             = $backend.Port
            portAutoSelected = $backend.AutoSelected
            pid              = $backendProcessId
            url              = "http://localhost:$($backend.Port)"
        }
        webhookBaseUrl    = $WebhookBaseUrl
        note              = 'Running from the published artifact at publishDirectory (LmStreaming.Sample.exe), not launched from source. There is no Vite process or Vite port.'
    }
    Write-RunStateFile -Path (Join-Path $publishDirectory '.run-state.json') -State $runState

    Write-Phase 'Ready'
    Write-Host "    Publish directory: $publishDirectory" -ForegroundColor Green
    Write-Host "    Checkout: $repositoryRoot" -ForegroundColor DarkGray
    Write-Host "    Branch:   $branch" -ForegroundColor DarkGray
    Write-Host "    Commit:   $commitShort" -ForegroundColor DarkGray
    Write-Host "    Backend:  http://localhost:$($backend.Port)  (PID $backendProcessId)" -ForegroundColor Green
    Write-Host "    Webhook:  $WebhookBaseUrl" -ForegroundColor DarkGray
    Write-Host "    Run-state: $(Join-Path $publishDirectory '.run-state.json')" -ForegroundColor DarkGray
    Write-Host '    Stop:     Ctrl+C' -ForegroundColor DarkGray
    Write-Host ''

    # Supervise via a bounded polling loop, NOT a raw WaitForExit(): an indefinite WaitForExit()
    # (no timeout) blocks this thread inside the BCL's unmanaged wait with no return point until
    # the child exits on its own, so PowerShell's engine never gets a chance to observe a pending
    # Ctrl+C / pipeline-stop between statements -- `finally` (and therefore
    # Stop-OwnedProcessTree) might never run. Each bounded WaitForExit(250) call returns control to
    # the engine every 250ms, which is exactly the point at which a pending Ctrl+C is honored and
    # unwinds through `finally` as ordinary script termination. Deliberately no
    # [Console]::CancelKeyPress handler here -- that would be a global handler outliving this
    # script's process lifetime; the bounded loop needs no such handler to be interruptible.
    while (-not $backendProcess.WaitForExit(250)) { }
    # Capture the backend's own exit code NOW -- before `finally` runs `taskkill` (which sets its
    # own $LASTEXITCODE and would otherwise silently replace a clean backend shutdown's exit code
    # with an unrelated taskkill result, e.g. a "process already gone" failure on a graceful stop).
    $backendExitCode = $backendProcess.ExitCode
} catch {
    Write-Host "    FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "    $($_.InvocationInfo.PositionMessage)" -ForegroundColor DarkGray
    # $backendExitCode is still its pre-try default (1): WaitForExit above never completed, so
    # there is no real backend exit code to report -- 1 is the meaningful nonzero launcher
    # failure this finding requires, independent of whatever $LASTEXITCODE happens to hold.
    exit $backendExitCode
} finally {
    Write-Phase 'Shutting down'
    # Only clean up when Start() actually succeeded and produced a real, positive PID -- NOT a
    # bare "$backendProcess is non-null" check. $backendProcess is assigned (via `::new()`) before
    # Start() is even called, so a thrown Start() (e.g. exe missing/no permission) still leaves a
    # non-null-but-never-started Process object; re-reading its .Id here would throw (no OS
    # process is associated with it), masking the real Start() failure with a secondary error and
    # skipping the rest of this cleanup block. $backendProcessId is the immutable value captured
    # once, right after a successful Start() -- it stays 0 if Start() never succeeded.
    if ($backendProcessId -gt 0) {
        Stop-OwnedProcessTree -ProcessId $backendProcessId -Label 'Backend'
    }
    Write-Host "    Publish directory retained at: $publishDirectory" -ForegroundColor DarkGray
    }

    exit $backendExitCode
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-Main -DestinationDirectoryWasExplicit $script:DestinationDirectoryWasExplicit -PortWasExplicit $script:PortWasExplicit
}
