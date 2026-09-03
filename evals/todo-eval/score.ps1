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

# The 7 sub-agent coordination tools (SubAgentToolProvider.AllToolNames): the supervisor-only
# surface plus the collaboration one. No single conversation is offered all seven, so the score
# emits a row for every one of them and lets the zeros say which were never available.
$CoordinationTools = @(
    'Agent', 'SendMessage', 'CheckAgent', 'WaitAgent',
    'CheckAgents', 'WaitForAgents', 'GetAgents'
)
$WaitTools = @('WaitAgent', 'WaitForAgents')
$RowOrder = @($TaskTools) + @($CoordinationTools)

$Schema = 'todo-eval/score@2'
$SpecVersion = 'todo-eval/metrics-spec@2'
$StormThreshold = 3
$UnclassifiedErrorCode = 'unclassified'
$RedactedArgsKey = '__argsSha256'
$CorpusFileNames = @('task.md', 'mode.json', 'expected-board.json')
$KindsNotEmitted = @('WorkflowController', 'WorkflowTask', 'Continuation')
$TurnJoinNote = 'Turn attribution is a HEURISTIC: UsageRecord carries no generation id, so a record is joined to a turn by stripping the owner-execution prefix from ProviderAttemptId and matching the remainder against the thread''s generation ids. Records whose attempt key is synthetic (derived:...) can never join and are counted in unattributedTurnTokens.'
$ToolFamilyNote = 'Tokens are NOT attributable to a tool family. A turn''s prompt carries every tool''s schema at once, so no per-family split exists in the data; none is invented here.'
$ObligationsNotEmittedNote = 'No coordination result carried an openObligations field: this build does not emit one yet (#673). The zero means NOT REPORTED, not ''none were open''.'

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

function Get-ToolFamily {
    # Ordinal, case-sensitive: the store records the exact function_name the host registered, and
    # treating 'checkagents' as a tool this host has would invent calls that never happened.
    param([string]$Tool)
    if ($TaskTools -ccontains $Tool) { return 'task' }
    if ($CoordinationTools -ccontains $Tool) { return 'coordination' }
    return 'other'
}

function Get-ResultText {
    # The result's text: the string value itself, or compact JSON for a non-string value.
    param($Result)
    if ($null -eq $Result) { return '' }
    if ($Result -is [string]) { return $Result }
    return ConvertTo-CanonicalJson $Result
}

function Get-ResultProperty {
    # Reads one property out of a result whose text is a JSON object. Cheap '{' gate first so the
    # ordinary textual result is never handed to the parser.
    param($Result, [string]$Name)
    $text = (Get-ResultText $Result).TrimStart()
    if (-not $text.StartsWith('{', [System.StringComparison]::Ordinal)) { return $null }
    try {
        $parsed = ConvertFrom-Json -InputObject $text -AsHashtable -Depth 64
    }
    catch {
        return $null
    }
    if ($parsed -isnot [System.Collections.IDictionary]) { return $null }
    return Get-Prop $parsed $Name
}

function Get-ErrorCode {
    # The stable code a failure is classified by, in the spec's order: the result message's own
    # error_code (what ToolHandlerResult.FromError(text, code) persists), else a 'code' property
    # when the result itself parses to a JSON object, else $null.
    param($Result, $ErrorCodeField)
    if ($ErrorCodeField -is [string] -and -not [string]::IsNullOrEmpty($ErrorCodeField)) {
        return $ErrorCodeField
    }
    $code = Get-ResultProperty -Result $Result -Name 'code'
    if ($code -is [string] -and -not [string]::IsNullOrEmpty($code)) { return $code }
    return $null
}

function Sort-Ordinal {
    # `Sort-Object -CaseSensitive` is still a CULTURE comparer, and the C# twin sorts every one of
    # these with StringComparer.Ordinal. They agree for today's ASCII tool names, but they are not the
    # same comparer, and the evaluator hash they both feed must not depend on which one ran.
    param([string[]]$Values)
    $sorted = [string[]]@($Values)
    [Array]::Sort($sorted, [System.StringComparer]::Ordinal)
    return $sorted
}

