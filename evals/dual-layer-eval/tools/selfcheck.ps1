#!/usr/bin/env pwsh
# Self-check for one dual-layer-eval task (or all of them): proves the checker can tell a right answer
# from a wrong one before any model is run. For each task:
#   fixtures  every meta.requiredFixtures glob matches exactly its declared count (runner semantics:
#             case-insensitive, '*' does not cross '/'), and the key's questions number q1..qN
#   gold      a workspace (fixtures copy) holding the GOLD answers scores 1.0 / pass
#   variants  the gold answers in other accepted spellings ("The X.", "(B) ...", "1,234") still score 1.0
#   empty     no answers.json scores 0 / fail, exit 0
#   wrong     wrong answers score 0 / fail
#   garbage   an unparseable answers.json scores 0 / fail, exit 0
#   nokey     a missing answer key exits 2 (could not judge)
#   isolation fixtures/ holds no key.json / build files, and no fixture file names a source id
#   memory    (non-retired tasks) no gold answer appears in the task message or in a fixture path;
#             a task whose meta.json sets memoryExposed has its leaks listed but not failed
#   trap      (synthetic tasks) answers computed under each trap fail exactly the questions that trap flips
# Usage: pwsh evals/dual-layer-eval/tools/selfcheck.ps1 [-Task mq1] [-Scratch <dir>]
param(
    [string[]] $Task,
    [string] $Scratch
)
$ErrorActionPreference = 'Stop'
$evalDir = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent (Split-Path -Parent $evalDir)
if (-not $Scratch) { $Scratch = Join-Path $repo '.logs/dual-layer-eval-build/selfcheck' }
$Task = @($Task | ForEach-Object { $_ -split ',' } | Where-Object { $_ })  # 'pwsh -File' passes "a,b" as one string
if (-not $Task) { $Task = @(Get-ChildItem (Join-Path $evalDir 'tasks') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'check.ps1') } | ForEach-Object Name) }

$failures = 0
function Assert([string] $taskId, [string] $name, [bool] $ok, [string] $detail) {
    $mark = if ($ok) { 'ok  ' } else { 'FAIL' }
    Write-Host ("  [{0}] {1,-10} {2}" -f $mark, $name, $detail)
    if (-not $ok) { $script:failures++ }
}

function Invoke-Check([string] $taskDir, [string] $ws, [string] $label) {
    $out = Join-Path $Scratch "$label.score.json"
    Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue
    $null = & pwsh -NoProfile -NonInteractive -File (Join-Path $taskDir 'check.ps1') -Workspace $ws -Out $out 2>&1
    $code = $LASTEXITCODE
    $score = if (Test-Path $out) { Get-Content $out -Raw | ConvertFrom-Json } else { $null }
    return [pscustomobject]@{ Exit = $code; Score = $score }
}

function Write-Answers([string] $ws, $obj) {
    New-Item -ItemType Directory -Force -Path $ws | Out-Null
    $obj | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ws 'answers.json') -Encoding utf8
}

