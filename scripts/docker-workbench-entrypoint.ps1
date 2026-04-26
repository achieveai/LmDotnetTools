$ErrorActionPreference = "Stop"

$Command = @($args)

$workbenchUser = if ([string]::IsNullOrWhiteSpace($env:WORKBENCH_USER)) { "dev" } else { $env:WORKBENCH_USER }
$runAsRoot = $env:WORKBENCH_RUN_AS_ROOT -eq "true"
$workspace = if ([string]::IsNullOrWhiteSpace($env:WORKBENCH_WORKSPACE)) { "/workspace" } else { $env:WORKBENCH_WORKSPACE }

$fixupPaths = @("/ms-playwright")

if (-not [string]::IsNullOrWhiteSpace($env:WORKBENCH_FIXUP_PATHS)) {
    $fixupPaths = @($env:WORKBENCH_FIXUP_PATHS -split "\|") + $fixupPaths
}

$fixupPaths = $fixupPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

foreach ($path in $fixupPaths) {
    if (Test-Path $path) {
        & chown "-R" "${workbenchUser}:${workbenchUser}" $path 2>$null | Out-Null
    }
}

if ($Command.Count -eq 0) {
    $Command = @("pwsh", "-NoLogo")
}

$commandName = $Command[0]
$commandArgs = if ($Command.Count -gt 1) { $Command[1..($Command.Count - 1)] } else { @() }

if ($runAsRoot) {
    & $commandName @commandArgs
    exit $LASTEXITCODE
}

& sudo "-E" "-H" "-u" $workbenchUser "--" $commandName @commandArgs
exit $LASTEXITCODE