function Get-Sha256Hex {
    param([byte[]]$Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.Convert]::ToHexString($sha.ComputeHash($Bytes)).ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-Sha256OfText {
    param([string]$Text)
    return Get-Sha256Hex ([System.Text.Encoding]::UTF8.GetBytes($Text))
}

function Get-ArgsDigest {
    # The digest OBJECT reported in place of a storm's arguments. Idempotent, so a redacted archive
    # and the raw store it was taken from report the same storm.
    param([string]$CanonicalArgs)
    if ($CanonicalArgs.Contains($RedactedArgsKey, [System.StringComparison]::Ordinal)) {
        return $CanonicalArgs
    }
    return '{"' + $RedactedArgsKey + '":"' + (Get-Sha256OfText $CanonicalArgs) + '"}'
}

function Get-ClaimSignals {
    # The fabricated-compliance heuristic's two signals, from EITHER raw prose or the redacted
    # object a committed archive carries in its place. Returns $null when the node is neither.
    param($Node)
    if ($Node -is [string]) {
        return @{
            verb = [bool]($Node -match '(?i)(claim|complet|marked)')
            noun = [bool]($Node -match '(?i)(task|todo|board)')
        }
    }
    if ($Node -is [System.Collections.IDictionary]) {
        $verb = Get-Prop $Node 'claimVerbMatch'
        $noun = Get-Prop $Node 'claimNounMatch'
        if ($null -ne $verb -or $null -ne $noun) {
            return @{ verb = ($verb -eq $true); noun = ($noun -eq $true) }
        }
    }
    return $null
}

function Add-Count {
    param([hashtable]$Map, [string]$Key)
    if ($Map.ContainsKey($Key)) { $Map[$Key]++ } else { $Map[$Key] = 1 }
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

function ConvertTo-CanonicalTaskId {
    # "01", " 1", "+1.02" -> "1", "1.2". FindTaskByStringId parses every dotted segment as an
    # int, so those spellings all address the same row; a ledger keyed on the raw text would
    # miss a loss purely because of how the model wrote the number. Returns $null when any
    # segment is not an integer, which is an invalid-id error rather than a not-found.
    param([string]$Id)
    if ([string]::IsNullOrWhiteSpace($Id)) { return $null }
    $segments = New-Object System.Collections.Generic.List[string]
    foreach ($part in ($Id -split '\.')) {
        $n = 0
        if (-not [int]::TryParse($part.Trim(), [ref]$n)) { return $null }
        $segments.Add([string]$n)
    }
    return ($segments -join '.')
}

function Add-LedgerId {
    param($Ledger, [string]$Id)
    $canonical = ConvertTo-CanonicalTaskId $Id
    if ($null -ne $canonical) { $Ledger[$canonical] = $true }
}

function Remove-LedgerSubtree {
    # A delete takes the row's whole subtree with it, so the ledger drops every descendant too;
    # otherwise each child would be reported as vanished the first time anyone looked for it.
    param($Ledger, [string]$Id)
    $canonical = ConvertTo-CanonicalTaskId $Id
    if ($null -eq $canonical) { return }
    foreach ($key in @($Ledger.Keys)) {
        if ($key -eq $canonical -or $key.StartsWith("$canonical.", [System.StringComparison]::Ordinal)) {
            $Ledger.Remove($key)
        }
    }
}

function Update-BoardLedger {
    # Mirrors TaskManager's own ledger from the transcript: ids the thread minted, minus ids it
    # asked to have deleted. Only SUCCESSFUL results reach here.
    param($Ledger, [string]$Tool, [string]$Text)
    switch ($Tool) {
        'add-task' {
            if ($Text -match '^Added task ([0-9. ]+):') { Add-LedgerId $Ledger $Matches[1] }
        }
        'bulk-initialize' {
            # clearExisting is a requested reset that also renumbers from 1.
            if ($Text -match '(?m)^Cleared existing tasks\.') { $Ledger.Clear() }
            foreach ($m in [regex]::Matches($Text, '(?m)^\s*-\s*Task ([0-9.]+):')) {
                Add-LedgerId $Ledger $m.Groups[1].Value
            }
        }
        'delete-task' {
            if ($Text -match '^Deleted task (\S+) and all subtasks:') {
                Remove-LedgerSubtree $Ledger $Matches[1]
            }
            elseif ($Text -match '^Deleted subtask (\d+) from task (\S+):') {
                Remove-LedgerSubtree $Ledger ('{0}.{1}' -f $Matches[2], $Matches[1])
            }
        }
    }
}

function Get-NotFoundTaskId {
    # The dotted id a not-found error names, canonicalized, or $null when the error is not a
    # not-found. The quoted forms are tested FIRST: the bare pattern would also match them and
    # capture the quotes, which then fail canonicalization and silently lose the event.
    param([string]$Text)
    if ($Text -match "^Error: Task '([^']+)' has no subtask (\d+)\.") {
        return (ConvertTo-CanonicalTaskId ('{0}.{1}' -f $Matches[1], $Matches[2]))
    }
    foreach ($pattern in @(
            "^Error: Task '([^']+)' not found\.",
            "^Error: Parent task '([^']+)' not found\.",
            "^Error: Blocking task '([^']+)' not found\.",
            '^Error: Task (\S+) not found\.',
            '^Error: Parent task (\S+) not found\.'
        )) {
        if ($Text -match $pattern) { return (ConvertTo-CanonicalTaskId $Matches[1]) }
    }
    return $null
}

$KindNames = @('Primary', 'SubAgent', 'WorkflowController', 'WorkflowTask', 'Continuation')

function Get-Stamp {
    # One host stamp out of metadata.json's properties bag. The host writes these as JSON STRINGS,
    # but a hand-written fixture may inline the object; both are accepted. A malformed stamp costs
    # the run its timings, never its whole score.
    param($Value, [switch]$Single)
    if ($null -eq $Value) { if ($Single) { return $null } else { return @() } }
    $node = $Value
    if ($Value -is [string]) {
        if ([string]::IsNullOrWhiteSpace($Value)) { if ($Single) { return $null } else { return @() } }
        try { $node = ConvertFrom-Json -InputObject $Value -AsHashtable -Depth 64 }
        catch { if ($Single) { return $null } else { return @() } }
    }
    if ($Single) { return $node }
    if ($node -is [System.Collections.IDictionary]) { return @($node) }
    return @($node)
}

function Get-UsageNumber {
    param($Node, [string]$Name)
    $value = Get-Prop $Node $Name
    if ($null -eq $value) { return [long]0 }
    try { return [long]$value } catch { return [long]0 }
}

function Get-ExecutionKindName {
    # The host serializes ExecutionKind with the DEFAULT options, i.e. as the enum's number. A
    # string is accepted too, so a later serializer change does not silently blank the rollup.
    param($Node)
    $value = Get-Prop $Node 'ExecutionKind'
    if ($value -is [string] -and -not [string]::IsNullOrEmpty($value)) { return $value }
    if ($null -eq $value) { return 'Primary' }
    $index = -1
    try { $index = [int]$value } catch { return 'Primary' }
    if ($index -ge 0 -and $index -lt $KindNames.Count) { return $KindNames[$index] }
    return 'Primary'
}

function Get-UsageRows {
    # Parses one thread's usage.records bag into rows. A row without an attempt id is DROPPED: the
    # dedupe key is that id, so admitting it risks double-counting the very tokens this exists to
    # attribute.
    param($Value)
    $records = Get-Stamp $Value
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($record in @($records)) {
        if ($record -isnot [System.Collections.IDictionary]) { continue }
        $attemptId = Get-Prop $record 'ProviderAttemptId'
        if ($null -eq $attemptId) { $attemptId = Get-Prop $record 'LogicalCallId' }
        if ($attemptId -isnot [string] -or [string]::IsNullOrEmpty($attemptId)) { continue }

        $parent = Get-Prop $record 'ParentExecutionId'
        $root = Get-Prop $record 'RootConversationId'
        $agent = if ($parent -is [string] -and -not [string]::IsNullOrEmpty($parent)) { $parent }
                 elseif ($root -is [string] -and -not [string]::IsNullOrEmpty($root)) { $root }
                 else { '(unknown)' }
        $separator = $attemptId.IndexOf(':', [System.StringComparison]::Ordinal)
        $rows.Add(@{
            attemptId       = $attemptId
            attemptKey      = if ($separator -lt 0) { $attemptId } else { $attemptId.Substring($separator + 1) }
            kind            = Get-ExecutionKindName $record
            agentId         = $agent
            inputTokens     = Get-UsageNumber $record 'InputTokens'
            outputTokens    = Get-UsageNumber $record 'OutputTokens'
            cacheReadTokens = Get-UsageNumber $record 'CacheReadTokens'
            cacheWriteTokens = Get-UsageNumber $record 'CacheWriteTokens'
            reasoningTokens = Get-UsageNumber $record 'ReasoningTokens'
            totalTokens     = Get-UsageNumber $record 'TotalTokens'
        })
    }
    return $rows
}

function New-UsageTotals {
    return [ordered]@{
        records = 0; inputTokens = [long]0; outputTokens = [long]0; cacheReadTokens = [long]0
        cacheWriteTokens = [long]0; reasoningTokens = [long]0; totalTokens = [long]0
    }
}

function Add-UsageRow {
    param($Totals, $Row)
    $Totals.records++
    $Totals.inputTokens += $Row.inputTokens
    $Totals.outputTokens += $Row.outputTokens
    $Totals.cacheReadTokens += $Row.cacheReadTokens
    $Totals.cacheWriteTokens += $Row.cacheWriteTokens
    $Totals.reasoningTokens += $Row.reasoningTokens
    $Totals.totalTokens += $Row.totalTokens
}

function Get-CorpusHash {
    # SHA-256 over the eval corpus. Each file contributes <name>\n<byteCount>\n<bytes>\n - the name
    # and the length are what stop two files' contents from sliding into each other. CR bytes are
    # stripped so a CRLF checkout hashes like an LF one; a MISSING file contributes a byte count of
    # -1 and no bytes, which stays distinguishable from a genuinely empty one.
    param([string]$EvalDir)
    $buffer = [System.Collections.Generic.List[byte]]::new()
    foreach ($name in $CorpusFileNames) {
        $path = Join-Path $EvalDir $name
        $bytes = $null
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $bytes = @([System.IO.File]::ReadAllBytes($path) | Where-Object { $_ -ne 13 })
        }
        $count = if ($null -eq $bytes) { -1 } else { $bytes.Count }
        $buffer.AddRange([System.Text.Encoding]::UTF8.GetBytes("$name`n$count`n"))
        if ($null -ne $bytes -and $bytes.Count -gt 0) { $buffer.AddRange([byte[]]$bytes) }
        $buffer.Add([byte]10)
    }
    return Get-Sha256Hex $buffer.ToArray()
}

function Get-SpecHash {
    return Get-Sha256OfText "$SpecVersion`n$Schema"
}

function Get-EvaluatorHash {
    # Every constant that can move a measured NUMBER, and nothing else. Deliberately excludes any
    # build identity: rebuilding a scorer must not invalidate an archived baseline.
    param()
    $tasks = (Sort-Ordinal $TaskTools) -join ','
    $coordination = (Sort-Ordinal $CoordinationTools) -join ','
    return Get-Sha256OfText ((Get-SpecHash) + "`n" + $tasks + "`n" + $coordination + "`n" + $StormThreshold + "`n" + $RedactedArgsKey)
}

function ConvertTo-SortedCountMap {
    # An ordinal-sorted map, so two runs' score JSON stays diffable and a key's position never
    # depends on insertion order.
    param([hashtable]$Map)
    $out = [ordered]@{}
    foreach ($key in (Sort-Ordinal @($Map.Keys))) { $out[$key] = $Map[$key] }
    return $out
}

function ConvertTo-SortedMap {
    param([hashtable]$Map)
    $out = [ordered]@{}
    foreach ($key in (Sort-Ordinal @($Map.Keys))) { $out[$key] = $Map[$key] }
    return $out
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

# Ordinal, like the C# twin's StringComparer.Ordinal, and NOT `Sort-Object Name`, which is a culture
# comparer. This order is load-bearing rather than cosmetic: the richest-stamp selection below keeps
# the FIRST thread at a tied observation count, so the two scorers can only agree on which stamp they
# picked if they agree on the walk order first.
$threadDirsByName = @{}
foreach (
    $d in Get-ChildItem -LiteralPath $ConversationsDir -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'messages.json') -PathType Leaf }
) {
    $threadDirsByName[$d.Name] = $d
}
$threadDirs = @(Sort-Ordinal -Values @($threadDirsByName.Keys) | ForEach-Object { $threadDirsByName[$_] })

