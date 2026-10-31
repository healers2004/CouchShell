# Starts Tachyon, couchd and the UI, in that order. Tachyon and couchd get no windows, and their
# output goes to log files. Anything already running is left alone, so this is safe to run twice.
# tools/install-startup.ps1 runs it at logon; tools/stop-couchshell.ps1 stops everything.
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$AtLogon  # the UI also keeps itself in front while other startup programs open
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$tachyon = Join-Path $root "build\vendor\tachyon\tachyon\$Configuration\tachyon.exe"
$couchd = Join-Path $root "build\couchd\$Configuration\couchd.exe"
$ui = Join-Path $root "ui\CouchShell\bin\$Configuration\net10.0-windows\CouchShell.exe"

foreach ($program in $tachyon, $couchd, $ui) {
    if (-not (Test-Path $program)) {
        throw "Missing $program. Build the $Configuration configuration first; the README has the commands."
    }
}

$data = Join-Path $env:LOCALAPPDATA 'CouchShell'
$logs = Join-Path $data 'logs'
New-Item -ItemType Directory -Force -Path (Join-Path $data 'data'), $logs | Out-Null

function Test-Running([string]$name) {
    return [bool](Get-Process $name -ErrorAction SilentlyContinue)
}

# cmd handles the redirect to a log file, and CreateNoWindow gives it and the program no window.
function Start-Background([string]$program, [string]$arguments, [string]$log) {
    $logFile = Join-Path $logs "$log.log"
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $env:ComSpec
    $start.Arguments = "/c `"`"$program`" $arguments > `"$logFile`" 2>&1`""
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    [void][Diagnostics.Process]::Start($start)
}

if (-not (Test-Running 'tachyon')) {
    Start-Background $tachyon "--dir `"$(Join-Path $data 'data')`"" 'tachyon'
}
if (-not (Test-Running 'couchd')) {
    # couchd and the UI both retry until Tachyon is up, so there's no need to wait for it here.
    Start-Background $couchd '' 'couchd'
}
if (-not (Test-Running 'CouchShell')) {
    if ($AtLogon) { Start-Process $ui -ArgumentList '--at-logon' } else { Start-Process $ui }
}
