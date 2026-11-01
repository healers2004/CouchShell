# Builds the Windows installer at out\CouchShellSetup-<version>.exe, from a clean copy of the program.
# Needs CMake, MSVC, the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
# Usage: powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'out'
$cppBuild = Join-Path $out 'cpp'
$publish = Join-Path $out 'publish'

function Invoke-Checked([string]$program, [string[]]$arguments) {
    & $program @arguments
    if ($LASTEXITCODE -ne 0) { throw "$program failed with exit code $LASTEXITCODE" }
}

function Find-Program([string]$name, [string[]]$places) {
    $found = Get-Command $name -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    foreach ($place in $places) {
        if (Test-Path $place) { return $place }
    }
    throw "$name not found. The top of this script lists what it needs."
}

$cmake = Find-Program 'cmake' @("$env:ProgramFiles\CMake\bin\cmake.exe")
$dotnet = Find-Program 'dotnet' @("$env:ProgramFiles\dotnet\dotnet.exe")
$iscc = Find-Program 'ISCC' @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe")

$project = Join-Path $root 'ui\CouchShell\CouchShell.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# The static C++ runtime, so the installed programs don't need the Visual C++ Redistributable.
# It's a build setting rather than a change to any CMakeLists, which keeps vendor/tachyon untouched.
Invoke-Checked $cmake @('-S', $root, '-B', $cppBuild, '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW', '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded')
Invoke-Checked $cmake @('--build', $cppBuild, '--config', 'Release', '--target', 'tachyon', 'couchd')

# Self-contained: the program brings its own .NET, so the PC doesn't need it installed.
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
Invoke-Checked $dotnet @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishReadyToRun=true', '-o', $publish)

$backend = New-Item -ItemType Directory -Force -Path (Join-Path $publish 'backend')
Copy-Item (Join-Path $cppBuild 'vendor\tachyon\tachyon\Release\tachyon.exe') $backend
Copy-Item (Join-Path $cppBuild 'couchd\Release\couchd.exe') $backend

Invoke-Checked $iscc @("/DAppVersion=$version", "/DPublishFolder=$publish", (Join-Path $root 'installer\CouchShell.iss'))
"Built $(Join-Path $out "CouchShellSetup-$version.exe")"
