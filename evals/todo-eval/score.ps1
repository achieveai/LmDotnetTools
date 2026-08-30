<#
.SYNOPSIS
Deterministic scoring oracle for the todo-eval Testing Mode (#618).

.DESCRIPTION
Computes the score object defined in metrics-spec.md from a conversation-store directory
plus a final board snapshot. No LLM judging. This is the manual-validation reference
implementation; the #619 harness reimplements the same spec in C#.

.PARAMETER ConversationsDir
The host's conversations/ directory for the run. Every immediate subdirectory containing
a messages.json (primary thread-* and every subagent-*) is scored.

.PARAMETER BoardSnapshot
Path to the final board JSON: either a raw TodoBoardSnapshot ({ "Tasks": [...] } — the
GET /api/conversations/{id}/todos payload or the todo.board value), or a thread
metadata.json whose properties["todo.board"] string holds the snapshot.

.PARAMETER Fixture
Path to the expected-board fixture. Defaults to expected-board.json next to this script.

.PARAMETER OutFile
Optional path to also write the score JSON to.

.EXAMPLE
pwsh ./score.ps1 -ConversationsDir C:\run\conversations -BoardSnapshot C:\run\board.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConversationsDir,
    [Parameter(Mandatory)][string]$BoardSnapshot,
    [string]$Fixture = (Join-Path $PSScriptRoot 'expected-board.json'),
    [string]$OutFile
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$TaskTools = @(
    'add-task', 'bulk-initialize', 'update-task', 'claim-task', 'assign-task',
    'block-task', 'attach-artifact', 'delete-task', 'get-task', 'add-note',
    'edit-note', 'delete-note', 'list-notes', 'list-tasks', 'search-tasks'
)

# --- helpers ---------------------------------------------------------------------------

function Get-Prop {
    # Case-insensitive property lookup over the hashtables ConvertFrom-Json -AsHashtable
    # yields (their key comparer is not guaranteed insensitive across PS versions).
    param($Node, [string]$Name)
    if ($null -eq $Node) { return $null }
    if ($Node -is [System.Collections.IDictionary]) {
        foreach ($k in $Node.Keys) {
            if ([string]::Equals([string]$k, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $Node[$k]
            }
        }
        return $null
    }
    return $null
}

function ConvertTo-JsonScalar {
    # System.Text.Json scalar emission. The (value, inputType, options) overload is used
    # because PowerShell cannot bind the generic Serialize<TValue>(value) overload directly.
    param($Value)
    return [System.Text.Json.JsonSerializer]::Serialize(
        $Value,
        $Value.GetType(),
        [System.Text.Json.JsonSerializerOptions]$null
    )
}

function ConvertTo-CanonicalJson {
    # Canonical serialization per metrics-spec.md: keys ordinal-sorted at every level,
    # compact output, scalars re-emitted by System.Text.Json.
    param($Node)
    if ($null -eq $Node) { return 'null' }
    if ($Node -is [bool]) { if ($Node) { return 'true' } else { return 'false' } }
    if ($Node -is [string]) { return ConvertTo-JsonScalar $Node }
    if ($Node -is [System.Collections.IDictionary]) {
        $keys = @($Node.Keys | ForEach-Object { [string]$_ })
        [System.Array]::Sort($keys, [System.StringComparer]::Ordinal)
        $parts = foreach ($k in $keys) {
            (ConvertTo-JsonScalar ([string]$k)) + ':' + (ConvertTo-CanonicalJson $Node[$k])
        }
        return '{' + ($parts -join ',') + '}'
    }
    if ($Node -is [System.Collections.IEnumerable]) {
        $parts = foreach ($item in $Node) { ConvertTo-CanonicalJson $item }
        return '[' + ($parts -join ',') + ']'
    }
    # Numbers, DateTime, anything else scalar.
    return ConvertTo-JsonScalar $Node
}

function Get-CanonicalArgs {
    param([string]$Raw)
    if ([string]::IsNullOrEmpty($Raw)) { return '' }
    # Only a PARSE failure falls back to the raw string (per metrics-spec.md). The
    # canonicalizer itself runs outside the try: a defect there must surface, not silently
    # degrade every identity to raw-string comparison.
    try {
        $parsed = ConvertFrom-Json -InputObject $Raw -AsHashtable -Depth 64
    }
    catch {
        return $Raw
    }
    return ConvertTo-CanonicalJson $parsed
}

function Test-ErrorResult {
    # Error result per spec (defensive union): is_error == true on the result message, OR
    # result text, leading whitespace removed, starts with "Error:" (ordinal,
    # case-sensitive). Non-string results are serialized to compact JSON first. Production
    # data records is_error: false on textual errors, so the text prefix is the primary
    # signal; is_error is honoured in case the store ever starts setting it.
    param($Result, $IsErrorFlag)
    if ($IsErrorFlag -eq $true) { return $true }
    if ($null -eq $Result) { return $false }
    $text = if ($Result -is [string]) { $Result } else { ConvertTo-CanonicalJson $Result }
    return $text.TrimStart().StartsWith('Error:', [System.StringComparison]::Ordinal)
}

function Get-BoardTasks {
    # Accepts a raw TodoBoardSnapshot or a metadata.json; returns the top-level task array.
    param([string]$Path)
    $root = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $Path -Raw) -AsHashtable -Depth 64
    $props = Get-Prop $root 'properties'
    if ($null -ne $props) {
        $boardString = Get-Prop $props 'todo.board'
        if ($null -eq $boardString) {
            throw "Board snapshot '$Path' looks like a metadata.json but has no properties['todo.board']."
        }
        $root = ConvertFrom-Json -InputObject ([string]$boardString) -AsHashtable -Depth 64
    }
    $tasks = Get-Prop $root 'Tasks'
    if ($null -eq $tasks) {
        throw "Board snapshot '$Path' has no Tasks array."
    }
    return @($tasks)
}

function Get-FlatTasks {
    # Flattens the task tree into rows of { Depth, Status, NoteCount, ChildCount, Blocked }.
    param($Tasks, [int]$Depth = 1)
    foreach ($t in @($Tasks)) {
        $children = @(Get-Prop $t 'subTasks')
        $notes = @(Get-Prop $t 'notes')
        $status = [string](Get-Prop $t 'status')
        [pscustomobject]@{
            Depth      = $Depth
            Status     = $status
            NoteCount  = $notes.Count
            ChildCount = $children.Count
        }
        if ($children.Count -gt 0) {
            Get-FlatTasks -Tasks $children -Depth ($Depth + 1)
        }
    }
}

# --- load inputs -----------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $ConversationsDir -PathType Container)) {
    throw "ConversationsDir '$ConversationsDir' is not a directory."
}
$fixtureData = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $Fixture -Raw) -AsHashtable -Depth 32
$boardTasks = Get-BoardTasks -Path $BoardSnapshot

