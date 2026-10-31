# Starts Tachyon, couchd and the UI, in that order, from a Release build. Anything already running
# is left alone, so this is safe to run twice. tools/install-startup.ps1 runs it at logon.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$tachyon = Join-Path $root 'build\vendor\tachyon\tachyon\Release\tachyon.exe'
$couchd = Join-Path $root 'build\couchd\Release\couchd.exe'
$ui = Join-Path $root 'ui\CouchShell\bin\Release\net10.0-windows\CouchShell.exe'

foreach ($program in $tachyon, $couchd, $ui) {
    if (-not (Test-Path $program)) {
        throw "Missing $program. Build a Release first; the README has the commands."
    }
}

$data = Join-Path $env:LOCALAPPDATA 'CouchShell'
$logs = Join-Path $data 'logs'
New-Item -ItemType Directory -Force -Path (Join-Path $data 'data'), $logs | Out-Null

function Test-Running([string]$name) {
    return [bool](Get-Process $name -ErrorAction SilentlyContinue)
}

function Start-Background([string]$program, [string[]]$arguments, [string]$log) {
    $options = @{
        FilePath               = $program
        NoNewWindow            = $true
        RedirectStandardOutput = (Join-Path $logs "$log.log")
        RedirectStandardError  = (Join-Path $logs "$log.error.log")
    }
    if ($arguments) { $options.ArgumentList = $arguments }
    Start-Process @options
}

if (-not (Test-Running 'tachyon')) {
    Start-Background $tachyon @('--dir', "`"$(Join-Path $data 'data')`"") 'tachyon'
}
if (-not (Test-Running 'couchd')) {
    # couchd and the UI both retry until Tachyon is up, so there's no need to wait for it here.
    Start-Background $couchd @() 'couchd'
}
if (-not (Test-Running 'CouchShell')) {
    Start-Process $ui
}
