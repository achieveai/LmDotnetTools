#requires -Version 7.5
<#
.SYNOPSIS
Captures stopped Windows Reviewd state without opening source SQLite databases.
.DESCRIPTION
Requires reviewed quiescence evidence bound to an explicit scope. This script does not stop
writers, authenticate the operator's claims, restore state, or authorize an upgrade. See RUNBOOK.md.
Python 3 is used only to verify separate working copies. Failed captures are retained.
#>
[CmdletBinding()]
param(
    [string]$ScopePath,
    [string]$QuiescencePath,
    [string]$ReviewedQuiescenceSha256,
    [string]$Destination,
    [string]$PythonPath = 'python'
)

$ErrorActionPreference = 'Stop'

function Assert-OfflinePath([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'AbsolutePathRequired' }
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'ReparsePoint'
        }
        $next = Split-Path $cursor -Parent
        if ($next -eq $cursor) { break }
        $cursor = $next
    }
}

function Test-OfflineOverlap([string]$Left, [string]$Right) {
    $a = [IO.Path]::GetFullPath($Left).TrimEnd('\', '/')
    $b = [IO.Path]::GetFullPath($Right).TrimEnd('\', '/')
    return $a.Equals($b, [StringComparison]::OrdinalIgnoreCase) -or
        $a.StartsWith($b + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $b.StartsWith($a + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-OfflineAccess([string]$Path) {
    $acl = Get-Acl -LiteralPath $Path
    $sid = [Security.Principal.SecurityIdentifier]
    $rules = @($acl.Access | ForEach-Object {
        '{0}|{1}|{2}|{3}|{4}' -f $_.IdentityReference.Translate($sid).Value,
            [int]$_.FileSystemRights, $_.AccessControlType, $_.InheritanceFlags, $_.PropagationFlags
    } | Sort-Object)
    return (@($acl.GetOwner($sid).Value, $acl.GetGroup($sid).Value) + $rules) -join "`n"
}

function Protect-OfflinePath([string]$From, [string]$To) {
    $acl = Get-Acl -LiteralPath $From
    $acl.SetAccessRuleProtection($true, $true)
    Set-Acl -LiteralPath $To -AclObject $acl
    if (-not (Get-Acl -LiteralPath $To).AreAccessRulesProtected -or
        (Get-OfflineAccess $From) -cne (Get-OfflineAccess $To)) { throw 'AccessProtectionMismatch' }
}

function New-OfflineDirectory([string]$From, [string]$To) {
    Assert-OfflinePath $From
    Assert-OfflinePath $To
    # No -Force: an existing destination, including one created in a race, is never reused.
    $null = New-Item -ItemType Directory -Path $To -ErrorAction Stop
    Protect-OfflinePath $From $To
}

function Copy-OfflineFile($From, $To, $InputStream) {
    Assert-OfflinePath $From
    Assert-OfflinePath $To
    $empty = [IO.File]::Open($To, [IO.FileMode]::CreateNew)
    $empty.Dispose()
    # File-to-file ACL copy is checked while empty, before any private byte reaches the destination.
    Protect-OfflinePath $From $To
    $ownedInput = $null -eq $InputStream
    if ($ownedInput) {
        $InputStream = [IO.File]::Open($From, 'Open', 'Read', [IO.FileShare]::Read)
    }
    try {
        $InputStream.Position = 0
        $output = [IO.File]::Open($To, 'Open', 'Write', [IO.FileShare]::None)
        try { $InputStream.CopyTo($output); $output.Flush($true) } finally { $output.Dispose() }
    } finally { if ($ownedInput) { $InputStream.Dispose() } }
}

function Write-OfflineJson([string]$Path, $Value, [string]$AccessSource) {
    $file = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    $file.Dispose()
    Protect-OfflinePath $AccessSource $Path
    $file = [IO.File]::Open($Path, 'Open', 'Write', [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 20))
        $file.Write($bytes); $file.Flush($true)
    } finally { $file.Dispose() }
}

function Assert-OfflineScope($Scope, [string]$Destination) {
    if (($Scope.schema -isnot [long] -and $Scope.schema -isnot [int]) -or $Scope.schema -ne 1 -or
        @($Scope.databases).Count -lt 4 -or @($Scope.entries).Count -lt 1) {
        throw 'InvalidScope'
    }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $paths = [Collections.Generic.List[string]]::new()
    foreach ($item in @($Scope.databases) + @($Scope.entries)) {
        if ($item.name -isnot [string] -or $item.name -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]*$' -or
            -not $names.Add($item.name)) { throw 'InvalidScopeName' }
        Assert-OfflinePath $item.path
        if (-not (Test-Path -LiteralPath $item.path)) { throw 'RequiredSourceMissing' }
        if (Test-OfflineOverlap $item.path $Destination) { throw 'PathOverlap' }
        foreach ($previous in $paths) {
            if (Test-OfflineOverlap $previous $item.path) { throw 'PathOverlap' }
        }
        $paths.Add($item.path)
    }
    foreach ($required in @('conversations', 'identity', 'github', 'ado')) {
        if (@($Scope.databases | Where-Object name -ceq $required).Count -ne 1) { throw 'DatabaseRoleMissing' }
    }
    foreach ($db in $Scope.databases) {
        if (-not (Test-Path -LiteralPath $db.path -PathType Leaf)) { throw 'DatabaseFileRequired' }
        # Absent sidecars still belong to SQLite: a capture directory must never occupy their names.
        foreach ($suffix in @('-wal', '-journal', '-shm')) {
            if (Test-OfflineOverlap ($db.path + $suffix) $Destination) { throw 'PathOverlap' }
        }
    }
    foreach ($property in @('processPaths', 'commandPaths', 'tasks', 'ports')) {
        if (-not $Scope.runtime.PSObject.Properties[$property] -or $Scope.runtime.$property -isnot [array]) {
            throw 'RuntimeScopeRequired'
        }
    }
    foreach ($path in @($Scope.runtime.processPaths) + @($Scope.runtime.commandPaths)) { Assert-OfflinePath $path }
    foreach ($task in $Scope.runtime.tasks) {
        if ($task -isnot [string] -or [string]::IsNullOrWhiteSpace($task)) { throw 'InvalidTaskScope' }
    }
    foreach ($port in $Scope.runtime.ports) {
        if ($port -isnot [long] -and $port -isnot [int]) { throw 'InvalidPortScope' }
        if ($port -lt 1 -or $port -gt 65535) { throw 'InvalidPortScope' }
    }
}

function Assert-OfflineQuiescence($Proof, [string]$ScopeHash) {
    if (($Proof.schema -isnot [long] -and $Proof.schema -isnot [int]) -or $Proof.schema -ne 1 -or $Proof.scopeSha256 -isnot [string] -or
        $Proof.scopeSha256 -ine $ScopeHash) { throw 'QuiescenceScopeMismatch' }
    foreach ($name in @('producersDisabled', 'writersStopped')) {
        if ($Proof.$name -isnot [bool] -or $Proof.$name -ne $true) { throw 'UnsafeQuiescence' }
    }
    # A valid saved-state capture can preserve evidence after failed final writes. It is not a cutover gate.
    if ($Proof.shutdownDurabilityPassed -isnot [bool]) { throw 'InvalidDurabilityEvidence' }
    if ($Proof.unknownWriters -isnot [bool] -or $Proof.unknownWriters -ne $false) { throw 'UnknownWriters' }
    foreach ($name in @('owners', 'unreleasedAttempts')) {
        if (($Proof.$name -isnot [long] -and $Proof.$name -isnot [int]) -or $Proof.$name -ne 0) {
            throw 'ActiveReviews'
        }
    }
    $observed = [datetimeoffset]::MinValue
    if ($Proof.observedAtUtc -isnot [string] -or $Proof.observedAtUtc -notmatch '(Z|\+00:00)$' -or
        -not [datetimeoffset]::TryParse($Proof.observedAtUtc, [ref]$observed)) { throw 'InvalidQuiescenceTime' }
    $age = ([datetimeoffset]::UtcNow - $observed).TotalSeconds
    if ($age -lt 0 -or $age -gt 600) { throw 'StaleQuiescence' }
}

function Assert-OfflineProducers($Runtime) {
    if (@($Runtime.processPaths).Count -or @($Runtime.commandPaths).Count) {
        foreach ($process in Get-CimInstance Win32_Process -ErrorAction Stop) {
            foreach ($path in $Runtime.processPaths) {
                if ($process.ExecutablePath -ieq $path -or
                    ($process.Name -ieq [IO.Path]::GetFileName($path) -and -not $process.ExecutablePath)) {
                    throw 'WriterPresentOrUnreadable'
                }
            }
            foreach ($path in $Runtime.commandPaths) {
                if ($process.CommandLine -and $process.CommandLine.IndexOf($path, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    throw 'ProducerPresent'
                }
            }
        }
    }
    foreach ($name in $Runtime.tasks) {
        $tasks = @(Get-ScheduledTask -TaskName $name -ErrorAction Stop)
        if ($tasks.Count -ne 1 -or $tasks[0].State -ne 'Disabled') { throw 'ProducerTaskEnabledOrAmbiguous' }
    }
    if (@($Runtime.tasks).Count) {
        $scheduler = New-Object -ComObject 'Schedule.Service'
        $scheduler.Connect()
        foreach ($instance in $scheduler.GetRunningTasks(0)) {
            if ($instance.Name -in $Runtime.tasks) { throw 'ProducerTaskRunning' }
        }
    }
    foreach ($port in $Runtime.ports) {
        if (@(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -eq $port).Count) {
            throw 'ListenerPresent'
        }
    }
}

function Get-OfflineSnapshot($Scope) {
    $items = [Collections.Generic.List[object]]::new()
    foreach ($db in $Scope.databases) {
        foreach ($suffix in @('', '-wal', '-journal')) {
            $items.Add([pscustomobject]@{ source = $db.path + $suffix; relative = 'databases/' + $db.name + '.db' + $suffix })
        }
    }
    foreach ($entry in $Scope.entries) {
        $items.Add([pscustomobject]@{ source = $entry.path; relative = 'entries/' + $entry.name })
        if (Test-Path -LiteralPath $entry.path -PathType Container) {
            # Walk one directory at a time: reject a link before descending into it.
            $pending = [Collections.Generic.Queue[string]]::new()
            $pending.Enqueue($entry.path)
            while ($pending.Count) {
                foreach ($child in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
                    Assert-OfflinePath $child.FullName
                    $items.Add([pscustomobject]@{
                        source = $child.FullName
                        relative = 'entries/' + $entry.name + '/' + [IO.Path]::GetRelativePath($entry.path, $child.FullName).Replace('\', '/')
                    })
                    if ($child.PSIsContainer) { $pending.Enqueue($child.FullName) }
                }
            }
        }
    }
    foreach ($item in $items | Sort-Object relative) {
        Assert-OfflinePath $item.source
        $exists = Test-Path -LiteralPath $item.source
        $directory = $exists -and (Test-Path -LiteralPath $item.source -PathType Container)
        [pscustomobject][ordered]@{
            relative = $item.relative; source = $item.source; exists = $exists; directory = $directory
            bytes = $(if ($exists -and -not $directory) { (Get-Item -LiteralPath $item.source -Force).Length } else { 0 })
            sha256 = $(if ($exists -and -not $directory) { (Get-FileHash -LiteralPath $item.source -Algorithm SHA256).Hash } else { $null })
            access = $(if ($exists) { Get-OfflineAccess $item.source } else { $null })
        }
    }
}

function Get-OfflineRawHashes([string]$Raw) {
    $scope = [pscustomobject]@{ databases = @(); entries = @([pscustomobject]@{ name = 'raw'; path = $Raw }) }
    @(Get-OfflineSnapshot $scope | Select-Object relative, exists, directory, bytes, sha256, access) |
        ConvertTo-Json -Depth 5 -Compress
}

function Invoke-OfflineBackup {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ScopePath,
        [Parameter(Mandatory)][string]$QuiescencePath,
        [Parameter(Mandatory)][string]$ReviewedQuiescenceSha256,
        [Parameter(Mandatory)][string]$Destination,
        [string]$PythonPath = 'python'
    )
    if (-not $IsWindows) { throw 'WindowsRequired' }
    foreach ($path in @($ScopePath, $QuiescencePath, $Destination)) { Assert-OfflinePath $path }
    if (Test-Path -LiteralPath $Destination) { throw 'DestinationExists' }
    if (-not (Test-Path -LiteralPath (Split-Path $Destination -Parent) -PathType Container)) { throw 'DestinationParentMissing' }
    if ($ReviewedQuiescenceSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        (Get-FileHash -LiteralPath $QuiescencePath).Hash -ine $ReviewedQuiescenceSha256) { throw 'QuiescenceHashMismatch' }
    $scopeHash = (Get-FileHash -LiteralPath $ScopePath).Hash
    $scope = Get-Content -LiteralPath $ScopePath -Raw | ConvertFrom-Json -DateKind String
    $proof = Get-Content -LiteralPath $QuiescencePath -Raw | ConvertFrom-Json -DateKind String
    Assert-OfflineScope $scope $Destination
    Assert-OfflineQuiescence $proof $scopeHash
    Assert-OfflineProducers $scope.runtime
    $before = @(Get-OfflineSnapshot $scope)
    $baseline = $before | ConvertTo-Json -Depth 8 -Compress
    $handles = @{}
    $created = $false
    $accessSource = $scope.databases[0].path
    $directoryAccessSource = Split-Path $accessSource -Parent
    try {
        foreach ($file in $before | Where-Object { $_.exists -and -not $_.directory }) {
            # A writer already holding an incompatible handle causes capture to stop here.
            $handles[$file.relative] = [IO.File]::Open($file.source, 'Open', 'Read', [IO.FileShare]::Read)
        }
        if ((@(Get-OfflineSnapshot $scope) | ConvertTo-Json -Depth 8 -Compress) -cne $baseline) { throw 'SourceDrift' }
        New-OfflineDirectory $directoryAccessSource $Destination
        $created = $true
        Write-OfflineJson (Join-Path $Destination 'source-inventory.json') $before $accessSource
        Copy-OfflineFile $ScopePath (Join-Path $Destination 'scope.json') $null
        Copy-OfflineFile $QuiescencePath (Join-Path $Destination 'quiescence.json') $null
        if ((Get-FileHash -LiteralPath (Join-Path $Destination 'scope.json')).Hash -ine $scopeHash -or
            (Get-FileHash -LiteralPath (Join-Path $Destination 'quiescence.json')).Hash -ine $ReviewedQuiescenceSha256) {
            throw 'InputEvidenceDrift'
        }
        $raw = Join-Path $Destination 'raw'
        foreach ($path in @($raw, (Join-Path $raw 'databases'), (Join-Path $raw 'entries'))) {
            New-OfflineDirectory $directoryAccessSource $path
        }
        foreach ($item in $before | Where-Object exists) {
            $target = Join-Path $raw $item.relative
            if ($item.directory) { New-OfflineDirectory $item.source $target }
            else {
                Copy-OfflineFile $item.source $target $handles[$item.relative]
                if ((Get-FileHash -LiteralPath $target).Hash -cne $item.sha256) { throw 'CopyHashMismatch' }
            }
        }
        if ((@(Get-OfflineSnapshot $scope) | ConvertTo-Json -Depth 8 -Compress) -cne $baseline) { throw 'SourceDrift' }
        Assert-OfflineQuiescence $proof $scopeHash
        Assert-OfflineProducers $scope.runtime
        $rawHashes = Get-OfflineRawHashes $raw
        $working = Join-Path $Destination 'working'
        New-OfflineDirectory $directoryAccessSource $working
        foreach ($db in $scope.databases) {
            foreach ($suffix in @('', '-wal', '-journal')) {
                $saved = Join-Path $raw ('databases/' + $db.name + '.db' + $suffix)
                if (Test-Path -LiteralPath $saved) { Copy-OfflineFile $saved (Join-Path $working ($db.name + '.db' + $suffix)) $null }
            }
        }
        # Only this isolated directory reaches SQLite. The verifier never sees a source path.
        $verification = & $PythonPath (Join-Path $PSScriptRoot 'verify-offline-databases.py') $working 2>&1
        if ($LASTEXITCODE -ne 0) { throw 'WorkingCopyVerificationFailed' }
        $databases = ($verification -join "`n") | ConvertFrom-Json
        if (@($databases).Count -ne @($scope.databases).Count) { throw 'DatabaseVerificationCountMismatch' }
        if ((Get-OfflineRawHashes $raw) -cne $rawHashes) { throw 'RawEvidenceChanged' }
        if ((@(Get-OfflineSnapshot $scope) | ConvertTo-Json -Depth 8 -Compress) -cne $baseline) { throw 'SourceDrift' }
        Assert-OfflineQuiescence $proof $scopeHash
        Assert-OfflineProducers $scope.runtime
        Write-OfflineJson (Join-Path $Destination 'complete.json') ([ordered]@{
            schema = 1; complete = $true; completedAtUtc = [datetimeoffset]::UtcNow.ToString('o')
            scopeSha256 = $scopeHash; quiescenceSha256 = $ReviewedQuiescenceSha256
            shutdownDurabilityPassed = $proof.shutdownDurabilityPassed
            databases = @($databases); rawEntries = ($rawHashes | ConvertFrom-Json)
            limitation = 'Saved-state integrity only; operator-supplied quiescence is not authenticated. No restore or deployment performed.'
        }) $accessSource
        [pscustomobject]@{ complete = $true; databases = @($databases).Count }
    } catch {
        if ($created) {
            Write-OfflineJson (Join-Path $Destination 'failed.json') ([ordered]@{
                schema = 1; complete = $false; failedAtUtc = [datetimeoffset]::UtcNow.ToString('o')
                errorType = $_.Exception.GetType().FullName; reason = $_.Exception.Message
                scopeSha256 = $scopeHash; quiescenceSha256 = $ReviewedQuiescenceSha256
            }) $accessSource
        }
        throw
    } finally { foreach ($handle in $handles.Values) { $handle.Dispose() } }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-OfflineBackup @PSBoundParameters | ConvertTo-Json -Compress
}