$threadDirs = @(
    Get-ChildItem -LiteralPath $ConversationsDir -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'messages.json') -PathType Leaf } |
        Sort-Object Name
)

# --- walk the conversation store -------------------------------------------------------

$totalToolCalls = 0
$unpairedToolCalls = 0
$perTool = [ordered]@{}
foreach ($t in $TaskTools) {
    $perTool[$t] = @{ calls = 0; errors = 0 }
}
$storms = [System.Collections.Generic.List[object]]::new()
$blockRecorded = $false
$blockExplicitlyCleared = $false
$turns = 0
$primaryTurns = 0
$subAgentThreads = 0
$subAgentsWithoutTaskToolCalls = [System.Collections.Generic.List[string]]::new()
$fabricatedComplianceSuspects = [System.Collections.Generic.List[string]]::new()

foreach ($dir in $threadDirs) {
    $envelopes = ConvertFrom-Json -InputObject (Get-Content -LiteralPath (Join-Path $dir.FullName 'messages.json') -Raw) -AsHashtable -Depth 64
    $threadId = $dir.Name

    # Pass 1: pair results by tool_call_id (within this thread only).
    $resultsById = @{}
    foreach ($env in @($envelopes)) {
        if ((Get-Prop $env 'messageType') -ne 'ToolCallResultMessage') { continue }
        $inner = ConvertFrom-Json -InputObject ([string](Get-Prop $env 'messageJson')) -AsHashtable -Depth 64
        $callId = [string](Get-Prop $inner 'tool_call_id')
        if (-not [string]::IsNullOrEmpty($callId) -and -not $resultsById.ContainsKey($callId)) {
            $resultsById[$callId] = @{
                Result  = Get-Prop $inner 'result'
                IsError = Get-Prop $inner 'is_error'
            }
        }
    }

    # Pass 2: calls in message order + turns.
    $generationIds = [System.Collections.Generic.HashSet[string]]::new()
    $streaks = @{}   # identity -> current consecutive-failure run length
    $threadTaskToolCalls = 0
    $assistantTexts = [System.Collections.Generic.List[string]]::new()
    foreach ($env in @($envelopes)) {
        $messageType = [string](Get-Prop $env 'messageType')
        $genId = Get-Prop $env 'generationId'
        $inner = $null
        if ($null -eq $genId -or $messageType -eq 'ToolCallMessage' -or $messageType -eq 'TextMessage') {
            try {
                $inner = ConvertFrom-Json -InputObject ([string](Get-Prop $env 'messageJson')) -AsHashtable -Depth 64
            }
            catch { $inner = $null }
        }
        if ($null -eq $genId -and $null -ne $inner) {
            $genId = Get-Prop $inner 'generationId'
            if ($null -eq $genId) { $genId = Get-Prop $inner 'generation_id' }
        }
        if ($null -ne $genId) { [void]$generationIds.Add([string]$genId) }

        if ($messageType -eq 'TextMessage' -and $null -ne $inner -and
            [string]::Equals([string](Get-Prop $env 'role'), 'Assistant', [System.StringComparison]::OrdinalIgnoreCase)) {
            $text = Get-Prop $inner 'text'
            if ($text -is [string]) { $assistantTexts.Add($text) }
        }

        if ($messageType -ne 'ToolCallMessage' -or $null -eq $inner) { continue }

        $tool = [string](Get-Prop $inner 'function_name')
        $callId = [string](Get-Prop $inner 'tool_call_id')
        $totalToolCalls++

        $hasResult = -not [string]::IsNullOrEmpty($callId) -and $resultsById.ContainsKey($callId)
        if (-not $hasResult) { $unpairedToolCalls++ }
        $isError = $hasResult -and (Test-ErrorResult -Result $resultsById[$callId].Result -IsErrorFlag $resultsById[$callId].IsError)

        if ($TaskTools -cnotcontains $tool) { continue }

        $threadTaskToolCalls++
        $perTool[$tool].calls++
        if ($isError) { $perTool[$tool].errors++ }

        $canonical = Get-CanonicalArgs ([string](Get-Prop $inner 'function_args'))
        $identity = $tool + "`n" + $canonical

        if ($isError) {
            if (-not $streaks.ContainsKey($identity)) { $streaks[$identity] = 0 }
            $streaks[$identity]++
        }
        elseif ($hasResult) {
            # A successful occurrence of the SAME identity ends its run.
            if ($streaks.ContainsKey($identity)) {
                if ($streaks[$identity] -ge 3) {
                    $storms.Add([ordered]@{ threadId = $threadId; tool = $tool; count = $streaks[$identity]; args = $canonical })
                }
                $streaks[$identity] = 0
            }

            if ($tool -eq 'block-task') {
                $parsedArgs = $null
                try { $parsedArgs = ConvertFrom-Json -InputObject ([string](Get-Prop $inner 'function_args')) -AsHashtable -Depth 64 } catch {}
                # An OMITTED blockedBy is the documented CLEAR form (BlockTaskCore treats
                # null like []). Null-check before wrapping: @($null).Count is 1, which
                # would score a clear call as a recorded block.
                $blockedByRaw = Get-Prop $parsedArgs 'blockedBy'
                $blockedByCount = if ($null -eq $blockedByRaw) { 0 } else { @($blockedByRaw).Count }
                if ($blockedByCount -gt 0) { $blockRecorded = $true } else { $blockExplicitlyCleared = $true }
            }
        }
    }

    # Thread end: close out open runs.
    foreach ($identity in $streaks.Keys) {
        if ($streaks[$identity] -ge 3) {
            $tool, $canonical = $identity -split "`n", 2
            $storms.Add([ordered]@{ threadId = $threadId; tool = $tool; count = $streaks[$identity]; args = $canonical })
        }
    }

    $turns += $generationIds.Count
    if (-not $threadId.StartsWith('subagent-', [System.StringComparison]::OrdinalIgnoreCase)) {
        $primaryTurns += $generationIds.Count
    }
    else {
        # Validity precondition (#618, production finding): a sub-agent ordered to work the
        # board that makes ZERO task-tool calls almost certainly never HAD the task tools in
        # its schema (restricted agent-definition template) — the run is invalid, not merely
        # a low score. Fabricated compliance: the same tool-less transcript whose assistant
        # text still CLAIMS board actions.
        $subAgentThreads++
        if ($threadTaskToolCalls -eq 0) {
            $subAgentsWithoutTaskToolCalls.Add($threadId)
            $claims = $assistantTexts | Where-Object {
                $_ -match '(?i)(claim|complet|marked)' -and $_ -match '(?i)(task|todo|board)'
            }
            if (@($claims).Count -gt 0) {
                $fabricatedComplianceSuspects.Add($threadId)
            }
        }
    }
}

