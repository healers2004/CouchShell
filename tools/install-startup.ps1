# Starts CouchShell at logon through Task Scheduler. Not a Windows service, because services run in
# a separate session and can't open apps on the desktop.
# Usage: tools/install-startup.ps1            installs or updates the task
#        tools/install-startup.ps1 -Remove    removes it
# If Windows denies access, run it once from an administrator PowerShell.
param([switch]$Remove)
$ErrorActionPreference = 'Stop'

$taskName = 'CouchShell'

if ($Remove) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Output "Removed the '$taskName' task."
    return
}

$script = Join-Path $PSScriptRoot 'start-couchshell.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
# A laptop on a TV may be on battery, and these programs must keep running for the whole session.
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Output "CouchShell will start at logon. Remove it with: tools/install-startup.ps1 -Remove"