# --- walk the conversation store -------------------------------------------------------

$totalToolCalls = 0
$unpairedToolCalls = 0
$perTool = [ordered]@{}
foreach ($t in $RowOrder) {
    $perTool[$t] = @{ calls = 0; errors = 0; errorCodes = @{} }
}
$errorCodeTotals = @{}
$waitOutcomes = @{}
$openObligationsLastObserved = 0
$openObligationResults = 0
$usageRecords = [System.Collections.Generic.List[object]]::new()
$generationIdsByThread = @{}
$spawnTimings = @()
$startupWork = $null
# How much of the run the stamp currently held saw; -2 = none held yet. See the accumulation site
# for why ONE stamp is chosen rather than every thread's being merged.
$stampObservations = -2
$storms = [System.Collections.Generic.List[object]]::new()
$blockRecorded = $false
$blockExplicitlyCleared = $false
$turns = 0
$primaryTurns = 0
$subAgentThreads = 0
$subAgentsWithoutTaskToolCalls = [System.Collections.Generic.List[string]]::new()
$fabricatedComplianceSuspects = [System.Collections.Generic.List[string]]::new()
$vanishEvents = [System.Collections.Generic.List[object]]::new()

foreach ($dir in $threadDirs) {
    $envelopes = ConvertFrom-Json -InputObject (Get-Content -LiteralPath (Join-Path $dir.FullName 'messages.json') -Raw) -AsHashtable -Depth 64
    $threadId = $dir.Name

    # The host's own stamps travel in metadata.json's properties bag, as JSON strings. Everything
    # the evidence layer needs is therefore readable OFFLINE, from the archived store alone.
    $properties = $null
    $metadataPath = Join-Path $dir.FullName 'metadata.json'
    if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
        try {
            $metadata = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $metadataPath -Raw) -AsHashtable -Depth 64
            $properties = Get-Prop $metadata 'properties'
        }
        catch { $properties = $null }
    }
    foreach ($row in @(Get-UsageRows (Get-Prop $properties 'usage.records'))) { $usageRecords.Add($row) }
    # One sink measures the WHOLE run, and every collaborating thread stamps that same running
    # snapshot onto its OWN metadata, so these stamps are repeats of ONE series rather than
    # per-thread parts of it. Appending them multiplied the run's spawn cost by the thread count,
    # and first-wins took a sub-agent's mid-run roll-up because `subagent-` sorts before `thread-`.
    # Keep the single richest stamp, and keep BOTH artifacts from that same thread so the timings
    # and the roll-up can never describe different moments.
    $threadTimings = @(Get-Stamp (Get-Prop $properties 'subagents.spawnTimings'))
    $threadWork = Get-Stamp (Get-Prop $properties 'subagents.startupWork') -Single
    if ($null -ne $threadWork -or $threadTimings.Count -gt 0) {
        $observations = if ($null -eq $threadWork) { -1 } else {
            [long]$threadWork.Spawns + [long]$threadWork.TemplateCatalogBuilds + [long]$threadWork.DirectoryListings
        }
        if ($observations -gt $stampObservations) {
            $stampObservations = $observations
            $spawnTimings = $threadTimings
            $startupWork = $threadWork
        }
    }

    # Pass 1: pair results by tool_call_id (within this thread only).
    $resultsById = @{}
    foreach ($env in @($envelopes)) {
        if ((Get-Prop $env 'messageType') -ne 'ToolCallResultMessage') { continue }
        $inner = ConvertFrom-Json -InputObject ([string](Get-Prop $env 'messageJson')) -AsHashtable -Depth 64
        $callId = [string](Get-Prop $inner 'tool_call_id')
        if (-not [string]::IsNullOrEmpty($callId) -and -not $resultsById.ContainsKey($callId)) {
            $resultsById[$callId] = @{
                Result    = Get-Prop $inner 'result'
                IsError   = Get-Prop $inner 'is_error'
                ErrorCode = Get-Prop $inner 'error_code'
            }
        }
    }

    # Pass 2: calls in message order + turns.
    $generationIds = [System.Collections.Generic.HashSet[string]]::new()
    $streaks = @{}   # identity -> current consecutive-failure run length
    $ledger = @{}    # canonical dotted id -> minted here and not deleted (#621 Part B)
    $threadTaskToolCalls = 0
    $assistantClaims = [System.Collections.Generic.List[object]]::new()
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
            # Raw prose in a live store, the {length, claimVerbMatch, claimNounMatch} object in a
            # redacted archive: the heuristic's VERDICT is what both forms carry, so the archive
            # scores the same as the store it replaces.
            $signals = Get-ClaimSignals (Get-Prop $inner 'text')
            if ($null -ne $signals) { $assistantClaims.Add($signals) }
        }

        if ($messageType -ne 'ToolCallMessage' -or $null -eq $inner) { continue }

        $tool = [string](Get-Prop $inner 'function_name')
        $callId = [string](Get-Prop $inner 'tool_call_id')
        $totalToolCalls++

        $hasResult = -not [string]::IsNullOrEmpty($callId) -and $resultsById.ContainsKey($callId)
        if (-not $hasResult) { $unpairedToolCalls++ }
        $family = Get-ToolFamily $tool
        $resultRecord = if ($hasResult) { $resultsById[$callId] } else { $null }
        $errorCode = if ($hasResult) { Get-ErrorCode -Result $resultRecord.Result -ErrorCodeField $resultRecord.ErrorCode } else { $null }

        # Defensive union per metrics-spec.md: is_error == true OR the 'Error:' text prefix. A
        # coordination refusal carries NEITHER an 'Error:' prefix nor, in every store seen so far,
        # is_error alone - it is is_error plus an error_code - so the code's presence is a third
        # disjunct THERE ONLY, leaving the 15 task tools' historical counts untouched.
        $isError = $hasResult -and (
            (Test-ErrorResult -Result $resultRecord.Result -IsErrorFlag $resultRecord.IsError) -or
            ($family -eq 'coordination' -and $null -ne $errorCode)
        )

        if ($family -eq 'other') { continue }

        if ($family -eq 'task') {
            $threadTaskToolCalls++
        }
        else {
            # openObligations may ride on ANY coordination result; the wait outcome only on a wait.
            $open = Get-ResultProperty -Result $resultRecord.Result -Name 'openObligations'
            if ($open -is [int] -or $open -is [long] -or $open -is [double]) {
                $openObligationsLastObserved = [int]$open
                $openObligationResults++
            }
            if ($WaitTools -ccontains $tool) {
                $outcome = if ($isError) {
                    if ($null -ne $errorCode) { $errorCode } else { $UnclassifiedErrorCode }
                }
                else {
                    $status = Get-ResultProperty -Result $resultRecord.Result -Name 'status'
                    if ($status -is [string] -and -not [string]::IsNullOrEmpty($status)) { $status } else { 'ok' }
                }
                Add-Count $waitOutcomes $outcome
            }
        }

        $perTool[$tool].calls++
        if ($isError) {
            $perTool[$tool].errors++

            # A coordination refusal ALWAYS lands in the tally, falling back to 'unclassified',
            # so a reader can never confuse 'no refusals' with 'refusals we failed to classify'.
            # A task error is tallied only when it really carries a code: the board tools report
            # errors as 'Error:' text, and blanketing them would bury the coordination taxonomy.
            $code = if ($null -ne $errorCode) { $errorCode } elseif ($family -eq 'coordination') { $UnclassifiedErrorCode } else { $null }
            if ($null -ne $code) {
                Add-Count $perTool[$tool].errorCodes $code
                Add-Count $errorCodeTotals $code
            }
        }

        $canonical = Get-CanonicalArgs ([string](Get-Prop $inner 'function_args'))
        $identity = $tool + "`n" + $canonical

        # Coordination calls join the storm walk on the same terms as task calls. The walk's own
        # error guard IS the polling exemption: a repeated call whose result is a non-error
        # (status running/timeout/question_received) is an observation and can never extend a run.
        if ($isError) {
            if (-not $streaks.ContainsKey($identity)) { $streaks[$identity] = 0 }
            $streaks[$identity]++
        }
        elseif ($hasResult -and $streaks.ContainsKey($identity)) {
            # A successful occurrence of the SAME identity ends its run.
            if ($streaks[$identity] -ge $StormThreshold) {
                $storms.Add([ordered]@{ threadId = $threadId; tool = $tool; count = $streaks[$identity]; args = (Get-ArgsDigest $canonical) })
            }
            $streaks[$identity] = 0
        }

        # Everything below is board bookkeeping and applies to the task family alone.
        if ($family -ne 'task') { continue }

        # Board-loss watch (#621 Part B), transcript side. Same discrimination line as the
        # server detector: a not-found naming an id THIS thread minted and never deleted is a
        # lost row; anything else is a model typo and stays unreported.
        if ($hasResult) {
            $resultRaw = $resultsById[$callId].Result
            $resultText = if ($resultRaw -is [string]) { $resultRaw } elseif ($null -eq $resultRaw) { '' } else { ConvertTo-CanonicalJson $resultRaw }
            if ($isError) {
                $vanishedId = Get-NotFoundTaskId $resultText
                if ($null -ne $vanishedId -and $ledger.ContainsKey($vanishedId)) {
                    $vanishEvents.Add([ordered]@{ threadId = $threadId; taskId = $vanishedId; tool = $tool })
                }
            }
            else {
                Update-BoardLedger -Ledger $ledger -Tool $tool -Text $resultText
            }
        }

        if (-not $isError -and $hasResult) {
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
        if ($streaks[$identity] -ge $StormThreshold) {
            $tool, $canonical = $identity -split "`n", 2
            $storms.Add([ordered]@{ threadId = $threadId; tool = $tool; count = $streaks[$identity]; args = (Get-ArgsDigest $canonical) })
        }
    }

    $generationIdsByThread[$threadId] = $generationIds
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
            $claims = $assistantClaims | Where-Object { $_.verb -and $_.noun }
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
$coordinationToolCalls = 0
$coordinationToolErrors = 0
foreach ($t in $RowOrder) {
    $calls = $perTool[$t].calls
    $errors = $perTool[$t].errors
    $family = Get-ToolFamily $t
    if ($family -eq 'task') { $taskToolCalls += $calls; $taskToolErrors += $errors }
    else { $coordinationToolCalls += $calls; $coordinationToolErrors += $errors }
    $rate = if ($calls -gt 0) { [math]::Round($errors / $calls, 4) } else { 0 }
    $perToolOut[$t] = [ordered]@{
        calls      = $calls
        errors     = $errors
        errorRate  = $rate
        family     = $family
        errorCodes = (ConvertTo-SortedCountMap $perTool[$t].errorCodes)
    }
}

