# End-to-end check on Windows: the real Tachyon and the real couchd, driven over the same pub/sub
# protocol the UI uses. Launches short-lived console programs only, and never really runs a power
# action or changes the volume.
# Usage: tools/integration_test.ps1 path\to\tachyon.exe path\to\couchd.exe
param(
    [Parameter(Mandatory)][string]$Tachyon,
    [Parameter(Mandatory)][string]$Couchd
)
$ErrorActionPreference = 'Stop'
$port = 6391  # not 6380, so this can run next to a real CouchShell

function Send-Command($stream, [string[]]$arguments) {
    $text = "*$($arguments.Count)`r`n"
    foreach ($argument in $arguments) {
        $text += "`$$([Text.Encoding]::UTF8.GetByteCount($argument))`r`n$argument`r`n"
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $stream.Write($bytes, 0, $bytes.Length)
}

function Read-Line($stream) {
    $bytes = New-Object System.Collections.Generic.List[byte]
    while ($true) {
        $next = $stream.ReadByte()
        if ($next -lt 0) { throw 'connection closed' }
        if ($next -eq 10 -and $bytes.Count -gt 0 -and $bytes[$bytes.Count - 1] -eq 13) {
            $bytes.RemoveAt($bytes.Count - 1)
            return [Text.Encoding]::UTF8.GetString($bytes.ToArray())
        }
        $bytes.Add([byte]$next)
    }
}

# Reads one reply: a string, an integer, $null, or an array of those.
function Read-Reply($stream) {
    $line = Read-Line $stream
    $rest = $line.Substring(1)
    switch ($line[0]) {
        '+' { return $rest }
        '-' { throw "server error: $rest" }
        ':' { return [long]$rest }
        '$' {
            if ([int]$rest -lt 0) { return $null }
            $buffer = New-Object byte[] ([int]$rest + 2)
            $filled = 0
            while ($filled -lt $buffer.Length) {
                $filled += $stream.Read($buffer, $filled, $buffer.Length - $filled)
            }
            return [Text.Encoding]::UTF8.GetString($buffer, 0, [int]$rest)
        }
        '*' {
            $items = @()
            for ($i = 0; $i -lt [int]$rest; $i++) { $items += , (Read-Reply $stream) }
            return , $items
        }
    }
    throw "unexpected reply: $line"
}

function Open-Connection {
    $client = New-Object System.Net.Sockets.TcpClient('127.0.0.1', $port)
    $client.ReceiveTimeout = 15000
    return $client
}

function Run-Command([string[]]$arguments) {
    Send-Command $commandStream $arguments
    return Read-Reply $commandStream
}

# Waits for the next event and checks it starts with $expected.
function Expect-Event([string]$expected) {
    while ($true) {
        $message = Read-Reply $eventStream
        $payload = $message[2]
        if ($payload -like 'volume *') { continue }  # depends on this machine's audio device
        if (-not $payload.StartsWith($expected)) { throw "expected event '$expected', got '$payload'" }
        Write-Output "ok: event '$payload'"
        return $payload
    }
}

function Expect-Equal($actual, $expected, [string]$what) {
    if ($actual -ne $expected) { throw "$what`: expected '$expected', got '$actual'" }
    Write-Output "ok: $what"
}

function Wait-ForCouchd {
    for ($i = 0; $i -lt 100; $i++) {
        if ((Run-Command @('EXISTS', 'couch:alive')) -eq 1) { return }
        Start-Sleep -Milliseconds 100
    }
    throw 'couchd never reported in'
}

$cmd = Join-Path $env:SystemRoot 'System32\cmd.exe'
$server = Start-Process $Tachyon -ArgumentList '--no-persist', '--port', $port -PassThru -WindowStyle Hidden
$helper = $null
try {
    $commandClient = $null
    for ($i = 0; $i -lt 50 -and -not $commandClient; $i++) {
        try { $commandClient = Open-Connection } catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $commandClient) { throw 'Tachyon did not start listening' }
    $commandStream = $commandClient.GetStream()

    $eventClient = Open-Connection
    $eventStream = $eventClient.GetStream()
    Send-Command $eventStream @('SUBSCRIBE', 'couch:events')
    [void](Read-Reply $eventStream)

    # A leftover from a couchd that died must not survive a new one starting.
    [void](Run-Command @('SET', 'couch:running', 'stale'))

    Expect-Equal (Run-Command @('PUBLISH', 'couch:requests', 'launch nobody')) 0 'PUBLISH reports no listener before couchd starts'

    $helper = Start-Process $Couchd -ArgumentList '--port', $port, '--dry-run-power' -PassThru -WindowStyle Hidden
    Wait-ForCouchd
    Expect-Equal (Run-Command @('EXISTS', 'couch:running')) 0 'a stale running key is cleared'

    # A normal launch and exit. ping waits about 2 seconds.
    [void](Run-Command @('HSET', 'app:slow', 'type', 'exe', 'target', $cmd, 'args', '/c ping -n 3 127.0.0.1 >nul'))
    Expect-Equal (Run-Command @('PUBLISH', 'couch:requests', 'launch slow')) 1 'couchd is listening'
    [void](Expect-Event 'started slow')
    Expect-Equal (Run-Command @('GET', 'couch:running')) 'slow' 'running key is set while the app runs'

    # A second launch is refused while the first one runs.
    [void](Run-Command @('PUBLISH', 'couch:requests', 'launch slow'))
    [void](Expect-Event 'launch_failed slow Another app')

    [void](Expect-Event 'exited slow')
    Expect-Equal (Run-Command @('EXISTS', 'couch:running')) 0 'running key is cleared after exit'
    Expect-Equal (Run-Command @('LRANGE', 'couch:recent', '0', '0'))[0] 'slow' 'recent list updated'
    $session = (Run-Command @('LRANGE', 'couch:sessions', '0', '0'))[0]
    if ($session -notmatch '^slow\t\d+\t\d+$') { throw "bad session record '$session'" }
    Write-Output 'ok: session recorded'

    # An app that exits immediately still goes through both events.
    [void](Run-Command @('HSET', 'app:quick', 'type', 'exe', 'target', $cmd, 'args', '/c exit 3'))
    [void](Run-Command @('PUBLISH', 'couch:requests', 'launch quick'))
    [void](Expect-Event 'started quick')
    [void](Expect-Event 'exited quick')

    # Failures are reported, and couchd keeps working afterwards.
    [void](Run-Command @('HSET', 'app:gone', 'type', 'exe', 'target', 'C:\does\not\exist.exe'))
    [void](Run-Command @('PUBLISH', 'couch:requests', 'launch gone'))
    [void](Expect-Event 'launch_failed gone The configured executable could not be found.')
    [void](Run-Command @('PUBLISH', 'couch:requests', 'launch ghost'))
    [void](Expect-Event 'launch_failed ghost This app is not configured.')
    [void](Run-Command @('HSET', 'app:bad', 'type', 'web', 'target', 'file:///C:/secret.txt'))
    [void](Run-Command @('PUBLISH', 'couch:requests', 'launch bad'))
    [void](Expect-Event 'launch_failed bad The configured web address is not valid.')

    # Dry run: nothing really happens, and no failure is reported.
    [void](Run-Command @('PUBLISH', 'couch:requests', 'power shutdown'))
    [void](Run-Command @('PUBLISH', 'couch:requests', 'launch quick'))
    [void](Expect-Event 'started quick')
    [void](Expect-Event 'exited quick')

    # The heartbeat expires when couchd dies, which is how the UI notices.
    Stop-Process $helper -Force
    $helper = $null
    Start-Sleep -Milliseconds 5500
    Expect-Equal (Run-Command @('EXISTS', 'couch:alive')) 0 'heartbeat expires after couchd dies'

    Write-Output 'integration test passed'
}
finally {
    if ($helper) { Stop-Process $helper -Force -ErrorAction SilentlyContinue }
    Stop-Process $server -Force -ErrorAction SilentlyContinue
}
