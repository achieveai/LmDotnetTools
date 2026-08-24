#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Publishes samples/CodeReviewDaemon.Sample into a standalone deployment directory,
    swapping it into place atomically and preserving the destination's live tenant
    configuration.

.DESCRIPTION
    The daemon is launched from a published directory (see scripts/ensure-services.ps1),
    not from a bin/Debug tree inside a worktree. This script produces that directory.

    Deploying is never a partial copy into a live destination. The publish output is
    staged elsewhere, assembled into a same-parent SIBLING candidate directory, and only
    then swapped in with two renames: <dest> -> <dest>.backup-<UTC>-<PID>, then
    <dest>.candidate-<UTC>-<PID> -> <dest>. Both renames are same-volume by construction
    because both siblings are derived from Split-Path -Parent $DestinationDirectory,
    never from the staging directory (which may sit on another drive). A rename is
    atomic and near-instantaneous; a recursive copy over a live tree is neither, and an
    interrupted one leaves a deployment that is half old and half new with nothing to
    roll back to.

    TENANT CONFIGURATION IS PRESERVED, NEVER OVERWRITTEN.
    Beside the base appsettings.json the daemon carries per-tenant profiles selected with
    `CodeReviewDaemon.Sample --review <tenant>`: appsettings.achieveai.json,
    appsettings.mcqdb.json, appsettings.s2s.json. Every one of them carries ABSOLUTE
    machine-specific paths - DatabasePath, LogFilePath and ConversationStorePath all point
    into B:/sources/LmDotnetTools/.run/, where the multi-GB live SQLite databases live.
    Overwriting a deployed tenant profile with the repository's copy would silently
    repoint a daemon at a different database: it would start cleanly, poll cleanly, and
    simply have no history. So on an update these files are carried over from the EXISTING
    destination, exactly the way publish-launch.ps1 treats .env, and the staged copies are
    not deployed. They ARE deployed on a first/empty install, where there is nothing to
    preserve and something has to seed them.

    The base appsettings.json is NOT preserved. It carries no absolute paths and is the
    layer the tenant files override, so it must track the repository.

.PARAMETER DestinationDirectory
    The standalone deployment directory. Defaults to B:\published\review-daemon,
    the sibling of the existing B:\published\LmStreaming.Sample deployment.

.PARAMETER Configuration
    Build configuration passed to `dotnet publish`. Defaults to Release: this is a
    deployment, not a debugging session.

.PARAMETER ProjectFile
    The daemon project to publish. Defaults to the CodeReviewDaemon.Sample project in the
    repository this script is tracked in, resolved from $PSScriptRoot rather than from the
    caller's current directory - a scheduled task or a shim runs with an arbitrary $PWD.

.PARAMETER StagingRoot
    Parent directory for the per-run staging directory. Each run creates
    <StagingRoot>/run-<UTC>-<PID> and leaves it in place afterwards for inspection.

.PARAMETER StagedDirectory
    Deploy an ALREADY-PUBLISHED directory instead of running `dotnet publish`. Intended for
    redeploying a verified artifact and for testing the deploy path itself without paying
    for a build. The directory is validated against the same markers a fresh publish is.

.EXAMPLE
    ./scripts/publish-daemon.ps1
    Publish Release and deploy to B:\published\review-daemon.

.EXAMPLE
    ./scripts/publish-daemon.ps1 -DestinationDirectory D:\scratch\daemon-test
    Deploy to a scratch destination - the way to exercise this script without touching a
    live deployment.

.EXAMPLE
    ./scripts/publish-daemon.ps1 -StagedDirectory C:\tmp\run-20260823T101500000Z-1234
    Redeploy an artifact that was already published, skipping the build.