New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
foreach ($id in $Task) {
    $taskDir = Join-Path $evalDir "tasks/$id"
    $fixtures = Join-Path $taskDir 'fixtures'
    Write-Host "$id"
    $meta = Get-Content (Join-Path $taskDir 'meta.json') -Raw | ConvertFrom-Json
    $key = Get-Content (Join-Path $taskDir 'hidden/key.json') -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
    $qs = @($key['questions'])

    # fixtures -------------------------------------------------------------------------------------
    $rel = @(Get-ChildItem -LiteralPath $fixtures -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($fixtures, $_.FullName).Replace('\', '/') })
    foreach ($rf in @($meta.requiredFixtures)) {
        $g = $rf.glob.Split('/')
        $n = @($rel | Where-Object {
                $p = $_.Split('/')
                if ($p.Count -ne $g.Count) { return $false }
                for ($i = 0; $i -lt $p.Count; $i++) { if (-not ($p[$i] -like $g[$i])) { return $false } }
                return $true
            }).Count
        Assert $id 'fixtures' ($n -eq $rf.count -and $rf.count -gt 0) "'$($rf.glob)' matched $n, declared $($rf.count)"
    }
    Assert $id 'fixtures' (@($meta.requiredFixtures).Count -gt 0) "$(@($meta.requiredFixtures).Count) requiredFixtures entr(y/ies); $($rel.Count) files in fixtures/"
    $ids = ($qs | ForEach-Object { $_['id'] }) -join ','
    $want = (1..$qs.Count | ForEach-Object { "q$_" }) -join ','
    Assert $id 'key' ($ids -eq $want) "$($qs.Count) questions ($ids)"
    $bytes = (Get-ChildItem -LiteralPath $fixtures -Recurse -File | Measure-Object Length -Sum).Sum
    # A git-ignored task may declare a larger budget (meta.maxFixtureMB); committed tasks stay under 8 MB.
    $capMB = if ($meta.PSObject.Properties['maxFixtureMB']) { [double] $meta.maxFixtureMB } else { 8 }
    Assert $id 'size' ($bytes -lt $capMB * 1MB) ("fixtures {0:N2} MB (< {1} MB)" -f ($bytes / 1MB), $capMB)

    # isolation ------------------------------------------------------------------------------------
    $bad = @($rel | Where-Object { $_ -match '(^|/)(key|build|build-report)\.json$|answers\.json$' })
    Assert $id 'isolation' ($bad.Count -eq 0) "no key/build/answers files under fixtures/ ($($bad.Count) found)"
    $srcIds = @($qs | Where-Object { $_.ContainsKey('source_id') } | ForEach-Object { [string] $_['source_id'] })
    if ($srcIds.Count -gt 0) {
        $hits = @(Get-ChildItem -LiteralPath $fixtures -Recurse -File | Select-String -SimpleMatch -Pattern $srcIds -List)
        Assert $id 'isolation' ($hits.Count -eq 0) "no fixture file carries an upstream question id ($($hits.Count) found)"
    }

    # memory probe (mechanical part) --------------------------------------------------------------
    # The design argument that no question is answerable from task.md alone is per task in
    # tasks/README.md. This checks the part a script can: no gold answer (4+ characters, or 3+ digits)
    # appears in the message the model receives or in any fixture path. Retired tasks are exempt:
    # they were retired precisely because the model knew their answers.
    if ($meta.split -ne 'retired') {
        $norm = { param($s) ' ' + ((([string] $s).ToLowerInvariant() -replace '[^\p{L}\p{Nd}]+', ' ').Trim()) + ' ' }
        $msg = ((Get-Content (Join-Path $taskDir 'task.md') -Raw -Encoding utf8) -split '(?m)^---\s*$', 2)[-1]
        $normMsg = & $norm $msg
        $normPaths = & $norm ($rel -join ' ')
        $checked = 0
        $leaks = @(foreach ($q in $qs) {
                foreach ($a in @($q['answers'])) {
                    $na = (& $norm $a).Trim()
                    $digits = ($na -replace '\D', '').Length
                    if (($q['kind'] -eq 'int' -and $digits -lt 3) -or ($q['kind'] -ne 'int' -and $na.Length -lt 4)) { continue }
                    $checked++
                    if ($normMsg.Contains(" $na ")) { "$($q['id']) '$a' in task message" }
                    if ($normPaths.Contains(" $na ")) { "$($q['id']) '$a' in a fixture path" }
                }
            })
        # A task restored despite known exposure says so in meta.memoryExposed; its leaks are listed, not failed.
        $ack = $meta.PSObject.Properties['memoryExposed']
        $detail = "$checked checked$(if ($leaks) { '; ' + ($leaks -join '; ') })$(if ($ack -and $leaks) { '; acknowledged in meta.memoryExposed' })"
        Assert $id 'memory' ($leaks.Count -eq 0 -or $ack) "no gold answer in the task message or a fixture path ($detail)"
    }

    # gold -----------------------------------------------------------------------------------------
    $ws = Join-Path $Scratch "$id-gold"
    Remove-Item -LiteralPath $ws -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath $fixtures -Destination $ws -Recurse
    $gold = [ordered]@{}
    foreach ($q in $qs) {
        $a = $q['answers'][0]
        $gold[$q['id']] = if ($q['kind'] -in 'text', 'norm') { [ordered]@{ answer = $a; evidence = @($q['evidence']) } } else { $a }
    }
    Write-Answers $ws $gold
    $r = Invoke-Check $taskDir $ws "$id-gold"
    Assert $id 'gold' ($r.Exit -eq 0 -and $r.Score.score -eq 1 -and $r.Score.outcome -eq 'pass') "exit $($r.Exit), score $($r.Score.score), $($r.Score.outcome)"

    # variants -------------------------------------------------------------------------------------
    $var = [ordered]@{}
    foreach ($q in $qs) {
        $a = $q['answers'][0]
        $var[$q['id']] = switch ($q['kind']) {
            'text' { "The $(([string] $a).ToUpperInvariant())." }
            'norm' { " $(([string] $a).ToUpperInvariant()). " }
            'choice' { "($a) because the pages say so" }
            'int' { ([int64] $a).ToString('N0', [Globalization.CultureInfo]::InvariantCulture) }
            'exact' { " $(([string] $a).ToLowerInvariant()) " }
            'idset' { ((([string] $a) -split ',\s*') | Sort-Object -Descending | ForEach-Object { $_.ToLowerInvariant() }) -join '; ' }
            'wordset' { @((([string] $a) -split ',\s*') | Sort-Object -Descending | ForEach-Object { $_.ToLowerInvariant() }) }
            'loc' { $f, $fn = ([string] $a) -split '::'; @('./repo/' + ($f -replace '/', '\') + '::' + ($fn -split '\.')[-1] + '()', 'src/zz.py::zz') }
        }
    }
    $ws = Join-Path $Scratch "$id-variants"; Remove-Item $ws -Recurse -Force -ErrorAction SilentlyContinue
    Write-Answers $ws $var
    $r = Invoke-Check $taskDir $ws "$id-variants"
    Assert $id 'variants' ($r.Exit -eq 0 -and $r.Score.score -eq 1) "exit $($r.Exit), score $($r.Score.score) (e.g. q1 = '$($var['q1'])')"

    # empty ----------------------------------------------------------------------------------------
    $ws = Join-Path $Scratch "$id-empty"; Remove-Item $ws -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $ws | Out-Null
    $r = Invoke-Check $taskDir $ws "$id-empty"
    Assert $id 'empty' ($r.Exit -eq 0 -and $r.Score.score -eq 0 -and $r.Score.outcome -eq 'fail') "exit $($r.Exit), score $($r.Score.score), $($r.Score.outcome)"

    # wrong ----------------------------------------------------------------------------------------
    $wrong = [ordered]@{}
    foreach ($q in $qs) {
        $a = $q['answers'][0]
        $wrong[$q['id']] = switch ($q['kind']) {
            'text' { 'zzqx unknown' }
            'norm' { 'zzqx unknown' }
            'choice' { @('A', 'B', 'C', 'D') | Where-Object { $_ -ne $a } | Select-Object -First 1 }
            'int' { [int64] $a + 1 }
            'exact' { 'ZZZ' }
            'idset' { ((([string] $a) -split ',\s*') | Select-Object -Skip 1) -join ', ' }
            'wordset' { ((([string] $a) -split ',\s*') | Select-Object -Skip 1) -join ', ' }
            'loc' { @((([string] $a) -split '::')[0] + '::zzqx_not_a_function') }
        }
    }
    $ws = Join-Path $Scratch "$id-wrong"; Remove-Item $ws -Recurse -Force -ErrorAction SilentlyContinue
    Write-Answers $ws $wrong
    $r = Invoke-Check $taskDir $ws "$id-wrong"
    Assert $id 'wrong' ($r.Exit -eq 0 -and $r.Score.score -eq 0 -and $r.Score.outcome -eq 'fail') "exit $($r.Exit), score $($r.Score.score), $($r.Score.outcome)"

    # garbage --------------------------------------------------------------------------------------
    $ws = Join-Path $Scratch "$id-garbage"; Remove-Item $ws -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $ws | Out-Null
    Set-Content -LiteralPath (Join-Path $ws 'answers.json') -Value '{ "q1": "unterminated' -Encoding utf8
    $r = Invoke-Check $taskDir $ws "$id-garbage"
    Assert $id 'garbage' ($r.Exit -eq 0 -and $r.Score.score -eq 0) "exit $($r.Exit), score $($r.Score.score)"

    # traps (synthetic tasks) ----------------------------------------------------------------------
    $reportPath = Join-Path $taskDir 'hidden/build-report.json'
    $report = if (Test-Path $reportPath) { Get-Content $reportPath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
    if ($report.ContainsKey('trap_answers')) {
        foreach ($trap in $report['trap_answers'].Keys) {
            $ta = [ordered]@{}
            foreach ($q in $qs) { $ta[$q['id']] = $report['trap_answers'][$trap][$q['id']] }
            $expectFail = @($qs | Where-Object { @($_['trap_flips']) -contains $trap }).Count
            $ws = Join-Path $Scratch "$id-trap-$trap"; Remove-Item $ws -Recurse -Force -ErrorAction SilentlyContinue
            Write-Answers $ws $ta
            $r = Invoke-Check $taskDir $ws "$id-trap-$trap"
            $failed = @($r.Score.checks | Where-Object { -not $_.pass }).Count
            Assert $id 'trap' ($r.Exit -eq 0 -and $failed -eq $expectFail -and $failed -gt 0) "answers computed under '$trap' fail $failed/$($qs.Count) (expected $expectFail), score $($r.Score.score)"
        }
    }

    # nokey ----------------------------------------------------------------------------------------
    $out = Join-Path $Scratch "$id-nokey.score.json"
    $null = & pwsh -NoProfile -NonInteractive -File (Join-Path $evalDir 'tools/Score-Answers.ps1') -Workspace $ws -Out $out -KeyPath (Join-Path $Scratch 'no-such-key.json') 2>&1
    Assert $id 'nokey' ($LASTEXITCODE -eq 2) "exit $LASTEXITCODE (expected 2)"
}
Write-Host ''
if ($failures -gt 0) { Write-Host "SELFCHECK FAILED: $failures assertion(s)"; exit 1 }
Write-Host "SELFCHECK PASSED: $($Task -join ', ')"
exit 0
