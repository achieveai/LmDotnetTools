#requires -Version 7
# Registers the two Scheduled Tasks that keep Reviewd reviewing. Re-run safe (replaces them).
#   "ADO Reviewd Watchdog"  - ensure-reviewd.ps1 every 5 minutes, and at logon.
#   "ADO Reviewd Discovery" - discover.ps1 every 2 minutes.
# Both run as the interactive user through `conhost --headless`, so no console window flashes.
# Pause everything: Disable-ScheduledTask "ADO Reviewd Watchdog" (or create state\PAUSED).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pwsh = (Get-Command pwsh).Source
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive

function Register([string] $name, [string] $script, [TimeSpan] $every, [bool] $atLogon) {
    $action = New-ScheduledTaskAction -Execute 'conhost.exe' -WorkingDirectory $root `
        -Argument "--headless `"$pwsh`" -NoProfile -File `"$(Join-Path $root $script)`""
    $repeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval $every `
        -RepetitionDuration (New-TimeSpan -Days 1)
    $repeat.Repetition.Duration = ''   # empty Duration means repeat forever
    $triggers = @($repeat)
    if ($atLogon) {
        $logon = New-ScheduledTaskTrigger -AtLogOn -User $principal.UserId
        $logon.Delay = 'PT2M'
        $triggers += $logon
    }
    Register-ScheduledTask -TaskName $name -Action $action -Trigger $triggers -Settings $settings -Principal $principal -Force | Out-Null
    $t = Get-ScheduledTask -TaskName $name
    "$name registered: every $($t.Triggers[0].Repetition.Interval), duration '$($t.Triggers[0].Repetition.Duration)', state $($t.State)"
}

Register 'ADO Reviewd Watchdog' 'ensure-reviewd.ps1' (New-TimeSpan -Minutes 5) $true
Register 'ADO Reviewd Discovery' 'discover.ps1' (New-TimeSpan -Minutes 2) $false