# --- evaluate the board against the fixture --------------------------------------------

$flat = @(Get-FlatTasks -Tasks $boardTasks)
$blockedCount = @($flat | Where-Object { [string]::Equals($_.Status, 'Blocked', [System.StringComparison]::OrdinalIgnoreCase) }).Count
$completionFailures = [System.Collections.Generic.List[string]]::new()
$fixtureBoard = Get-Prop $fixtureData 'board'
$fixtureConversation = Get-Prop $fixtureData 'conversation'

$expectedTop = Get-Prop $fixtureBoard 'topLevelTaskCount'
if ($null -ne $expectedTop) {
    $actualTop = @($flat | Where-Object { $_.Depth -eq 1 }).Count
    if ($actualTop -ne [int]$expectedTop) {
        $completionFailures.Add("topLevelTaskCount: expected $expectedTop, found $actualTop")
    }
}

$expectedCounts = Get-Prop $fixtureBoard 'subtaskCountsSorted'
if ($null -ne $expectedCounts) {
    $actualCounts = @($flat | Where-Object { $_.Depth -eq 1 } | ForEach-Object { $_.ChildCount } | Sort-Object)
    $expectedSorted = @(@($expectedCounts) | Sort-Object)
    if (($actualCounts -join ',') -ne ($expectedSorted -join ',')) {
        $completionFailures.Add("subtaskCountsSorted: expected [$($expectedSorted -join ',')], found [$($actualCounts -join ',')]")
    }
}

