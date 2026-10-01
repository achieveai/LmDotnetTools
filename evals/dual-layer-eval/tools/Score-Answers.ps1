#!/usr/bin/env pwsh
# Shared J1 checker for every dual-layer-eval task: scores <Workspace>/answers.json against a task's
# hidden/key.json and writes score.json (compaction-eval/score@1 shape: task, outcome, score, checks).
# No LLM. Exit 0 when it judged (any score), 2 when it could not judge (key missing or unreadable).
#
# key.json: { "task": "mq1", "questions": [ { "id": "q1", "kind": "text|choice|int|exact",
#             "answers": [...], "evidence": ["file.md", ...] } ] }
#   text    SQuAD-style normalisation (lowercase, strip accents, dashes to spaces, other punctuation
#           and symbols removed, articles a/an/the dropped, whitespace collapsed); correct when the
#           normalised answer equals ANY gold/alias, or its token F1 against one is >= 0.8
#   norm    the same normalisation, but ONLY an exact normalised match with the answer or an alias
#           counts (no F1): 'Mara Okafar' must not pass for 'Mara Okafor', nor '2500ms' for '2500'
#   choice  one letter A-D; accepts "B", "(B)", "B.", "B) text", "Option B"
#   int     an exact whole number; accepts 1234, "1234", "1,234", 1234.0
#   exact   case-insensitive, trimmed string equality
#   idset   a set of ids such as ticket numbers: the answer may be a JSON array or one string; every token
#           shaped like LETTERS-DIGITS is taken, case-folded, and the answer passes only when its set equals
#           the gold set (answers[0], comma-separated). Order and duplicates do not matter; no partial credit
#   wordset like idset for bare words such as stock tickers: every run of letters/digits is a token, case-folded;
#           passes only on set equality with the gold (answers[0], comma-separated). No partial credit
#   loc     code locations 'path/to/file.py::Class.method' (a JSON array, or one string split on commas and
#           whitespace). Every gold entry is an acceptable location. Passes when at most three are given and
#           one has a gold entry's path (case-folded, '\' as '/', a leading './' or 'repo/' dropped) and
#           its function: the same qualname, or the same last segment ('method' for 'Class.method')
# answers.json values may be the answer itself or { "answer": ..., "evidence": [...] }. Evidence is
# never scored; its recall against the key's evidence is reported per check as a diagnostic.
param(
    [Parameter(Mandatory = $true)] [string] $Workspace,
    [Parameter(Mandatory = $true)] [string] $Out,
    [Parameter(Mandatory = $true)] [string] $KeyPath,
    [double] $F1Threshold = 0.8
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3

if (-not (Test-Path -LiteralPath $KeyPath)) { [Console]::Error.WriteLine("answer key missing: $KeyPath"); exit 2 }
try { $key = Get-Content -LiteralPath $KeyPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable }
catch { [Console]::Error.WriteLine("answer key unreadable: $($_.Exception.Message)"); exit 2 }
if (-not $key.ContainsKey('questions') -or $key['questions'].Count -eq 0) { [Console]::Error.WriteLine('answer key has no questions'); exit 2 }

function Get-NormalizedText([string] $s) {
    if ($null -eq $s) { return '' }
    $d = $s.ToLowerInvariant().Normalize([Text.NormalizationForm]::FormD)
    $sb = [Text.StringBuilder]::new()
    foreach ($ch in $d.ToCharArray()) {
        $cat = [Globalization.CharUnicodeInfo]::GetUnicodeCategory($ch)
        if ($cat -eq 'NonSpacingMark') { continue }
        if ($cat -eq 'DashPunctuation') { [void] $sb.Append(' '); continue }  # 'four-year' ~ 'four year'
        if ([char]::IsPunctuation($ch) -or [char]::IsSymbol($ch)) { continue }
        [void] $sb.Append($ch)
    }
    $t = [regex]::Replace($sb.ToString(), '\b(a|an|the)\b', ' ')
    return ([regex]::Replace($t, '\s+', ' ')).Trim()
}

function Get-TokenF1([string] $pred, [string] $gold) {
    $p = @((Get-NormalizedText $pred) -split ' ' | Where-Object { $_ })
    $g = @((Get-NormalizedText $gold) -split ' ' | Where-Object { $_ })
    if ($p.Count -eq 0 -or $g.Count -eq 0) { return [double]($p.Count -eq $g.Count) }
    $counts = @{}
    foreach ($t in $g) { $counts[$t] = 1 + ($counts[$t] ?? 0) }
    $common = 0
    foreach ($t in $p) { if (($counts[$t] ?? 0) -gt 0) { $common++; $counts[$t]-- } }
    if ($common -eq 0) { return 0.0 }
    $precision = $common / $p.Count; $recall = $common / $g.Count
    return 2 * $precision * $recall / ($precision + $recall)
}

function Get-ChoiceLetter([string] $s) {
    if ($null -eq $s) { return '' }
    $m = [regex]::Match($s.Trim(), '^(?:option\s+|answer\s*[:=]?\s*)?\(?([A-Da-d])\)?(?:$|[\s\.\):,])', 'IgnoreCase')
    if ($m.Success) { return $m.Groups[1].Value.ToUpperInvariant() }
    return ''
}

function Get-IdSet([string[]] $items) {
    @($items | ForEach-Object { [regex]::Matches($_, '[A-Za-z]+-\d+') | ForEach-Object { $_.Value.ToUpperInvariant() } } | Sort-Object -Unique)
}

function ConvertTo-Location([string] $s) {
    $m = [regex]::Match($s.Trim(), '^(?<p>[^\s:]+\.py)::?(?<q>[A-Za-z_][\w.]*)')
    if (-not $m.Success) { return $null }
    $path = ($m.Groups['p'].Value -replace '\\', '/').ToLowerInvariant() -replace '^(\./)?(repo/)?', ''
    [pscustomobject]@{ Path = $path; Qual = $m.Groups['q'].Value.TrimEnd('.'); Last = ($m.Groups['q'].Value.TrimEnd('.') -split '\.')[-1] }
}

function Get-WordSet([string[]] $items) {
    @($items | ForEach-Object { [regex]::Matches($_, '[A-Za-z0-9]+') | ForEach-Object { $_.Value.ToUpperInvariant() } } | Sort-Object -Unique)
}

function Get-WholeNumber($v) {
    if ($null -eq $v) { return $null }
    $s = ([string] $v).Trim() -replace '[,_\s]', ''
    $d = 0.0
    if ([double]::TryParse($s, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref] $d) -and $d -eq [math]::Floor($d)) {
        return [int64] $d
    }
    return $null
}

