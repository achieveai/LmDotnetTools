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
- B1: a block-task call that OMITS blockedBy (the documented clear form) scores as a
  clear, never as a recorded block; the []-form clear stays covered by complete-run.
- B2: an empty conversations dir is invalid with a distinct misconfiguration reason.
- F4: is_error:true alone marks a result as an error (union with the "Error:" prefix).
- V7/V8: the board ledger accepts both indents bulk-initialize's echo emits (2-space main
  rows, 4-space subtask rows), and still invents nothing the echo did not name.
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
Assert ($c.validity.reasons.Count -eq 0) 'complete-run: no validity reasons'
Assert ($c.validity.subAgentThreads -eq 1) 'complete-run: 1 subagent thread'
Assert ($c.subAgentCount -eq 1) 'complete-run: subAgentCount surfaced in summary'

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

    # B1: omitted blockedBy is the documented CLEAR form. Rewrite BOTH block-task calls in
    # the complete-run primary thread to {"taskId":"2.3"} (no blockedBy key at all): the
    # run's only block-task calls are now clears, so blockRecorded must be false and
    # blockExplicitlyCleared true. Pins the null-check before @() wrapping — @($null).Count
    # is 1, which scored an omitted-blockedBy clear as a recorded block.
    $b1Dir = Join-Path $tmp 'b1-conversations\thread-fixture-complete'
    New-Item -ItemType Directory -Path $b1Dir -Force | Out-Null
    $messages = Get-Content (Join-Path $fixtures 'complete-run\conversations\thread-fixture-complete\messages.json') -Raw |
        ConvertFrom-Json -AsHashtable -Depth 64
    foreach ($m in $messages) {
        if ($m['messageType'] -eq 'ToolCallMessage') {
            $inner = ConvertFrom-Json -InputObject $m['messageJson'] -AsHashtable -Depth 64
            if ($inner['function_name'] -eq 'block-task') {
                $inner['function_args'] = '{"taskId":"2.3"}'
                $m['messageJson'] = ($inner | ConvertTo-Json -Depth 20 -Compress)
            }
        }
    }
    ConvertTo-Json -InputObject @($messages) -Depth 30 | Set-Content (Join-Path $b1Dir 'messages.json')
    $b1 = Invoke-Score -ConversationsDir (Join-Path $tmp 'b1-conversations') `
                       -BoardSnapshot (Join-Path $fixtures 'complete-run\board.json')
    Assert ($b1.blockRecorded -eq $false) 'B1: omitted blockedBy does NOT count as a recorded block'
    Assert ($b1.blockExplicitlyCleared -eq $true) 'B1: omitted blockedBy counts as an explicit clear'
    Assert ($b1.completionFailures -contains 'requireBlockRecorded: no successful block-task call with a non-empty blockedBy was found') `
        'B1: requireBlockRecorded gate is not satisfied by a clear call'
    Assert ($b1.subAgentCount -eq 0) 'B1: zero-subagent run reports subAgentCount 0'
    Assert ($b1.validity.valid -eq $true) 'B1: zero-subagent run stays VALID'

    # B2: an empty conversations dir is a harness misconfiguration, never a scored run.
    $emptyDir = Join-Path $tmp 'b2-empty-conversations'
    New-Item -ItemType Directory -Path $emptyDir -Force | Out-Null
    $b2 = Invoke-Score -ConversationsDir $emptyDir `
                       -BoardSnapshot (Join-Path $fixtures 'complete-run\board.json')
    Assert ($b2.threads -eq 0) 'B2: empty dir scores zero threads'
    Assert ($b2.validity.valid -eq $false) 'B2: zero threads is INVALID'
    Assert (@($b2.validity.reasons) -contains 'no conversation threads found - harness misconfiguration') `
        'B2: zero-thread invalidity carries the distinct misconfiguration reason'

    # F4: is_error:true on a result whose text is NOT an "Error:" string still counts as an
    # error (defensive union with the text prefix).
    $f4Dir = Join-Path $tmp 'f4-conversations\thread-fixture-complete'
    New-Item -ItemType Directory -Path $f4Dir -Force | Out-Null
    $messages = Get-Content (Join-Path $fixtures 'complete-run\conversations\thread-fixture-complete\messages.json') -Raw |
        ConvertFrom-Json -AsHashtable -Depth 64
    foreach ($m in $messages) {
        if ($m['messageType'] -eq 'ToolCallResultMessage') {
            $inner = ConvertFrom-Json -InputObject $m['messageJson'] -AsHashtable -Depth 64
            if ($inner['tool_call_id'] -eq 'call_fx_0006') {
                $inner['is_error'] = $true   # text stays a success string
                $m['messageJson'] = ($inner | ConvertTo-Json -Depth 20 -Compress)
            }
        }
    }
    ConvertTo-Json -InputObject @($messages) -Depth 30 | Set-Content (Join-Path $f4Dir 'messages.json')
    $f4 = Invoke-Score -ConversationsDir (Join-Path $tmp 'f4-conversations') `
                       -BoardSnapshot (Join-Path $fixtures 'complete-run\board.json')
    Assert ($f4.perTool.'block-task'.errors -eq 1) 'F4: is_error:true alone marks the call as an error'

    # ---- V: board-id-vanished (#621 Part B) ---------------------------------------------
    # Built here rather than as a committed fixture, the way B1/B2/F4 are: the metric is a
    # sequence property of one thread, and the sequence IS the test.

    $vCallSeq = 0
    function New-VCall {
        param([string]$Tool, [string]$Args, [string]$Result)
        $script:vCallSeq++
        $id = "call_v_{0:d4}" -f $script:vCallSeq
        @(
            [ordered]@{
                messageType = 'ToolCallMessage'
                generationId = 'gen-v'
                role = 'Assistant'
                messageJson = (@{ function_name = $Tool; function_args = $Args; tool_call_id = $id } | ConvertTo-Json -Compress)
            },
            [ordered]@{
                messageType = 'ToolCallResultMessage'
                generationId = 'gen-v'
                role = 'Tool'
                messageJson = (@{ tool_call_id = $id; result = $Result; is_error = $false } | ConvertTo-Json -Compress)
            }
        )
    }

    function Invoke-VScore {
        param([string]$Name, $Messages)
        $dir = Join-Path $tmp "$Name\thread-vanish"
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        ConvertTo-Json -InputObject @($Messages) -Depth 30 | Set-Content (Join-Path $dir 'messages.json')
        return Invoke-Score -ConversationsDir (Join-Path $tmp $Name) `
                            -BoardSnapshot (Join-Path $fixtures 'complete-run\board.json')
    }

    $mint = @()
    $mint += New-VCall 'add-task' '{"title":"Alpha"}' 'Added task 1: Alpha'
    $mint += New-VCall 'add-task' '{"title":"Gamma","parentId":"1"}' 'Added task 1.3: Gamma'
    $mint += New-VCall 'add-task' '{"title":"Beta"}' 'Added task 2: Beta'
    $deleteCall = New-VCall 'delete-task' '{"taskId":"2"}' 'Deleted task 2 and all subtasks: Beta'
    $probes = @()
    $probes += New-VCall 'get-task' '{"taskId":"9"}' "Error: Task '9' not found."
    $probes += New-VCall 'get-task' '{"taskId":"2"}' "Error: Task '2' not found."
    $probes += New-VCall 'get-task' '{"taskId":" 01"}' "Error: Task ' 01' not found."
    $probes += New-VCall 'add-note' '{"taskId":"1","subtaskId":3}' "Error: Task '1' has no subtask 3."

    $v = Invoke-VScore 'v-conversations' ($mint + $deleteCall + $probes)
    $vIds = @($v.boardIdVanished.events | ForEach-Object { $_.taskId })

    # The two positives. They are also this section's non-vacuity proof: the negatives below
    # are asserted on the SAME run, so "no event" cannot be an unreached code path.
    Assert ($v.boardIdVanished.count -eq 2) 'V1: exactly the two ids this thread minted and never deleted are reported'
    Assert ($vIds -contains '1') 'V1: a not-found for a minted id is a vanish (canonicalized from " 01")'
    Assert ($vIds -contains '1.3') 'V2: the has-no-subtask wording resolves to the dotted id 1.3'

    # The negatives.
    Assert (-not ($vIds -contains '9')) 'V3: an id this thread never minted is NOT a vanish'
    Assert (-not ($vIds -contains '2')) 'V4: an id the thread deliberately deleted is NOT a vanish'
    Assert ($v.boardIdVanished.note -match 'TodoBoardIdVanished') 'V5: the emitted note points the reader at the server log event'

    # Mutation: drop the successful delete-task from the transcript and id 2 is no longer a
    # deliberate removal, so the very same probe becomes a reported vanish. Pins V4 to the
    # delete-ledger logic rather than to anything incidental about id 2.
    $vm = Invoke-VScore 'vm-conversations' ($mint + $probes)
    Assert ($vm.boardIdVanished.count -eq 3) 'V6: without the delete, the deleted-id probe DOES count as a vanish'
    Assert (@($vm.boardIdVanished.events | ForEach-Object { $_.taskId }) -contains '2') 'V6: and the id it names is 2'

    # V7/V8: the indents bulk-initialize's echo actually uses (#634 R1). Its main-task rows sit at
    # two spaces and its subtask rows at four, and this ledger's regex has to accept BOTH. Pinned
    # through a real scoring run rather than by reading the pattern: the C# BoardIdLedger has a
    # mutation-proved twin of this check, and without one here the PowerShell regex could be
    # narrowed to either indent alone and every existing check would stay green.
    $bulkEcho = @(
        'Added 2 task(s) and 3 subtask(s):'
        '  - Task 1: Alpha'
        '    - Task 1.1: Alpha one'
        '    - Task 1.2: Alpha two'
        '  - Task 2: Beta'
        '    - Task 2.1: Beta one'
    ) -join "`n"

    $bulkMint = New-VCall 'bulk-initialize' '{"tasks":[]}' $bulkEcho
    $bulkProbes = @()
    $bulkProbes += New-VCall 'get-task' '{"taskId":"1.1"}' "Error: Task '1.1' not found."
    $bulkProbes += New-VCall 'get-task' '{"taskId":"2"}' "Error: Task '2' not found."
    $bulkProbes += New-VCall 'get-task' '{"taskId":"2.2"}' "Error: Task '2.2' not found."

    $vb = Invoke-VScore 'vb-conversations' ($bulkMint + $bulkProbes)
    $vbIds = @($vb.boardIdVanished.events | ForEach-Object { $_.taskId })

    Assert ($vbIds -contains '1.1') 'V7: a subtask row the echo names at a 4-space indent enters the ledger'
    Assert ($vbIds -contains '2') 'V7: and a main-task row at a 2-space indent still does'
    Assert ($vb.boardIdVanished.count -eq 2) 'V7: exactly those two probes are reported'

    # The paired negative, on the SAME run, so the positives above cannot be an unreached path.
    Assert (-not ($vbIds -contains '2.2')) 'V8: a row the echo never named is NOT in the ledger, so its not-found stays a typo'
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