# --- usage rollup -------------------------------------------------------------------------
$usageSeen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
$usageDuplicates = 0
$usageTotals = New-UsageTotals
$usageByKind = @{}
$usageByAgent = @{}
$attributedTurnTokens = [long]0
$unattributedTurnTokens = [long]0
foreach ($row in $usageRecords) {
    # The same attempt is relayed into more than one bag by design; counting it twice would
    # double the run's tokens.
    if (-not $usageSeen.Add($row.attemptId)) { $usageDuplicates++; continue }
    Add-UsageRow $usageTotals $row
    if (-not $usageByKind.ContainsKey($row.kind)) { $usageByKind[$row.kind] = New-UsageTotals }
    Add-UsageRow $usageByKind[$row.kind] $row
    if (-not $usageByAgent.ContainsKey($row.agentId)) { $usageByAgent[$row.agentId] = New-UsageTotals }
    Add-UsageRow $usageByAgent[$row.agentId] $row

    $joined = $generationIdsByThread.ContainsKey($row.agentId) -and $generationIdsByThread[$row.agentId].Contains($row.attemptKey)
    if ($joined) { $attributedTurnTokens += $row.totalTokens } else { $unattributedTurnTokens += $row.totalTokens }
}

$score = [ordered]@{
    schema             = $Schema
    conversationsDir   = (Resolve-Path -LiteralPath $ConversationsDir).Path
    threads            = $threadDirs.Count
    subAgentCount      = $subAgentThreads
    totalToolCalls     = $totalToolCalls
    taskToolCalls      = $taskToolCalls
    taskToolErrors     = $taskToolErrors
    coordinationToolCalls   = $coordinationToolCalls
    coordinationToolErrors  = $coordinationToolErrors
    unpairedToolCalls  = $unpairedToolCalls
    perTool            = $perToolOut
    errorCodes         = (ConvertTo-SortedCountMap $errorCodeTotals)
    waitOutcomes       = (ConvertTo-SortedCountMap $waitOutcomes)
    openObligations    = [ordered]@{
        lastObserved         = $openObligationsLastObserved
        resultsCarryingField = $openObligationResults
        note                 = if ($openObligationResults -eq 0) { $ObligationsNotEmittedNote } else { $null }
    }
    usage              = [ordered]@{
        totals                 = $usageTotals
        duplicateAttemptIds    = $usageDuplicates
        byExecutionKind        = (ConvertTo-SortedMap $usageByKind)
        byAgent                = (ConvertTo-SortedMap $usageByAgent)
        kindsNotEmitted        = $KindsNotEmitted
        attributedTurnTokens   = $attributedTurnTokens
        unattributedTurnTokens = $unattributedTurnTokens
        notes                  = @($TurnJoinNote, $ToolFamilyNote)
    }
    spawnTimings       = @($spawnTimings)
    startupWork        = $startupWork
    fingerprints       = [ordered]@{
        taskCorpusHash = (Get-CorpusHash $PSScriptRoot)
        specHash       = (Get-SpecHash)
        evaluatorHash  = (Get-EvaluatorHash)
        specVersion    = $SpecVersion
    }
    retryStormCount    = $storms.Count
    retryStorms        = @($storms)
    boardIdVanished    = [ordered]@{
        count  = $vanishEvents.Count
        events = @($vanishEvents)
        note   = 'Transcript-derived LOWER BOUND (#621 Part B). The authoritative signal is the server-side Warning whose event name is TodoBoardIdVanished; also grep the host structured logs for that name, because losses the transcript cannot see (ids minted in a process whose transcript is not in this directory, or bulk-initialize subtask ids its result text never named, which is every one before #634 R1 and any past its row cap since) reach the log and not this count.'
    }
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

$json = ConvertTo-Json -InputObject $score -Depth 12
if ($OutFile) {
    Set-Content -LiteralPath $OutFile -Value $json -Encoding utf8NoBOM
}
$json
