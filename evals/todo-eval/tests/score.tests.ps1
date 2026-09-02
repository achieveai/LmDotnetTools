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
- M1-M12 (#670): the coordination family — its own counts, refusals that carry no "Error:"
  prefix, the error-code fallback chain, the polling exemption, wait outcomes, usage
  dedupe and turn join, the host stamps, the redacted input forms, and the fingerprints.
  Each mutation breaks ONE of those and the verdict flips.
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

    # ---- M: the coordination family (#670) ------------------------------------------------
    # Read from the committed fixtures/coordination-run store, which the C# twin scores too:
    # one shared fixture is what makes a scorer disagreement visible instead of plausible.

    $coordFixture = Join-Path $fixtures 'coordination-run'
    $coordBoard = Join-Path $coordFixture 'board.json'

    function Copy-CoordStore {
        # A private copy of the fixture store that a mutation may edit. The fixture itself is
        # never written to: two mutations would otherwise interfere through it.
        param([string]$Name)
        $dest = Join-Path $tmp $Name
        Copy-Item -Recurse -Force (Join-Path $coordFixture 'conversations') $dest
        return $dest
    }

    function Edit-CoordMessages {
        # Applies $Edit to each inner message hashtable of one thread's messages.json.
        param([string]$StoreDir, [string]$Thread, [scriptblock]$Edit)
        $path = Join-Path $StoreDir "$Thread\messages.json"
        $messages = ConvertFrom-Json -InputObject (Get-Content -LiteralPath $path -Raw) -AsHashtable -Depth 64
        foreach ($m in @($messages)) {
            $inner = ConvertFrom-Json -InputObject $m['messageJson'] -AsHashtable -Depth 64
            & $Edit $m $inner
            $m['messageJson'] = ($inner | ConvertTo-Json -Depth 20 -Compress)
        }
        ConvertTo-Json -InputObject @($messages) -Depth 30 | Set-Content -LiteralPath $path
    }

    function Get-Count {
        # A count-map lookup that tolerates the key being absent (StrictMode would throw).
        param($Map, [string]$Key)
        if ($null -eq $Map) { return 0 }
        if (@($Map.PSObject.Properties.Name) -notcontains $Key) { return 0 }
        return $Map.$Key
    }

    $m = Invoke-Score -ConversationsDir (Join-Path $coordFixture 'conversations') -BoardSnapshot $coordBoard

    # M1: the two families are counted apart, and every coordination tool gets a row.
    Assert ($m.schema -eq 'todo-eval/score@2') 'M1: the score object announces schema @2'
    Assert ($m.totalToolCalls -eq 16) 'M1: 16 tool calls in total'
    Assert ($m.coordinationToolCalls -eq 15) 'M1: 15 coordination calls'
    Assert ($m.coordinationToolErrors -eq 5) 'M1: 5 coordination errors'
    Assert ($m.taskToolCalls -eq 1) 'M1: the one board call is still counted as task work'
    Assert (@($m.perTool.PSObject.Properties.Name).Count -eq 22) 'M1: 22 per-tool rows (15 task + 7 coordination)'
    Assert ($m.perTool.'WaitForAgents'.family -eq 'coordination') 'M1: WaitForAgents is in the coordination family'
    Assert ($m.perTool.'add-task'.family -eq 'task') 'M1: add-task is still in the task family'

    # M2: the refusal shape. Its text is a plain sentence, so the task family's "Error:" prefix
    # rule alone would have scored all three as SUCCESSES.
    Assert ($m.perTool.'WaitForAgents'.errors -eq 3) 'M2: a refusal with no "Error:" prefix is still an error'
    Assert ((Get-Count $m.perTool.'WaitForAgents'.errorCodes 'unknown_agent') -eq 3) 'M2: and it is classified by its error_code'

    # M3: the error-code fallback chain, both remaining rungs.
    Assert ((Get-Count $m.perTool.'SendMessage'.errorCodes 'depth_limit') -eq 1) 'M3: a code carried inside the result object is used'
    Assert ((Get-Count $m.perTool.'CheckAgent'.errorCodes 'unclassified') -eq 1) 'M3: a coordination failure with no code anywhere is reported as unclassified'
    Assert ((Get-Count $m.errorCodes 'unknown_agent') -eq 3) 'M3: codes roll up run-wide'

    # M4: exactly one storm, and the polling exemption asserted on the SAME run, so the zero
    # cannot be an unreached code path.
    Assert ($m.retryStormCount -eq 1) 'M4: the three identical refusals are one storm'
    Assert ($m.retryStorms[0].tool -eq 'WaitForAgents') 'M4: and the storm names WaitForAgents'
    Assert ($m.perTool.'CheckAgents'.calls -eq 5) 'M4: the five identical polls really happened'
    Assert (@($m.retryStorms | Where-Object { $_.tool -eq 'CheckAgents' }).Count -eq 0) 'M4: five identical SUCCESSFUL polls are never a storm'
    Assert ($m.retryStorms[0].args -match '__argsSha256') 'M4: the storm reports an argument digest, not the arguments'
    Assert ($m.retryStorms[0].args -notmatch 'agent-9') 'M4: and the model text really is gone from it'

    # M5: wait outcomes separate "the wait timed out" from "the agent does not exist".
    Assert ((Get-Count $m.waitOutcomes 'unknown_agent') -eq 3) 'M5: a refused wait is tallied by its code'
    Assert ((Get-Count $m.waitOutcomes 'timeout') -eq 1) 'M5: a non-error wait is tallied by its status'

    # M6 MUTATION: flip three of the successful CheckAgents polls to errors. If the exemption
    # were keyed on the tool NAME rather than on the outcome, this would stay storm-free.
    $m6Store = Copy-CoordStore 'm6-conversations'
    $m6Flipped = 0
    Edit-CoordMessages -StoreDir $m6Store -Thread 'thread-fixture-coord' -Edit {
        param($envelope, $inner)
        if ($envelope['messageType'] -eq 'ToolCallResultMessage' -and
            $inner['tool_call_id'] -match '^call_co_(0006|0007|0008)$' -and $script:m6Flipped -lt 3) {
            $inner['is_error'] = $true
            $inner['error_code'] = 'transport_lost'
            $script:m6Flipped++
        }
    }
    $m6 = Invoke-Score -ConversationsDir $m6Store -BoardSnapshot $coordBoard
    Assert ($m6Flipped -eq 3) 'M6: the mutation really flipped three polls'
    Assert (@($m6.retryStorms | Where-Object { $_.tool -eq 'CheckAgents' }).Count -eq 1) 'M6: three consecutive FAILING polls of one identity ARE a storm'

    # M7 MUTATION: the vocabulary drift alarm. Rename one coordination tool in the transcript
    # and its calls must fall out of the coordination totals entirely - which is exactly what a
    # host-side rename would do to a silently un-updated scorer.
    $m7Store = Copy-CoordStore 'm7-conversations'
    Edit-CoordMessages -StoreDir $m7Store -Thread 'thread-fixture-coord' -Edit {
        param($envelope, $inner)
        if ($envelope['messageType'] -eq 'ToolCallMessage' -and $inner['function_name'] -eq 'WaitForAgents') {
            $inner['function_name'] = 'waitforagents'
        }
    }
    $m7 = Invoke-Score -ConversationsDir $m7Store -BoardSnapshot $coordBoard
    Assert ($m7.coordinationToolCalls -eq 12) 'M7: a renamed tool stops being coordination work'
    Assert ($m7.perTool.'WaitForAgents'.calls -eq 0) 'M7: its row goes to zero rather than disappearing'
    Assert ($m7.totalToolCalls -eq 16) 'M7: and the total still sees the calls, so the loss is visible'

    # M8 MUTATION: strip the error_code. The refusals stay errors (is_error still says so) but
    # lose their classification, and the score must SAY unclassified rather than omit them.
    $m8Store = Copy-CoordStore 'm8-conversations'
    Edit-CoordMessages -StoreDir $m8Store -Thread 'thread-fixture-coord' -Edit {
        param($envelope, $inner)
        if ($inner.Contains('error_code')) { $inner.Remove('error_code') }
    }
    $m8 = Invoke-Score -ConversationsDir $m8Store -BoardSnapshot $coordBoard
    Assert ($m8.coordinationToolErrors -eq 5) 'M8: without codes the refusals are still errors'
    Assert ((Get-Count $m8.errorCodes 'unclassified') -eq 4) 'M8: and every uncoded coordination failure is reported as unclassified'
    Assert ((Get-Count $m8.waitOutcomes 'unclassified') -eq 3) 'M8: an uncoded refused wait is an unclassified outcome, not a missing one'

    # M9 MUTATION: drop is_error and keep only error_code. This is the coordination-only third
    # disjunct on its own - the fixture normally carries both, so without this mutation the
    # disjunct could be deleted and every other check would stay green.
    $m9Store = Copy-CoordStore 'm9-conversations'
    Edit-CoordMessages -StoreDir $m9Store -Thread 'thread-fixture-coord' -Edit {
        param($envelope, $inner)
        if ($inner.Contains('is_error')) { $inner['is_error'] = $false }
    }
    $m9 = Invoke-Score -ConversationsDir $m9Store -BoardSnapshot $coordBoard
    Assert ($m9.perTool.'WaitForAgents'.errors -eq 3) 'M9: an error_code alone marks a coordination result as an error'
    Assert ($m9.perTool.'CheckAgent'.errors -eq 0) 'M9: and a coordination failure with neither flag nor code is NOT invented as one'

    # M10: the redacted input forms a committed archive carries.
    $m10Store = Copy-CoordStore 'm10-conversations'
    Edit-CoordMessages -StoreDir $m10Store -Thread 'thread-fixture-coord' -Edit {
        param($envelope, $inner)
        if ($envelope['messageType'] -eq 'ToolCallMessage' -and $inner['function_name'] -eq 'WaitForAgents') {
            $inner['function_args'] = '{"__argsSha256":"f37ffa650c05594f4d9ba546d9d23586552fb5f88e5ca246a9b59d87ca4b673d"}'
        }
    }
    $m10 = Invoke-Score -ConversationsDir $m10Store -BoardSnapshot $coordBoard
    Assert ($m10.retryStormCount -eq 1) 'M10: digest arguments still form the same single storm'
    Assert ($m10.retryStorms[0].args -eq $m.retryStorms[0].args) 'M10: and the archive reports the same storm identity as the raw store'

    # M11: usage. Both bags carry the primary attempt; counting it twice would double the run.
    Assert ($m.usage.duplicateAttemptIds -eq 1) 'M11: the relayed attempt is deduped'
    Assert ($m.usage.totals.records -eq 3) 'M11: three distinct usage records'
    Assert ($m.usage.totals.totalTokens -eq 2070) 'M11: totals sum every token class'
    Assert ($m.usage.byExecutionKind.SubAgent.totalTokens -eq 500) 'M11: the numeric ExecutionKind resolves to SubAgent'
    Assert ($m.usage.attributedTurnTokens -eq 2000) 'M11: records whose attempt key is a real generation id join a turn'
    Assert ($m.usage.unattributedTurnTokens -eq 70) 'M11: a synthetic derived: key can never join and is reported apart'
    Assert (@($m.usage.kindsNotEmitted) -contains 'Continuation') 'M11: the kinds this build cannot emit are named, so their zeros are not read as measurements'

    # M12: the host stamps and the obligations sentinel.
    Assert ($m.spawnTimings[0].ToolRegistryMs -eq 37) 'M12: a spawn timing stamp is read back from metadata'
    Assert ($m.spawnTimings[0].Reconstructed -eq $false) 'M12: and a fresh spawn is told apart from a rebuilt one'
    Assert ($m.startupWork.TemplateCatalogBuilds -eq 11) 'M12: the startup-work roll-up is read back too'
    Assert ($m.startupWork.Reconstructions -eq 1) 'M12: including the share of construction that was re-construction'
    Assert ($m.openObligations.resultsCarryingField -eq 0) 'M12: no result carries openObligations yet'
    Assert ($m.openObligations.note -match 'NOT REPORTED') 'M12: so the zero is labelled NOT REPORTED rather than left to be read as none-open'

    # M13 MUTATION: the corpus fingerprint moves when the corpus moves, and the two contract
    # hashes do not - which is what lets #677 tell "different task" from "different scorer".
    # Run against a COPY of the scorer and its corpus: mutating the repo's own task.md would
    # leave the working tree dirty if this run were interrupted.
    $m13Dir = Join-Path $tmp 'm13-corpus'
    New-Item -ItemType Directory -Path $m13Dir -Force | Out-Null
    Copy-Item -Force $score $m13Dir
    foreach ($corpusFile in @('task.md', 'mode.json', 'expected-board.json')) {
        Copy-Item -Force (Join-Path $root $corpusFile) $m13Dir
    }
    $m13Score = Join-Path $m13Dir 'score.ps1'
    $m13Before = & $m13Score -ConversationsDir (Join-Path $coordFixture 'conversations') -BoardSnapshot $coordBoard | Out-String | ConvertFrom-Json
    Assert ($m13Before.fingerprints.taskCorpusHash -eq $m.fingerprints.taskCorpusHash) 'M13: the copied corpus fingerprints identically to the original'

    Add-Content -LiteralPath (Join-Path $m13Dir 'task.md') -Value 'mutation'
    $m13 = & $m13Score -ConversationsDir (Join-Path $coordFixture 'conversations') -BoardSnapshot $coordBoard | Out-String | ConvertFrom-Json
    Assert ($m13.fingerprints.taskCorpusHash -ne $m.fingerprints.taskCorpusHash) 'M13: perturbing task.md changes the corpus fingerprint'
    Assert ($m13.fingerprints.specHash -eq $m.fingerprints.specHash) 'M13: but not the spec fingerprint'
    Assert ($m13.fingerprints.evaluatorHash -eq $m.fingerprints.evaluatorHash) 'M13: nor the evaluator fingerprint'
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