$level3 = Get-Prop $fixtureBoard 'level3'
if ($null -ne $level3) {
    $minParents = [int](Get-Prop $level3 'minParents')
    $minChildren = [int](Get-Prop $level3 'minChildrenPerParent')
    $parents = @($flat | Where-Object { $_.Depth -eq 2 -and $_.ChildCount -ge $minChildren }).Count
    if ($parents -lt $minParents) {
        $completionFailures.Add("level3: expected >= $minParents depth-2 task(s) with >= $minChildren children, found $parents")
    }
}

if ((Get-Prop $fixtureBoard 'allTasksCompleted') -eq $true) {
    $notCompleted = @($flat | Where-Object { -not [string]::Equals($_.Status, 'Completed', [System.StringComparison]::OrdinalIgnoreCase) }).Count
    if ($notCompleted -gt 0) {
        $completionFailures.Add("allTasksCompleted: $notCompleted task(s) not Completed")
    }
}

$minNotes = Get-Prop $fixtureBoard 'minNotesPerSubtask'
if ($null -ne $minNotes) {
    $short = @($flat | Where-Object { $_.Depth -ge 2 -and $_.NoteCount -lt [int]$minNotes }).Count
    if ($short -gt 0) {
        $completionFailures.Add("minNotesPerSubtask: $short subtask(s) have fewer than $minNotes note(s)")
    }
}