$checks = New-Object System.Collections.Generic.List[object]
$answers = $null
$parseNote = ''
$ansPath = Join-Path $Workspace 'answers.json'
if (-not (Test-Path -LiteralPath $ansPath)) { $parseNote = 'answers.json missing' }
else {
    try {
        $answers = Get-Content -LiteralPath $ansPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
        if ($answers -isnot [System.Collections.IDictionary]) { $answers = $null; $parseNote = 'answers.json is not an object' }
    } catch { $parseNote = "answers.json does not parse: $($_.Exception.Message)" }
}

foreach ($q in $key['questions']) {
    $id = [string] $q['id']
    $raw = $null; $evidence = @()
    if ($null -ne $answers) {
        # accept "q1" and "Q1"
        $k = @($answers.Keys | Where-Object { ([string] $_).Trim().ToLowerInvariant() -eq $id }) | Select-Object -First 1
        if ($null -ne $k) {
            $raw = $answers[$k]
            if ($raw -is [System.Collections.IDictionary]) {
                $evidence = @($raw['evidence'] ?? @())
                $raw = $raw['answer']
            }
        }
    }
    $got = if ($null -eq $raw) { '' } else { [string] $raw }
    $gold = @($q['answers'])
    $pass = $false; $how = ''
    if ($null -eq $raw) { $how = if ($parseNote) { $parseNote } else { 'no answer' } }
    else {
        switch ([string] $q['kind']) {
            'text' {
                $ng = Get-NormalizedText $got
                $best = 0.0
                foreach ($g in $gold) {
                    if ($ng -ne '' -and $ng -eq (Get-NormalizedText ([string] $g))) { $pass = $true; $how = 'exact'; break }
                    $best = [math]::Max($best, (Get-TokenF1 $got ([string] $g)))
                }
                if (-not $pass) { $pass = $best -ge ($F1Threshold - 1e-9); $how = ('f1={0:0.00}' -f $best) }
            }
            'norm' {
                $ng = Get-NormalizedText $got
                $pass = $ng -ne '' -and @($gold | Where-Object { (Get-NormalizedText ([string] $_)) -eq $ng }).Count -gt 0
                $how = "normalised '$ng'"
            }
            'choice' {
                $letter = Get-ChoiceLetter $got
                $pass = $letter -ne '' -and $letter -eq ([string] $gold[0]).ToUpperInvariant(); $how = "letter '$letter'"
            }
            'int' {
                $n = Get-WholeNumber $raw
                $pass = $null -ne $n -and $n -eq [int64] $gold[0]; $how = "number '$n'"
            }
            'exact' {
                $pass = $got.Trim().ToUpperInvariant() -eq ([string] $gold[0]).Trim().ToUpperInvariant(); $how = 'exact'
            }
            'idset' {
                $items = if ($raw -is [System.Collections.IEnumerable] -and $raw -isnot [string]) { @($raw | ForEach-Object { [string] $_ }) } else { @([string] $raw) }
                $haveIds = @(Get-IdSet $items)
                $wantIds = @(Get-IdSet @([string] $gold[0]))
                $pass = $wantIds.Count -gt 0 -and (($haveIds -join ',') -eq ($wantIds -join ','))
                $how = "ids [$($haveIds -join ', ')]"
            }
            'wordset' {
                $items = if ($raw -is [System.Collections.IEnumerable] -and $raw -isnot [string]) { @($raw | ForEach-Object { [string] $_ }) } else { @([string] $raw) }
                $haveIds = @(Get-WordSet $items)
                $wantIds = @(Get-WordSet @([string] $gold[0]))
                $pass = $wantIds.Count -gt 0 -and (($haveIds -join ',') -eq ($wantIds -join ','))
                $how = "words [$($haveIds -join ', ')]"
            }
            'loc' {
                $items = @(if ($raw -is [System.Collections.IEnumerable] -and $raw -isnot [string]) { $raw | ForEach-Object { [string] $_ } } else { ([string] $raw) -split '[,\s]+' | Where-Object { $_ } })
                $have = @($items | ForEach-Object { ConvertTo-Location $_ } | Where-Object { $_ })
                $want = @($gold | ForEach-Object { ConvertTo-Location ([string] $_) } | Where-Object { $_ })
                $hit = @($have | Where-Object { $h = $_; @($want | Where-Object { $_.Path -eq $h.Path -and ($_.Qual -eq $h.Qual -or $_.Last -eq $h.Last) }).Count -gt 0 })
                $pass = $items.Count -le 3 -and $hit.Count -gt 0
                $how = if ($items.Count -gt 3) { "$($items.Count) locations (more than 3)" } else { "$($hit.Count) of $($items.Count) locations match" }
            }
            default { [Console]::Error.WriteLine("unknown kind '$($q['kind'])' for $id"); exit 2 }
        }
    }
    $detail = "got '$got' ($how); expected '$($gold[0])'"
    if ($q.ContainsKey('evidence') -and @($q['evidence']).Count -gt 0) {
        $want = @($q['evidence'] | ForEach-Object { ([string] $_).ToLowerInvariant() })
        $have = @($evidence | ForEach-Object { (Split-Path -Leaf ([string] $_)).ToLowerInvariant() })
        $hit = @($want | Where-Object { $have -contains $_ }).Count
        $detail += "; evidence recall $hit/$($want.Count)"
    }
    $checks.Add([ordered]@{ name = $id; pass = [bool] $pass; detail = $detail })
}

$passed = @($checks | Where-Object { $_.pass }).Count
$total = $checks.Count
$outcome = if ($passed -eq $total) { 'pass' } elseif ($passed -eq 0) { 'fail' } else { 'partial' }
$task = if ($key.ContainsKey('task')) { [string] $key['task'] } else { Split-Path -Leaf (Split-Path -Parent (Split-Path -Parent $KeyPath)) }
[ordered]@{ task = $task; outcome = $outcome; score = [math]::Round($passed / $total, 4); checks = $checks } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Out -Encoding utf8
Write-Host "${task}: $outcome ($passed/$total)"
exit 0
