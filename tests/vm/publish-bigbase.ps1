# publish-bigbase.ps1: files a finished bigbase-build VM run in the shared worlds library.
#
#   publish-bigbase.ps1 -Run <run folder> [-Replace] [-LibraryRoot <dir>]
#
# Reads <run>\out\world.txt and <run>\result.json, then writes <LibraryRoot>\<station>\ with:
#   <station>\<station>.save (+ manualsave/quicksave/autosave when the run has them; the folder vm-client-run's -Save wants),
#   lattice\ (the run's data\lattice\<station>, when present), world.txt, md5.txt, README.md.
# Refuses (exit 1, nothing written) when: the run folder or its world.txt or result.json or save is missing, world.txt
# says save-success is not True, unknown is not 0, landed is not yes, the save's world.xml has no HumanSaveData entry
# (P52), or the library folder exists without -Replace.
# -Replace replaces only that one station folder. The save is never copied from a player's saves: it comes from the run.
# Exit 0 published, 1 refused.
param(
    [Parameter(Mandatory)] [string] $Run,
    [switch] $Replace,
    [string] $LibraryRoot = $env:STATIONEERS_WORLDS
)
$ErrorActionPreference = 'Stop'

function Refuse([string] $why) { [Console]::Error.WriteLine("publish-bigbase: REFUSED: $why"); exit 1 }

if (-not $LibraryRoot) { Refuse 'no worlds library: set STATIONEERS_WORLDS or pass -LibraryRoot' }

if (-not (Test-Path -LiteralPath $Run -PathType Container)) { Refuse "no run folder $Run" }
$Run = (Resolve-Path -LiteralPath $Run).Path
$worldTxt = Join-Path $Run 'out\world.txt'
if (-not (Test-Path -LiteralPath $worldTxt)) { Refuse "no world.txt at $worldTxt" }
$resultJson = Join-Path $Run 'result.json'
if (-not (Test-Path -LiteralPath $resultJson)) { Refuse "no result.json at $resultJson" }

# world.txt: one "key value" per line.
$world = @{}
foreach ($l in [IO.File]::ReadAllLines($worldTxt)) {
    if ($l -match '^(\S+) (.*)$') { $world[$Matches[1]] = $Matches[2] }
}
foreach ($k in 'station', 'save-success', 'unknown', 'structures', 'spawned', 'refused', 'held') {
    if (-not $world.ContainsKey($k)) { Refuse "world.txt has no '$k' line" }
}
if ($world['save-success'] -ne 'True') { Refuse "world.txt save-success is '$($world['save-success'])', not True" }
if ($world['unknown'] -ne '0') { Refuse "world.txt unknown is '$($world['unknown'])', not 0 (the game lacks prefabs of the tsv)" }
if ($world['landed'] -ne 'yes') { Refuse "world.txt landed is '$($world['landed'])', not yes: a library world is post-landing" }
$station = $world['station']
if ($station -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { Refuse "station name '$station' is not a plain folder name" }

$result = Get-Content -LiteralPath $resultJson -Raw | ConvertFrom-Json
if ($result.status -ne 'pass') { Refuse "result.json status is '$($result.status)', not pass" }

$saveDir = Join-Path $Run "data\saves\$station"
$saveFile = Join-Path $saveDir "$station.save"
if (-not (Test-Path -LiteralPath $saveFile)) { Refuse "no save at $saveFile" }
# P52: a client on a save with no saved character gets a new player (the lander plays), and that run ends infra.
# So the save's world.xml must hold a HumanSaveData entry.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$hasHuman = $false
try {
    $zip0 = [IO.Compression.ZipFile]::OpenRead($saveFile)
    try {
        $wx = $zip0.GetEntry('world.xml')
        if (-not $wx) { Refuse "the save $saveFile has no world.xml" }
        $rd0 = New-Object IO.StreamReader($wx.Open())
        try { while (-not $hasHuman -and ($line0 = $rd0.ReadLine()) -ne $null) { if ($line0.Contains('xsi:type="HumanSaveData"')) { $hasHuman = $true } } }
        finally { $rd0.Dispose() }
    } finally { $zip0.Dispose() }
} catch [IO.InvalidDataException] { Refuse "the save $saveFile is not a readable zip" }
if (-not $hasHuman) { Refuse 'the save has no saved character (no xsi:type="HumanSaveData" in world.xml): a client loading it gets a new player' }
$latticeSrc = Join-Path $Run "data\lattice\$station"

$dest = Join-Path $LibraryRoot $station
if (Test-Path -LiteralPath $dest) {
    if (-not $Replace) { Refuse "$dest exists; pass -Replace to replace it" }
}

# --- all checks passed; write ---
if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $dest $station) | Out-Null
Copy-Item -Recurse -Force (Join-Path $saveDir '*') (Join-Path $dest $station)
$hasLattice = Test-Path -LiteralPath $latticeSrc
if ($hasLattice) { Copy-Item -Recurse -Force $latticeSrc (Join-Path $dest 'lattice') }
Copy-Item -LiteralPath $worldTxt (Join-Path $dest 'world.txt')

$saveMd5 = (Get-FileHash -LiteralPath (Join-Path $dest "$station\$station.save") -Algorithm MD5).Hash
if ($saveMd5 -ne (Get-FileHash -LiteralPath $saveFile -Algorithm MD5).Hash) { throw 'copied save md5 differs from the run save' }
$worldMd5 = (Get-FileHash -LiteralPath (Join-Path $dest 'world.txt') -Algorithm MD5).Hash
[IO.File]::WriteAllText((Join-Path $dest 'md5.txt'), "$saveMd5  $station/$station.save`n$worldMd5  world.txt`n")