#>
[CmdletBinding()]
param(
    [string] $DestinationDirectory = 'B:\published\review-daemon',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $ProjectFile,
    [string] $StagingRoot,
    [string] $StagedDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Captured HERE, at true script scope, immediately after the param() block: inside a
# function $PSBoundParameters only ever reflects that function's own bound parameters,
# never the enclosing script's command line. publish-launch.ps1 shipped a bug that was
# exactly this - a genuine "-DestinationDirectory X" was read as absent from inside
# Invoke-Main and the destination branch never ran.
$script:StagedDirectoryWasExplicit = $PSBoundParameters.ContainsKey('StagedDirectory')

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------

# The published apphost's file name. The daemon runs as an APPHOST, not under dotnet.exe -
# every process-identity check in this repo that filtered on Name='dotnet.exe' missed it.
$script:ExecutableName = 'CodeReviewDaemon.Sample.exe'

# The exact marker set that defines a "Recognized" deployment. Test-DestinationState (which
# decides whether a destination is one of ours) and Confirm-PublishArtifact (which validates
# a candidate before it is swapped in) derive from this SAME list, so the two cannot
# disagree. publish-launch.ps1 records what happens when they do: a publish that produced no
# appsettings.json passed validation, deployed, deleted its backup, and then classified as
# Unrecognized on the NEXT run - locking the operator out of their own deploy directory with
# an error calling it foreign.
$script:DestinationMarkerRelativePaths = @(
    $script:ExecutableName,
    'CodeReviewDaemon.Sample.dll',
    'appsettings.json'
)

# Top-level destination entries carried over byte-for-byte from an existing deployment.
# Everything else in the staged output replaces the destination's content.
#
# Two rules, deliberately separate, because they answer different questions:
#
#   PreserveNames / PreservePatterns - "the deployed copy wins over the staged copy".
#   A staged copy is still used when the destination has none, so adding a fourth tenant
#   profile to the repository does deploy it the first time. That is why the tenant rule is
#   a PATTERN and not the three names known today: an explicit list would silently drop a
#   tenant added later, and the failure mode - a daemon quietly running with no profile -
#   looks exactly like a healthy daemon with nothing to do.
#
#   NeverFromStaging - "the staged copy is not a copy of this file at all". .env holds
#   secrets and is never produced by `dotnet publish`; if one ever appeared in a staged tree
#   it would be an accident, and copying it forward would overwrite the real one.
$script:DestinationPreserveNames = @(
    'oauth-tokens',
    'conversations',
    'logs',
    '.logs'
)

# 'appsettings.*.json' matches appsettings.achieveai.json / .mcqdb.json / .s2s.json and any
# tenant added later. It does NOT match the base 'appsettings.json': the pattern requires a
# literal dot after 'appsettings' AND a '.json' suffix after the wildcard, and 'json' has
# neither. The base file is deployed normally, which is the point - it is the layer the
# tenant files override and it carries no absolute paths.
$script:DestinationPreservePatterns = @(
    'appsettings.*.json'
)

$script:DestinationNeverFromStaging = @(
    '.env'
)

# Injectable seams. A test can fail exactly the Nth rename, or substitute a process table,
# without touching the real filesystem or the real process list.
$script:DefaultProcessEnumerationDelegate = { Get-Process -ErrorAction SilentlyContinue }
$script:DefaultMoveDelegate = { param($From, $To) [System.IO.Directory]::Move($From, $To) }

# Injectable seam for "is this file locked by a running process?". Returns $true when the
# file exists and CANNOT be opened for exclusive read/write - which is the property that
# actually decides whether the swap's rename will fail.
$script:DefaultExclusiveOpenProbeDelegate = {
    param($Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    try {
        $stream = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
        $stream.Dispose()
        return $false
    }
    catch [System.IO.IOException] {
        # Sharing violation: something else holds the file. This is the signal we want.
        return $true
    }
    catch [System.UnauthorizedAccessException] {
        # Cannot even attempt the probe (ACL, read-only). Indeterminate -> fail closed.
        return $true
    }
}

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

function Write-Phase([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Detail([string] $Message) {
    Write-Host "    $Message" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# Preserve-list membership
# ---------------------------------------------------------------------------

# ONE predicate, consulted with opposite polarity by the two copy passes: a deny-list when
# copying staged -> candidate, an allow-list when copying live-destination -> candidate.
# Two hand-maintained membership tests would eventually disagree, and the disagreement that
# matters here is silent - a tenant profile that is both skipped from staging and not
# carried over simply vanishes from the deployment.
function Test-PreservedName {
    param([Parameter(Mandatory)] [string] $Name)

    if ($script:DestinationPreserveNames -contains $Name) { return $true }
    foreach ($pattern in $script:DestinationPreservePatterns) {
        if ($Name -like $pattern) { return $true }
    }
    return $false
}

function Test-NeverFromStagingName {
    param([Parameter(Mandatory)] [string] $Name)
    return ($script:DestinationNeverFromStaging -contains $Name)
}

# ---------------------------------------------------------------------------
# Destination classification
# ---------------------------------------------------------------------------

# Classifies a destination using ONLY cheap filesystem checks - this always runs BEFORE any
# build work, so an Unrecognized destination is rejected before `dotnet publish` ever runs.
# Four states: Missing (path does not exist), Empty (exists, zero entries - hidden and
# system entries still count), Recognized (every marker present), Unrecognized (non-empty
# and missing at least one marker, including missing exactly one of three).
function Test-DestinationState {
    param([Parameter(Mandatory)] [string] $DestinationDirectory)

    if (-not (Test-Path -LiteralPath $DestinationDirectory)) {
        return [pscustomobject]@{ State = 'Missing' }
    }

    $entries = @(Get-ChildItem -LiteralPath $DestinationDirectory -Force)
    if ($entries.Count -eq 0) {
        return [pscustomobject]@{ State = 'Empty' }
    }

    $missingMarkers = @(
        $script:DestinationMarkerRelativePaths |
            Where-Object { -not (Test-Path -LiteralPath (Join-Path $DestinationDirectory $_)) }
    )

    if ($missingMarkers.Count -eq 0) {
        return [pscustomobject]@{ State = 'Recognized' }
    }

    return [pscustomobject]@{ State = 'Unrecognized' }
}

function Get-UnrecognizedDestinationMessage {
    param(
        [Parameter(Mandatory)] [string] $DestinationDirectory,
        [Parameter(Mandatory)] [string] $Refusal
    )
    $markerList = $script:DestinationMarkerRelativePaths -join ', '
    return "Destination '$DestinationDirectory' is Unrecognized (non-empty, but missing one or more of the required markers: $markerList). $Refusal"
}

# Refuses when the destination is Missing but leftover deploy siblings sit beside it.
# Between the two renames of a swap the destination path does not exist at all, so a crash
# in that window leaves <leaf>.backup-* and/or <leaf>.candidate-* with NO destination.
# Re-running would then classify Missing and take the fresh-install branch - which never
# carries the preserved set over - so the retry would deploy a blank instance and strand the
# operator's only deployed tenant profiles in a sibling nothing ever mentions again.
function Assert-NoOrphanedDeploySiblings {
    param([Parameter(Mandatory)] [string] $DestinationDirectory)

    $parent = Split-Path -Parent $DestinationDirectory
    $leaf = Split-Path -Leaf $DestinationDirectory
    if ([string]::IsNullOrEmpty($parent) -or -not (Test-Path -LiteralPath $parent)) {
        return
    }

    $orphans = @(
        Get-ChildItem -LiteralPath $parent -Force -Directory |
            Where-Object { $_.Name -like "$leaf.backup-*" -or $_.Name -like "$leaf.candidate-*" } |
            ForEach-Object { $_.FullName }
    )

    if ($orphans.Count -gt 0) {
        throw (
            "Destination '$DestinationDirectory' does not exist, but $($orphans.Count) leftover deploy " +
            "sibling(s) were found beside it:`n  " + ($orphans -join "`n  ") + "`n" +
            "That is the signature of a deploy interrupted between its two renames. The '.backup-*' " +
            "directory is your PREVIOUS deployment and holds the only copy of the deployed tenant " +
            "profiles (appsettings.<tenant>.json) and oauth-tokens/. Rename it back onto " +
            "'$DestinationDirectory' to recover, then re-run. Refusing to deploy - a fresh install " +
            "here would strand that data permanently."
        )
    }
}

function Resolve-DestinationDirectory {
    param([Parameter(Mandatory)] [string] $DestinationDirectory)

    if ([string]::IsNullOrWhiteSpace($DestinationDirectory)) {
        throw 'DestinationDirectory must not be empty.'
    }

    # Resolved against $PWD rather than the process working directory, and never with
    # Resolve-Path, which requires the path to already exist - the Missing state is normal.
    $absolute = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine((Get-Location).ProviderPath, $DestinationDirectory))
    $absolute = $absolute.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)

    $parent = Split-Path -Parent $absolute
    if ([string]::IsNullOrEmpty($parent)) {
        throw "Refusing to use '$DestinationDirectory' as a destination: it resolves to a volume root ('$absolute'). The deploy renames the destination to a sibling, which a root has none of."
    }

    return $absolute
}

# ---------------------------------------------------------------------------
# Running-process checkpoint
# ---------------------------------------------------------------------------

# Is the destination executable in use? Two independent signals, ORed, because each alone
# has a hole:
#
#  1. An exclusive-open probe (FileShare.None) on the executable itself. A running Windows
#     process holds its own image file with a share mode that denies write, so the probe
#     fails with a sharing violation - the same condition that makes the swap's rename fail.
#  2. Process enumeration matching the resolved image path, which names WHICH process to
#     stop and catches an executable already renamed out from under a live process.
#
# The fail-closed rule is narrowed to a NAME match on purpose. PowerShell's Process.Path is
# an ETS ScriptProperty over MainModule.FileName; for an elevated or other-user process the
# underlying Win32Exception is swallowed and the getter yields $null rather than throwing.
# Treating every null path as a match would make this predicate permanently true on any box
# and refuse every deploy. "Name matches AND path indeterminate" isolates the one ambiguous
# case that matters - an instance of OUR apphost we cannot confirm - and calls it held.
#
# This also happens to be the identity check that survives the apphost trap: the daemon and
# the review host run as CodeReviewDaemon.Sample.exe / LmStreaming.Sample.exe, so a check
# keyed on 'dotnet.exe' would see nothing at all.
function Test-ProcessHoldsPath {
    param(
        [Parameter(Mandatory)] [string]      $ExecutablePath,
        [Parameter(Mandatory)] [scriptblock] $ProcessEnumerationDelegate,
        [scriptblock] $ExclusiveOpenProbeDelegate = $script:DefaultExclusiveOpenProbeDelegate
    )

    $resolvedTarget = [System.IO.Path]::GetFullPath($ExecutablePath)

    if (& $ExclusiveOpenProbeDelegate $resolvedTarget) {
        return $true
    }

    $targetProcessName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedTarget)
    $processes = & $ProcessEnumerationDelegate
    foreach ($process in $processes) {
        $candidatePath = $null
        try {
            $candidatePath = $process.Path
        }
        catch {
            # Belt and braces: in practice the getter returns $null rather than throwing,
            # and the branch below is the one that fires.
            $candidatePath = $null
        }

        if (-not $candidatePath) {
            $candidateName = $null
            try { $candidateName = $process.ProcessName } catch { $candidateName = $null }
            if ($candidateName -and $candidateName.Equals($targetProcessName, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
            continue
        }

        try {
            $resolvedCandidate = [System.IO.Path]::GetFullPath($candidatePath)
        }
        catch {
            continue
        }
        if ($resolvedCandidate.Equals($resolvedTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }
    return $false
}

# ---------------------------------------------------------------------------
# Publish
# ---------------------------------------------------------------------------

function New-PublishRunDirectory {
    param([Parameter(Mandatory)] [string] $StagingRoot)

    $runDirectory = Join-Path $StagingRoot ('run-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), $PID)
    [System.IO.Directory]::CreateDirectory($runDirectory) | Out-Null
    return $runDirectory
}

# Native commands do not honour $ErrorActionPreference = 'Stop', so every native call in
# this file carries its own $LASTEXITCODE check. Output is deliberately NOT captured: the
# build log belongs on the operator's console.
function Invoke-ApplicationPublish {
    param(
        [Parameter(Mandatory)] [string] $ProjectFile,
        [Parameter(Mandatory)] [string] $Configuration,
        [Parameter(Mandatory)] [string] $OutputDirectory
    )

    & dotnet publish $ProjectFile -c $Configuration -o $OutputDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

function Confirm-PublishArtifact {
    param([Parameter(Mandatory)] [string] $PublishDirectory)

    $missing = @(
        $script:DestinationMarkerRelativePaths |
            Where-Object { -not (Test-Path -LiteralPath (Join-Path $PublishDirectory $_)) }
    )
    if ($missing.Count -gt 0) {
        throw "Publish output at '$PublishDirectory' is incomplete - missing: $($missing -join ', '). Refusing to deploy it."
    }
    return [pscustomobject]@{
        PublishDirectory = $PublishDirectory
        ExecutablePath   = (Join-Path $PublishDirectory $script:ExecutableName)
    }
}

# ---------------------------------------------------------------------------
# Candidate assembly and atomic swap
# ---------------------------------------------------------------------------

# Candidate and backup names are always same-parent, same-volume SIBLINGS of the
# destination - not of the staging directory, which may live on a different drive. That is
# what makes the final swap an atomic rename rather than a cross-volume copy. There is no
# explicit drive-letter comparison because same-volume-ness is structural here;
# Directory.Move throwing on a cross-volume attempt is the backstop.
function New-CandidateDirectory {
    param([Parameter(Mandatory)] [string] $DestinationDirectory)
    $parent = Split-Path -Parent $DestinationDirectory
    $name = Split-Path -Leaf $DestinationDirectory
    $candidatePath = Join-Path $parent ('{0}.candidate-{1}-{2}' -f $name, [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), $PID)
    [System.IO.Directory]::CreateDirectory($candidatePath) | Out-Null
    return $candidatePath
}

function New-BackupPath {
    param([Parameter(Mandatory)] [string] $DestinationDirectory)
    $parent = Split-Path -Parent $DestinationDirectory
    $name = Split-Path -Leaf $DestinationDirectory
    return Join-Path $parent ('{0}.backup-{1}-{2}' -f $name, [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), $PID)
}

function Invoke-AtomicMove {
    param(
        [Parameter(Mandatory)] [string]      $From,
        [Parameter(Mandatory)] [string]      $To,
        [Parameter(Mandatory)] [scriptblock] $MoveDelegate
    )
    & $MoveDelegate $From $To
}

# Copies staged -> candidate, skipping any name the destination's own copy must win.
#
# $ExistingDestinationDirectory is what makes "preserve if it already exists" different
# from "never deploy": a preserved name is skipped ONLY when the live destination actually
# has it. Omit the parameter (fresh install) and every staged entry is copied, which is how
# appsettings.<tenant>.json gets seeded the first time. NeverFromStaging entries are skipped
# unconditionally - there is no legitimate staged .env.
function Copy-ReplaceSet {
    param(
        [Parameter(Mandatory)] [string] $StagedDirectory,
        [Parameter(Mandatory)] [string] $CandidateDirectory,
        [string] $ExistingDestinationDirectory
    )

    foreach ($entry in @(Get-ChildItem -LiteralPath $StagedDirectory -Force)) {
        if (Test-NeverFromStagingName -Name $entry.Name) {
            continue
        }
        if ((Test-PreservedName -Name $entry.Name) -and
            -not [string]::IsNullOrEmpty($ExistingDestinationDirectory) -and
            (Test-Path -LiteralPath (Join-Path $ExistingDestinationDirectory $entry.Name))) {
            continue
        }
        $targetPath = Join-Path $CandidateDirectory $entry.Name
        if ($entry.PSIsContainer) {
            Copy-Item -LiteralPath $entry.FullName -Destination $targetPath -Recurse -Force
        }
        else {
            Copy-Item -LiteralPath $entry.FullName -Destination $targetPath -Force
        }
    }
}

# Copies every preserved entry that exists on the EXISTING destination into the candidate,
# byte-for-byte. SQLite sidecars (-wal/-shm/-journal) travel with their database, but only
# whichever of them is actually present.
function Copy-PreserveSet {
    param(
        [Parameter(Mandatory)] [string] $DestinationDirectory,
        [Parameter(Mandatory)] [string] $CandidateDirectory
    )

    # A plain foreach, not a ForEach-Object pipeline: the accumulator below must live in
    # THIS function's scope, and relying on a pipeline block to share it is the kind of
    # scoping subtlety that silently returns an empty list.
    $copied = @()
    foreach ($entry in @(Get-ChildItem -LiteralPath $DestinationDirectory -Force)) {
        $name = $entry.Name
        if (-not ((Test-PreservedName -Name $name) -or (Test-NeverFromStagingName -Name $name))) {
            continue
        }
        $targetPath = Join-Path $CandidateDirectory $name
        if ($entry.PSIsContainer) {
            Copy-Item -LiteralPath $entry.FullName -Destination $targetPath -Recurse -Force
        }
        else {
            Copy-Item -LiteralPath $entry.FullName -Destination $targetPath -Force
            foreach ($suffix in @('-wal', '-shm', '-journal')) {
                $sidecarSource = $entry.FullName + $suffix
                if (Test-Path -LiteralPath $sidecarSource) {
                    Copy-Item -LiteralPath $sidecarSource -Destination ($targetPath + $suffix) -Force
                }
            }
        }
        $copied += $name
    }
    return , @($copied)
}

# The two-rename swap.
#
# Rename 1 (destination -> backup) is UNGUARDED on purpose: it is the first mutation, so a
# failure there leaves the pre-run state intact and the natural error is the right error.
# Only rename 2 has a rollback path, and that rollback is itself guarded: between the two
# renames the destination path does not exist at all, so if the rollback ALSO fails the
# operator has no destination and two siblings, and the only thing that makes that
# recoverable is being told both paths and both errors.
function Invoke-CandidateSwap {
    param(
        [Parameter(Mandatory)] [string]      $CandidateDirectory,
        [Parameter(Mandatory)] [string]      $DestinationDirectory,
        [Parameter(Mandatory)] [scriptblock] $MoveDelegate
    )

    $backupPath = New-BackupPath -DestinationDirectory $DestinationDirectory
    Invoke-AtomicMove -From $DestinationDirectory -To $backupPath -MoveDelegate $MoveDelegate

    try {
        Invoke-AtomicMove -From $CandidateDirectory -To $DestinationDirectory -MoveDelegate $MoveDelegate
    }
    catch {
        $swapError = $_.Exception.Message
        try {
            Invoke-AtomicMove -From $backupPath -To $DestinationDirectory -MoveDelegate $MoveDelegate
        }
        catch {
            throw (
                "Swapping the candidate into place FAILED, and rolling back to the previous deployment ALSO FAILED. " +
                "'$DestinationDirectory' does not currently exist and NOTHING will recreate it automatically. " +
                "Your previous deployment - including the only copy of the deployed tenant profiles " +
                "(appsettings.<tenant>.json) and oauth-tokens/ - is intact at '$backupPath'; rename it back onto " +
                "'$DestinationDirectory' to recover. The assembled candidate is retained at '$CandidateDirectory'. " +
                "Swap error: $swapError. Rollback error: $($_.Exception.Message)."
            )
        }
        throw "Swapping the candidate into place failed and has been rolled back to the pre-existing destination (byte-identical to before this run): $swapError. The assembled candidate is retained at '$CandidateDirectory' for inspection."
    }

    try {
        Remove-Item -LiteralPath $backupPath -Recurse -Force
    }
    catch {
        Write-Warning "Deploy succeeded, but removing the temporary backup '$backupPath' failed: $($_.Exception.Message)"
    }
}

function Invoke-DestinationDeploy {
    param(
        [Parameter(Mandatory)] [string] $StagedDirectory,
        [Parameter(Mandatory)] [string] $DestinationDirectory,
        [Parameter(Mandatory)] [string] $State,
        [scriptblock] $MoveDelegate = $script:DefaultMoveDelegate,
        [scriptblock] $ProcessEnumerationDelegate = $script:DefaultProcessEnumerationDelegate
    )

    if ($State -eq 'Unrecognized') {
        throw (Get-UnrecognizedDestinationMessage -DestinationDirectory $DestinationDirectory `
                -Refusal "Refusing to deploy over a directory this script does not recognize as one of its own deployments. Move it aside, or point -DestinationDirectory somewhere else.")
    }

    $executablePath = Join-Path $DestinationDirectory $script:ExecutableName

    if ($State -eq 'Missing') {
        # Assembled as a same-parent SIBLING candidate and renamed into place - NOT a raw
        # move of $StagedDirectory itself. Staging may live on a different volume, and
        # Directory.Move cannot cross volumes; renaming staged itself would also CONSUME it,
        # unlike the other two branches, which leave it intact for inspection.
        Write-Detail 'Fresh install: nothing to preserve; the staged tenant profiles seed the deployment.'
        $candidateDirectory = New-CandidateDirectory -DestinationDirectory $DestinationDirectory
        Copy-ReplaceSet -StagedDirectory $StagedDirectory -CandidateDirectory $candidateDirectory
        Confirm-PublishArtifact -PublishDirectory $candidateDirectory | Out-Null
        Invoke-AtomicMove -From $candidateDirectory -To $DestinationDirectory -MoveDelegate $MoveDelegate
        return
    }

    if ($State -eq 'Empty') {
        Write-Detail 'Empty destination: nothing to preserve; the staged tenant profiles seed the deployment.'
        $candidateDirectory = New-CandidateDirectory -DestinationDirectory $DestinationDirectory
        Copy-ReplaceSet -StagedDirectory $StagedDirectory -CandidateDirectory $candidateDirectory
        Confirm-PublishArtifact -PublishDirectory $candidateDirectory | Out-Null
        Invoke-CandidateSwap -CandidateDirectory $candidateDirectory -DestinationDirectory $DestinationDirectory -MoveDelegate $MoveDelegate
        return
    }

    # $State -eq 'Recognized' from here on.
    if (Test-ProcessHoldsPath -ExecutablePath $executablePath -ProcessEnumerationDelegate $ProcessEnumerationDelegate) {
        throw "Checkpoint A: the destination executable at '$executablePath' is currently running. Stop it before deploying, then retry."
    }

    $candidateDirectory = New-CandidateDirectory -DestinationDirectory $DestinationDirectory
    Copy-ReplaceSet -StagedDirectory $StagedDirectory -CandidateDirectory $candidateDirectory -ExistingDestinationDirectory $DestinationDirectory

    if (Test-ProcessHoldsPath -ExecutablePath $executablePath -ProcessEnumerationDelegate $ProcessEnumerationDelegate) {
        throw "Checkpoint B: the destination executable at '$executablePath' started running before its preserved data could be read. The assembled candidate is retained at '$candidateDirectory' for inspection; the destination was not touched."
    }

    $preserved = Copy-PreserveSet -DestinationDirectory $DestinationDirectory -CandidateDirectory $candidateDirectory
    if ($preserved.Count -gt 0) {
        Write-Detail "Preserved from the live deployment: $($preserved -join ', ')"
    }
    else {
        Write-Detail 'Preserved from the live deployment: (nothing matched the preserve set)'
    }

    Confirm-PublishArtifact -PublishDirectory $candidateDirectory | Out-Null

    if (Test-ProcessHoldsPath -ExecutablePath $executablePath -ProcessEnumerationDelegate $ProcessEnumerationDelegate) {
        throw "Checkpoint C: the destination executable at '$executablePath' started running immediately before the swap. The assembled candidate is retained at '$candidateDirectory' for inspection; the destination was not touched."
    }

    Invoke-CandidateSwap -CandidateDirectory $candidateDirectory -DestinationDirectory $DestinationDirectory -MoveDelegate $MoveDelegate
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

# Wrapped in a named function so that dot-sourcing this script only DEFINES functions and
# never runs dotnet or mutates a destination as a side effect. See the guard at the bottom.
function Invoke-Main {
    param(
        [bool] $StagedDirectoryWasExplicit = $false
    )

    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath

    $project = $ProjectFile
    if ([string]::IsNullOrWhiteSpace($project)) {
        $project = Join-Path $repoRoot 'samples/CodeReviewDaemon.Sample/CodeReviewDaemon.Sample.csproj'
    }

    $stagingParent = $StagingRoot
    if ([string]::IsNullOrWhiteSpace($stagingParent)) {
        $stagingParent = Join-Path $repoRoot '.claude/scratchpad/codereviewdaemon-publish'
    }

    $destination = Resolve-DestinationDirectory -DestinationDirectory $DestinationDirectory

    Write-Phase "Classifying destination"
    Write-Detail "Destination: $destination"
    $state = (Test-DestinationState -DestinationDirectory $destination).State
    Write-Detail "Destination state: $state"

    # Classify and refuse BEFORE building. An Unrecognized destination must cost the
    # operator a message, not a five-minute publish followed by a message.
    if ($state -eq 'Unrecognized') {
        throw (Get-UnrecognizedDestinationMessage -DestinationDirectory $destination `
                -Refusal "Refusing to deploy over a directory this script does not recognize as one of its own deployments. Move it aside, or point -DestinationDirectory somewhere else.")
    }
    if ($state -eq 'Missing') {
        Assert-NoOrphanedDeploySiblings -DestinationDirectory $destination
    }
    if ($state -eq 'Recognized') {
        $executablePath = Join-Path $destination $script:ExecutableName
        if (Test-ProcessHoldsPath -ExecutablePath $executablePath -ProcessEnumerationDelegate $script:DefaultProcessEnumerationDelegate) {
            throw "Checkpoint A: the destination executable at '$executablePath' is currently running. Stop it before deploying, then retry."
        }
    }

    if ($StagedDirectoryWasExplicit) {
        $staged = [System.IO.Path]::GetFullPath(
            [System.IO.Path]::Combine((Get-Location).ProviderPath, $StagedDirectory))
        Write-Phase "Using a pre-published artifact"
        Write-Detail "Staged: $staged"
        if (-not (Test-Path -LiteralPath $staged -PathType Container)) {
            throw "-StagedDirectory '$staged' does not exist or is not a directory."
        }
    }
    else {
        Write-Phase "Publishing $Configuration"
        Write-Detail "Project: $project"
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
            throw "Project file not found: '$project'."
        }
        if (-not (Test-Path -LiteralPath $stagingParent)) {
            New-Item -ItemType Directory -Path $stagingParent -Force | Out-Null
        }
        $staged = New-PublishRunDirectory -StagingRoot $stagingParent
        Write-Detail "Staging: $staged"
        Invoke-ApplicationPublish -ProjectFile $project -Configuration $Configuration -OutputDirectory $staged
    }

    Confirm-PublishArtifact -PublishDirectory $staged | Out-Null

    Write-Phase "Deploying"
    Invoke-DestinationDeploy -StagedDirectory $staged -DestinationDirectory $destination -State $state

    Write-Phase "Done"
    Write-Host "    Deployed: $destination" -ForegroundColor Green
    Write-Host "    Launch:   $destination\$script:ExecutableName --review <tenant>" -ForegroundColor Green
}

# Dot-source guard: ". ./scripts/publish-daemon.ps1" defines the functions above for
# testing and executes nothing.
if ($MyInvocation.InvocationName -ne '.') {
    Invoke-Main -StagedDirectoryWasExplicit $script:StagedDirectoryWasExplicit
}
