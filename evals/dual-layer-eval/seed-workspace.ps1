#!/usr/bin/env pwsh
# Seeds a hand-testing workspace (e.g. the UI's default "demo" workspace) with dual-layer-eval tasks
# (default: every task whose meta.json split is not "retired"), so a task can be tried in the chat
# without the runner.
#
#   pwsh evals/dual-layer-eval/seed-workspace.ps1 -Workspace <dir> [-Task mq1,ag1]
#
# For each task it writes <dir>/<task-id>/ = a copy of that task's fixtures/ plus TASK.md (the task.md
# message with {SEED} set to the task's first seed and a note that paths are relative to the folder),
# and <dir>/README.md with one line per task folder present in <dir> (seeded now or earlier; retired
# ones marked) and a prompt to paste into the chat. hidden/ (answer keys) is never copied. Idempotent: it
# replaces only the <task-id>/ folders it was asked for and README.md, and leaves everything else in
# <dir> (other task folders, .conversations/, .mcp-gateway/, *.txt, ...) untouched.
#
# To score a hand run: pwsh evals/dual-layer-eval/tasks/<id>/check.ps1 -Workspace <dir>/<id> -Out score.json
param(
    [Parameter(Mandatory = $true)] [string] $Workspace,
    [string[]] $Task
)
$ErrorActionPreference = 'Stop'
$evalDir = $PSScriptRoot
$tasksDir = Join-Path $evalDir 'tasks'
if (-not (Test-Path -LiteralPath $Workspace -PathType Container)) { throw "workspace directory not found: $Workspace" }
$Workspace = (Resolve-Path -LiteralPath $Workspace).Path
$all = @(Get-ChildItem -LiteralPath $tasksDir -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'check.ps1') } | ForEach-Object Name)
$Task = @($Task | ForEach-Object { $_ -split ',' } | Where-Object { $_ })  # 'pwsh -File' passes "a,b" as one string
$metaOf = @{}
foreach ($id in $all) { $metaOf[$id] = Get-Content (Join-Path $tasksDir "$id/meta.json") -Raw | ConvertFrom-Json }
if (-not $Task) { $Task = @($all | Where-Object { $metaOf[$_].split -ne 'retired' }) }
$reserved = @('.conversations', '.mcp-gateway')

$lines = @(
    '# Dual-layer eval tasks (seeded for hand testing)',
    '',
    'Each folder is one task from `evals/dual-layer-eval/tasks/`: its input files plus `TASK.md`, the exact',
    'prompt the eval sends. Paste the sample prompt into a chat bound to this workspace. The answer keys are',
    'not here; score a finished run with',
    '`pwsh evals/dual-layer-eval/tasks/<id>/check.ps1 -Workspace <this dir>/<id> -Out score.json`.',
    ''
)
foreach ($id in $Task) {
    if ($all -notcontains $id) { throw "unknown task '$id' (known: $($all -join ', '))" }
    if ($reserved -contains $id -or $id -match '[\\/]|^\.') { throw "refusing task id '$id'" }
    $src = Join-Path $tasksDir $id
    $dest = Join-Path $Workspace $id
    if (Test-Path -LiteralPath $dest) {
        $item = Get-Item -LiteralPath $dest -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "refusing to replace reparse point $dest" }
        Remove-Item -LiteralPath $dest -Recurse -Force
    }
    Copy-Item -LiteralPath (Join-Path $src 'fixtures') -Destination $dest -Recurse

    $seed = @($metaOf[$id].seeds)[0]
    $taskMd = Get-Content (Join-Path $src 'task.md') -Raw -Encoding utf8
    $idx = $taskMd.IndexOf("`n---`n")
    if ($idx -lt 0) { throw "$id/task.md has no '---' marker" }
    $message = $taskMd.Substring($idx + 5).TrimStart("`r", "`n").Replace('{SEED}', $seed)
    $note = "> Hand-testing copy: this task lives in the ``$id/`` folder of the workspace. Every path below`n" +
        "> (inputs and ``answers.json``) is relative to that folder, so write ``$id/answers.json``.`n`n"
    Set-Content -LiteralPath (Join-Path $dest 'TASK.md') -Value ($note + $message) -Encoding utf8 -NoNewline
}

# README: every known task that has a seeded folder here (from this run or an earlier one), active first.
$present = @($all | Where-Object { Test-Path -LiteralPath (Join-Path $Workspace "$_/TASK.md") })
$ordered = @($present | Where-Object { $metaOf[$_].split -ne 'retired' }) + @($present | Where-Object { $metaOf[$_].split -eq 'retired' })
foreach ($id in $ordered) {
    $taskMd = Get-Content (Join-Path $tasksDir "$id/task.md") -Raw -Encoding utf8
    $header = ($taskMd.Substring(0, $taskMd.IndexOf("`n---`n")) -split "`n")[0].TrimStart('#', ' ') -replace "^$([regex]::Escape($id))\s*\W\s*", ''
    $tag = if ($metaOf[$id].split -eq 'retired') { ' (retired; see evals/dual-layer-eval/tasks/README.md)' } else { '' }
    $lines += "- **$id** - $header$tag. Prompt: ``Read $id/TASK.md and carry out the task it describes. All of its paths are relative to the $id/ folder.``"
}
Set-Content -LiteralPath (Join-Path $Workspace 'README.md') -Value (($lines -join "`n") + "`n") -Encoding utf8 -NoNewline

foreach ($id in $Task) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $Workspace $id) -Recurse -File)
    $mb = ($files | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("{0}: {1} files, {2:N2} MB" -f $id, $files.Count, $mb)
}
Write-Host "README.md written to $Workspace"