$maxBlocked = Get-Prop $fixtureBoard 'maxBlockedTasks'
if ($null -ne $maxBlocked -and $blockedCount -gt [int]$maxBlocked) {
    $completionFailures.Add("maxBlockedTasks: expected <= $maxBlocked, found $blockedCount")
}

# --- validity ---------------------------------------------------------------------------

$validityReasons = [System.Collections.Generic.List[string]]::new()
if ($threadDirs.Count -eq 0) {
    # A mis-pointed ConversationsDir must never enter a sweep as a valid failed run.
    $validityReasons.Add('no conversation threads found - harness misconfiguration')
}
if ($subAgentsWithoutTaskToolCalls.Count -gt 0) {
    $validityReasons.Add("sub-agent thread(s) with zero task-tool calls: $($subAgentsWithoutTaskToolCalls -join ', ')")
}
# NOTE (spec): subAgentThreads == 0 is VALID - spawning no sub-agents is model behavior
# under test, not harness breakage. It is surfaced as subAgentCount in the summary so
# sweep tables can segment such runs.

$blockCleared = $blockRecorded -and ($blockedCount -eq 0)
if ((Get-Prop $fixtureConversation 'requireBlockRecorded') -eq $true -and -not $blockRecorded) {
    $completionFailures.Add('requireBlockRecorded: no successful block-task call with a non-empty blockedBy was found')
}
if ((Get-Prop $fixtureConversation 'requireBlockCleared') -eq $true -and -not $blockCleared) {
    $completionFailures.Add('requireBlockCleared: the recorded block was never cleared (or was never recorded)')
}

# --- emit ------------------------------------------------------------------------------

$perToolOut = [ordered]@{}
$taskToolCalls = 0
$taskToolErrors = 0
foreach ($t in $TaskTools) {
    $calls = $perTool[$t].calls
    $errors = $perTool[$t].errors
    $taskToolCalls += $calls
    $taskToolErrors += $errors
    $rate = if ($calls -gt 0) { [math]::Round($errors / $calls, 4) } else { 0 }
    $perToolOut[$t] = [ordered]@{ calls = $calls; errors = $errors; errorRate = $rate }
}

$score = [ordered]@{
    schema             = 'todo-eval/score@1'
    conversationsDir   = (Resolve-Path -LiteralPath $ConversationsDir).Path
    threads            = $threadDirs.Count
    subAgentCount      = $subAgentThreads
    totalToolCalls     = $totalToolCalls
    taskToolCalls      = $taskToolCalls
    taskToolErrors     = $taskToolErrors
    unpairedToolCalls  = $unpairedToolCalls
    perTool            = $perToolOut
    retryStormCount    = $storms.Count
    retryStorms        = @($storms)
    blockRecorded      = $blockRecorded
    blockExplicitlyCleared = $blockExplicitlyCleared
    blockCleared       = $blockCleared
    completion         = ($completionFailures.Count -eq 0)
    completionFailures = @($completionFailures)
    turns              = $turns
    primaryTurns       = $primaryTurns
    validity           = [ordered]@{
        valid                          = ($validityReasons.Count -eq 0)
        reasons                        = @($validityReasons)
        subAgentThreads                = $subAgentThreads
        subAgentsWithoutTaskToolCalls  = @($subAgentsWithoutTaskToolCalls)
        fabricatedComplianceSuspects   = @($fabricatedComplianceSuspects)
    }
}

$json = ConvertTo-Json -InputObject $score -Depth 10
if ($OutFile) {
    Set-Content -LiteralPath $OutFile -Value $json -Encoding utf8NoBOM
}
$json