# Game facts from the run's logs (the build number is not in a file; the Unity version is the stable marker).
$unity = ''
$player = Join-Path $Run 'Player.log'
if (Test-Path -LiteralPath $player) {
    $m = Select-String -LiteralPath $player -Pattern '^Initialize engine version: (.+)$' | Select-Object -First 1
    if ($m) { $unity = $m.Matches[0].Groups[1].Value }
}
$gameVersion = 'unknown'
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($saveFile)
    try {
        $entry = $zip.GetEntry('world_meta.xml')
        if ($entry) {
            $rd = New-Object IO.StreamReader($entry.Open())
            $meta = $rd.ReadToEnd(); $rd.Dispose()
            if ($meta -match '<GameVersion>([^<]+)</GameVersion>') { $gameVersion = $Matches[1] }
        }
    } finally { $zip.Dispose() }
} catch { }
$mods = if ($result.info.mods) { (($result.info.mods.PSObject.Properties | % { "$($_.Name) $($_.Value)" }) -join ', ') } else { 'unknown' }
$refusedLines = @()
$bep = Join-Path $Run 'LogOutput.log'
foreach ($f in $bep, $player) {
    if ((Test-Path -LiteralPath $f) -and -not $refusedLines) {
        $refusedLines = @(Select-String -LiteralPath $f -Pattern 'LIVE bigbase: refused \S+ at ' | % { ($_.Line -replace '^.*(LIVE bigbase: refused)', '$1') })
    }
}
$refusedPrefabs = @($refusedLines | % { if ($_ -match 'refused (\S+) at ') { $Matches[1] } } | Group-Object | Sort-Object Count -Descending | % { "$($_.Name) x$($_.Count)" })

$heldN = [int]$world['held']
$what = if ($heldN -gt 0) {
    "the generated big base with $heldN structures held back as a plan of ghosts ($($world['plans']) plan(s), $($world['ghosts']) ghosts); the other $($world['spawned']) are built"
} else { "the full generated big base, built; no plan" }
$kitSha = if ($result.info.kit -and $result.info.kit.sha) { $result.info.kit.sha } else { 'unknown' }
$buildCmd = "vm-run.ps1 start -Mod Lattice -Kind proof -Proof bigbase-build -Ref $kitSha -Param bigbase=<$($world['tsv'])>,held=$heldN"
$vmCommit = if ($result.info.kit -and $result.info.kit.rigvmExtract) { $result.info.kit.rigvmExtract } elseif ($result.info.rigVM) { $result.info.rigVM } elseif ($result.info.rigvm) { $result.info.rigvm } else { 'unknown' }
$publisher = (& git -C $PSScriptRoot rev-parse --short HEAD 2>$null)
if (-not $publisher) { $publisher = 'unknown' }
$readme = @"
# $station

Post-landing test world: the player is on the ground at the stand spot, the structures are built, and no lander
sequence is pending (world.txt ``landed $($world['landed'])``). Never copied from a player's saves: generated and built on the VM.

## What it holds
- $what
- structures in the world (world.txt ``structures``): $($world['structures'])
- spawned $($world['spawned']), refused $($world['refused']), prefabs the game lacks $($world['unknown'])
- plans $($world['plans']), ghosts $($world['ghosts'])
- stand spot (m): $($world['stand'])
- placement (world.txt): offset $($world['offset']) (dx dy dz, the base shifted onto the measured ground), landing $($world['landing']) (x y z, ground at the landing spot). Positions in the save are world coordinates.
- ground heights under the base: min $($world['ground-min']), max $($world['ground-max']); lowest piece $($world['lowest-piece'])
- world $($world['world']), difficulty $($world['difficulty']), start $($world['start'])
- Tests read counts from world.txt, never from literals.

## Identity
- save md5 (``$station/$station.save``): $saveMd5
- generator seed $($world['seed']); tsv ``$($world['tsv'])`` SHA1 $($world['tsv-sha1'])
- catalogue SHA1 $($world['catalogue-sha1']); mix SHA1 $($world['mix-sha1'])
- mods in the run: $mods (world.txt lattice-version $($world['lattice-version']))
- game build $gameVersion (from the save's world_meta.xml); Unity $unity
- RigVM commit: $vmCommit

## Refused pieces
$(if ($refusedPrefabs.Count) { ($refusedPrefabs | % { "- $_" }) -join "`n" } else { '- none' })

## How it was made
- run: ``$($result.mod)`` stamp ``$(Split-Path $Run -Leaf)`` (runId $($result.runId)), owner $($result.owner), rig $($result.rig)
- build: ``$buildCmd``, args ``$($result.info.args)``
- published with: ``Lattice tests\vm\publish-bigbase.ps1 at $publisher`` (``-Run <that run folder>``)

## How to rebuild
Regenerate the tsv (Lattice.BigBase), run the build above, publish with ``-Replace``, then run the load check below.

## Load check
The bigbase-load run loads this world.
"@
[IO.File]::WriteAllText((Join-Path $dest 'README.md'), $readme.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))

Write-Host "publish-bigbase: published $station to $dest"
Write-Host "  save md5 $saveMd5; structures $($world['structures']); spawned $($world['spawned']); refused $($world['refused']); plans $($world['plans']); ghosts $($world['ghosts']); landed $($world['landed'])"
if ($refusedPrefabs.Count) { Write-Host "  refused prefabs: $($refusedPrefabs -join ', ')" }
exit 0
