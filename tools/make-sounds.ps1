# Synthesizes the UI sounds into ui/CouchShell/Assets/Sounds. They're generated here rather than
# downloaded, so there's nothing to license. Run it only when changing a sound; the .wav files are
# committed. Usage: powershell -ExecutionPolicy Bypass -File tools\make-sounds.ps1
$ErrorActionPreference = 'Stop'

$rate = 44100
$folder = Join-Path (Split-Path -Parent $PSScriptRoot) 'ui\CouchShell\Assets\Sounds'
New-Item -ItemType Directory -Force -Path $folder | Out-Null
Get-ChildItem $folder -Filter *.wav | Remove-Item

# Each note: a kind of sound, frequency in Hz, start and length in seconds, loudness from 0 to 1.
function Note($kind, $hz, $at, $length, $level) {
    [pscustomobject]@{ Kind = $kind; Hz = $hz; At = $at; Length = $length; Level = $level }
}

$noise = New-Object Random 7  # the same seed every time, so the files don't change between runs

function Sample($note, [double]$t) {
    $phase = 2 * [Math]::PI * $note.Hz * $t
    switch ($note.Kind) {
        # A soft bell: a fundamental plus a quieter octave, fading away.
        'bell' { return [Math]::Exp(-5 * $t / $note.Length) * ([Math]::Sin($phase) + 0.3 * [Math]::Sin(2 * $phase)) / 1.3 }
        # A struck wooden bar: warm, with a bright overtone that dies almost at once.
        'wood' { return [Math]::Exp(-7 * $t / $note.Length) * ([Math]::Sin($phase) + 0.35 * [Math]::Exp(-40 * $t) * [Math]::Sin(3.93 * $phase)) / 1.35 }
        # A crisp console-style tick: a tiny burst of noise over a short tone.
        'click' { return 0.55 * [Math]::Exp(-$t / 0.004) * ($noise.NextDouble() * 2 - 1) + 0.6 * [Math]::Exp(-$t / 0.02) * [Math]::Sin($phase) }
    }
}

function Write-Sound([string]$name, [double]$seconds, [object[]]$notes) {
    $count = [int]($seconds * $rate)
    $mix = New-Object double[] $count
    foreach ($note in $notes) {
        $start = [int]($note.At * $rate)
        $length = [int]($note.Length * $rate)
        for ($i = 0; $i -lt $length -and ($start + $i) -lt $count; $i++) {
            $t = $i / $rate
            $attack = [Math]::Min(1.0, $t / 0.001)  # a tiny ramp, so the start doesn't pop
            $mix[$start + $i] += $note.Level * $attack * (Sample $note $t)
        }
    }

    $file = New-Object IO.MemoryStream
    $writer = New-Object IO.BinaryWriter $file
    $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
    $writer.Write([uint32](36 + $count * 2))
    $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
    $writer.Write([uint32]16)
    $writer.Write([uint16]1)         # uncompressed
    $writer.Write([uint16]1)         # mono
    $writer.Write([uint32]$rate)
    $writer.Write([uint32]($rate * 2))
    $writer.Write([uint16]2)
    $writer.Write([uint16]16)
    $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
    $writer.Write([uint32]($count * 2))
    foreach ($sample in $mix) {
        $writer.Write([int16][Math]::Max(-32767, [Math]::Min(32767, $sample * 32767)))
    }
    $writer.Flush()
    [IO.File]::WriteAllBytes((Join-Path $folder "$name.wav"), $file.ToArray())
    "Wrote $name.wav ($($file.Length) bytes)"
}

# Soft: warm wooden taps. The default.
Write-Sound 'soft-move' 0.12 @((Note 'wood' 784 0 0.12 0.21))
Write-Sound 'soft-select' 0.24 @((Note 'wood' 784 0 0.12 0.25), (Note 'wood' 1175 0.075 0.16 0.25))
Write-Sound 'soft-back' 0.22 @((Note 'wood' 784 0 0.11 0.22), (Note 'wood' 587 0.07 0.15 0.22))

# Click: crisp and short, like a console's menus.
Write-Sound 'click-move' 0.09 @((Note 'click' 2200 0 0.09 0.22))
Write-Sound 'click-select' 0.18 @((Note 'click' 2200 0 0.09 0.26), (Note 'click' 3000 0.06 0.11 0.26))
Write-Sound 'click-back' 0.17 @((Note 'click' 2200 0 0.09 0.24), (Note 'click' 1500 0.06 0.11 0.24))

# Chime: the bright bells from before.
Write-Sound 'chime-move' 0.16 @((Note 'bell' 1175 0 0.16 0.2))
Write-Sound 'chime-select' 0.28 @((Note 'bell' 1319 0 0.14 0.26), (Note 'bell' 1976 0.08 0.2 0.26))
Write-Sound 'chime-back' 0.26 @((Note 'bell' 1319 0 0.13 0.22), (Note 'bell' 988 0.08 0.18 0.22))

# A rising chord, for the startup animation.
Write-Sound 'startup' 1.5 @((Note 'bell' 523 0 1.0 0.22), (Note 'bell' 784 0.14 0.95 0.22), (Note 'bell' 1047 0.28 0.9 0.22), (Note 'bell' 1568 0.42 1.05 0.2))
