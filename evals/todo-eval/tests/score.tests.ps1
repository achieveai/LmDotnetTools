<#
Fixture-pinned checks for score.ps1 (#618). Self-contained — plain assertions, no Pester.
Run: pwsh ./tests/score.tests.ps1   Exit code 0 = all green, 1 = failures (listed).

Coverage:
- complete-run fixture scores complete, storm-free, block recorded+cleared, valid.
- retry-storm fixture counts exactly one storm of length 4 (across two arg key orders —
  pins canonicalization; raw-string identity would see 2+2 and count none), completion
  false with every fixture check failing, storm-free second identity at 2 stays silent.
- no-tools-subagent fixture: completion true but validity false + fabricated-compliance
  suspect (the production failure shape).
- Mutation checks: break one field -> the verdict flips. Proves each assertion is wired
  to the input it claims to test, not vacuously green.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$score = Join-Path $root 'score.ps1'
$fixtures = Join-Path $root 'fixtures'
$failures = [System.Collections.Generic.List[string]]::new()
$passCount = 0

function Invoke-Score {
    param([string]$ConversationsDir, [string]$BoardSnapshot, [string]$Fixture)
    $scoreArgs = @{ ConversationsDir = $ConversationsDir; BoardSnapshot = $BoardSnapshot }
    if ($Fixture) { $scoreArgs.Fixture = $Fixture }
    $json = & $score @scoreArgs | Out-String
    return $json | ConvertFrom-Json
}

function Assert {
    param([bool]$Condition, [string]$Name)
    if ($Condition) {
        $script:passCount++
    }
    else {
        $script:failures.Add($Name)
        Write-Host "FAIL: $Name" -ForegroundColor Red
    }
}

# ---- complete-run ----------------------------------------------------------------------

$c = Invoke-Score -ConversationsDir (Join-Path $fixtures 'complete-run\conversations') `
                  -BoardSnapshot (Join-Path $fixtures 'complete-run\board.json')

Assert ($c.completion -eq $true) 'complete-run: completion is true'
Assert ($c.completionFailures.Count -eq 0) 'complete-run: no completion failures'
Assert ($c.retryStormCount -eq 0) 'complete-run: no retry storms'
Assert ($c.blockRecorded -eq $true) 'complete-run: block recorded'
Assert ($c.blockExplicitlyCleared -eq $true) 'complete-run: block explicitly cleared'
Assert ($c.blockCleared -eq $true) 'complete-run: block cleared'
Assert ($c.threads -eq 2) 'complete-run: 2 threads scored'
Assert ($c.totalToolCalls -eq 13) 'complete-run: 13 total tool calls'
Assert ($c.taskToolCalls -eq 13) 'complete-run: 13 task tool calls'
Assert ($c.taskToolErrors -eq 1) 'complete-run: exactly 1 task tool error'
Assert ($c.unpairedToolCalls -eq 0) 'complete-run: no unpaired calls'
Assert ($c.perTool.'add-note'.calls -eq 3) 'complete-run: add-note calls = 3'
Assert ($c.perTool.'add-note'.errors -eq 1) 'complete-run: add-note errors = 1'
Assert ($c.perTool.'add-note'.errorRate -eq 0.3333) 'complete-run: add-note errorRate = 0.3333'
Assert ($c.perTool.'search-tasks'.calls -eq 0) 'complete-run: zero-call tool still reported'
Assert ($c.turns -eq 8) 'complete-run: 8 turns (5 primary + 3 subagent generations)'
Assert ($c.primaryTurns -eq 5) 'complete-run: 5 primary turns'
Assert ($c.validity.valid -eq $true) 'complete-run: run is valid'
Assert ($c.validity.subAgentThreads -eq 1) 'complete-run: 1 subagent thread'

# ---- retry-storm -----------------------------------------------------------------------

$s = Invoke-Score -ConversationsDir (Join-Path $fixtures 'retry-storm\conversations') `
                  -BoardSnapshot (Join-Path $fixtures 'retry-storm\board.json')

Assert ($s.retryStormCount -eq 1) 'retry-storm: exactly one storm'
Assert ($s.retryStorms[0].count -eq 4) 'retry-storm: storm length 4 (canonicalized args unify both key orders)'
Assert ($s.retryStorms[0].tool -eq 'add-note') 'retry-storm: storm tool is add-note'
Assert ($s.retryStorms[0].threadId -eq 'thread-fixture-storm') 'retry-storm: storm thread id'
Assert ($s.completion -eq $false) 'retry-storm: completion is false'
Assert ($s.completionFailures.Count -ge 4) 'retry-storm: multiple fixture checks fail'
Assert ($s.blockRecorded -eq $false) 'retry-storm: no block recorded'
Assert ($s.blockCleared -eq $false) 'retry-storm: block not cleared'
Assert ($s.perTool.'add-note'.errorRate -eq 1) 'retry-storm: add-note error rate 100%'

# ---- no-tools-subagent (validity precondition + fabricated compliance) ------------------

$n = Invoke-Score -ConversationsDir (Join-Path $fixtures 'no-tools-subagent\conversations') `
                  -BoardSnapshot (Join-Path $fixtures 'no-tools-subagent\board.json')

Assert ($n.completion -eq $true) 'no-tools: board still completes'
Assert ($n.validity.valid -eq $false) 'no-tools: run is INVALID'
Assert ($n.validity.subAgentsWithoutTaskToolCalls -contains 'subagent-fixture-notools-0002') 'no-tools: tool-less subagent listed'
Assert ($n.validity.fabricatedComplianceSuspects -contains 'subagent-fixture-notools-0002') 'no-tools: fabricated compliance flagged'
Assert ($n.validity.subAgentsWithoutTaskToolCalls -notcontains 'subagent-fixture-ws2-0001') 'no-tools: working subagent not listed'

# ---- mutation checks (each breaks ONE thing; the verdict must flip) ----------------------

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("todo-eval-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
try {
    # M1: one subtask status flipped Completed -> InProgress in the board => completion false.
    $board = Get-Content (Join-Path $fixtures 'complete-run\board.json') -Raw | ConvertFrom-Json -AsHashtable -Depth 64
    $board['Tasks'][2]['subTasks'][1]['status'] = 'InProgress'
    $m1Board = Join-Path $tmp 'm1-board.json'
    $board | ConvertTo-Json -Depth 20 | Set-Content $m1Board
    $m1 = Invoke-Score -ConversationsDir (Join-Path $fixtures 'complete-run\conversations') -BoardSnapshot $m1Board
    Assert ($m1.completion -eq $false) 'M1: one non-Completed subtask flips completion'

    # M2: fixture expects 4 top-level tasks => completion false on the same good board.
    $fx = Get-Content (Join-Path $root 'expected-board.json') -Raw | ConvertFrom-Json -AsHashtable -Depth 32
    $fx['board']['topLevelTaskCount'] = 4
    $m2Fixture = Join-Path $tmp 'm2-fixture.json'
    $fx | ConvertTo-Json -Depth 20 | Set-Content $m2Fixture
    $m2 = Invoke-Score -ConversationsDir (Join-Path $fixtures 'complete-run\conversations') `
                       -BoardSnapshot (Join-Path $fixtures 'complete-run\board.json') -Fixture $m2Fixture
    Assert ($m2.completion -eq $false) 'M2: fixture topLevelTaskCount=4 flips completion'

    # M3: strip the notes from one subtask => completion false (minNotesPerSubtask wired).
    $board = Get-Content (Join-Path $fixtures 'complete-run\board.json') -Raw | ConvertFrom-Json -AsHashtable -Depth 64
    $board['Tasks'][0]['subTasks'][3]['subTasks'][0]['notes'] = @()
    $m3Board = Join-Path $tmp 'm3-board.json'
    $board | ConvertTo-Json -Depth 20 | Set-Content $m3Board
    $m3 = Invoke-Score -ConversationsDir (Join-Path $fixtures 'complete-run\conversations') -BoardSnapshot $m3Board
    Assert ($m3.completion -eq $false) 'M3: a note-less depth-3 task flips completion'

    # M4: turn the THIRD identical failing storm call into a success => streak resets at 2,
    # tail run of 1, no storm. Pins that a same-identity success breaks the run.
    $stormDir = Join-Path $tmp 'm4-conversations\thread-fixture-storm'
    New-Item -ItemType Directory -Path $stormDir -Force | Out-Null
    $messages = Get-Content (Join-Path $fixtures 'retry-storm\conversations\thread-fixture-storm\messages.json') -Raw |
        ConvertFrom-Json -AsHashtable -Depth 64
    foreach ($m in $messages) {
        if ($m['messageType'] -eq 'ToolCallResultMessage') {
            $inner = ConvertFrom-Json -InputObject $m['messageJson'] -AsHashtable -Depth 64
            if ($inner['tool_call_id'] -eq 'call_st_0005') {
                $inner['result'] = 'Added note to task 2.1.'
                $m['messageJson'] = ($inner | ConvertTo-Json -Depth 20 -Compress)
            }
        }
    }
    ConvertTo-Json -InputObject @($messages) -Depth 30 | Set-Content (Join-Path $stormDir 'messages.json')
    $m4 = Invoke-Score -ConversationsDir (Join-Path $tmp 'm4-conversations') `
                       -BoardSnapshot (Join-Path $fixtures 'retry-storm\board.json')
    Assert ($m4.retryStormCount -eq 0) 'M4: a same-identity success mid-run breaks the storm'
    Assert ($m4.perTool.'add-note'.errors -eq 5) 'M4: mutated call now counts as success'

    # M5: block the final board (one Blocked task) => blockCleared false + completion false.
    $board = Get-Content (Join-Path $fixtures 'complete-run\board.json') -Raw | ConvertFrom-Json -AsHashtable -Depth 64
    $board['Tasks'][1]['subTasks'][2]['status'] = 'Blocked'
    $m5Board = Join-Path $tmp 'm5-board.json'
    $board | ConvertTo-Json -Depth 20 | Set-Content $m5Board
    $m5 = Invoke-Score -ConversationsDir (Join-Path $fixtures 'complete-run\conversations') -BoardSnapshot $m5Board
    Assert ($m5.blockCleared -eq $false) 'M5: a Blocked task on the final board flips blockCleared'
    Assert ($m5.completion -eq $false) 'M5: a Blocked task flips completion'
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

# ---- summary ----------------------------------------------------------------------------

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host "All $passCount checks passed." -ForegroundColor Green
    exit 0
}
Write-Host "$($failures.Count) of $($passCount + $failures.Count) checks FAILED:" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
