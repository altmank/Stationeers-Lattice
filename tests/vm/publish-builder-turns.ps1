#Requires -Version 7.5
# publish-builder-turns.ps1: files a finished builder-turns VM run in the shared worlds library.
#
#   publish-builder-turns.ps1 -Run <run folder> [-Replace] [-LibraryRoot <dir>]
#
# The sibling of publish-bigbase.ps1 for a world the game's builder placed: same library layout, same refusals (run folder,
# world.txt, result.json, save, save-success, landed, HumanSaveData, existing folder), but its world.txt keys are the
# builder run's (pieces, turns-tried, turns-placed, turns-refused, turns-outside) and its README says what the world holds.
# Writes <LibraryRoot>\<station>\ with <station>\<station>.save (and the run's other save files), world.txt, md5.txt, README.md.
# Exit 0 published, 1 refused.
param(
    [Parameter(Mandatory)] [string] $Run,
    [switch] $Replace,
    [string] $LibraryRoot = $env:STATIONEERS_WORLDS
)
$ErrorActionPreference = 'Stop'

function Refuse([string] $why) { [Console]::Error.WriteLine("publish-builder-turns: REFUSED: $why"); exit 1 }

if (-not $LibraryRoot) { Refuse 'no worlds library: set STATIONEERS_WORLDS or pass -LibraryRoot' }
if (-not (Test-Path -LiteralPath $Run -PathType Container)) { Refuse "no run folder $Run" }
$Run = (Resolve-Path -LiteralPath $Run).Path
$worldTxt = Join-Path $Run 'out\world.txt'
if (-not (Test-Path -LiteralPath $worldTxt)) { Refuse "no world.txt at $worldTxt" }
$resultJson = Join-Path $Run 'result.json'
if (-not (Test-Path -LiteralPath $resultJson)) { Refuse "no result.json at $resultJson" }

$world = @{}
foreach ($l in [IO.File]::ReadAllLines($worldTxt)) {
    if ($l -match '^(\S+) (.*)$') { $world[$Matches[1]] = $Matches[2] }
}
foreach ($k in 'station', 'save-success', 'landed', 'pieces', 'turns-tried', 'turns-placed', 'turns-refused', 'turns-outside', 'structures') {
    if (-not $world.ContainsKey($k)) { Refuse "world.txt has no '$k' line" }
}
if ($world['save-success'] -ne 'True') { Refuse "world.txt save-success is '$($world['save-success'])', not True" }
if ($world['landed'] -ne 'yes') { Refuse "world.txt landed is '$($world['landed'])', not yes: a library world is post-landing" }
if ([int]$world['turns-placed'] -lt 1) { Refuse 'world.txt turns-placed is 0: nothing was built' }
$station = $world['station']
if ($station -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { Refuse "station name '$station' is not a plain folder name" }

$result = Get-Content -LiteralPath $resultJson -Raw | ConvertFrom-Json -DateKind String
if ($result.status -ne 'pass') { Refuse "result.json status is '$($result.status)', not pass" }

$saveDir = Join-Path $Run "data\saves\$station"
$saveFile = Join-Path $saveDir "$station.save"
if (-not (Test-Path -LiteralPath $saveFile)) { Refuse "no save at $saveFile" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$hasHuman = $false
$gameVersion = 'unknown'
try {
    $zip = [IO.Compression.ZipFile]::OpenRead($saveFile)
    try {
        $wx = $zip.GetEntry('world.xml')
        if (-not $wx) { Refuse "the save $saveFile has no world.xml" }
        $rd = New-Object IO.StreamReader($wx.Open())
        try { while (-not $hasHuman -and ($l0 = $rd.ReadLine()) -ne $null) { if ($l0.Contains('xsi:type="HumanSaveData"')) { $hasHuman = $true } } }
        finally { $rd.Dispose() }
        $meta = $zip.GetEntry('world_meta.xml')
        if ($meta) {
            $rm = New-Object IO.StreamReader($meta.Open()); $text = $rm.ReadToEnd(); $rm.Dispose()
            if ($text -match '<GameVersion>([^<]+)</GameVersion>') { $gameVersion = $Matches[1] }
        }
    } finally { $zip.Dispose() }
} catch [IO.InvalidDataException] { Refuse "the save $saveFile is not a readable zip" }
if (-not $hasHuman) { Refuse 'the save has no saved character (no xsi:type="HumanSaveData" in world.xml): a client loading it gets a new player' }

$dest = Join-Path $LibraryRoot $station
if ((Test-Path -LiteralPath $dest) -and -not $Replace) { Refuse "$dest exists; pass -Replace to replace it" }

if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $dest $station) | Out-Null
Copy-Item -Recurse -Force (Join-Path $saveDir '*') (Join-Path $dest $station)
Copy-Item -LiteralPath $worldTxt (Join-Path $dest 'world.txt')
$saveMd5 = (Get-FileHash -LiteralPath (Join-Path $dest "$station\$station.save") -Algorithm MD5).Hash
if ($saveMd5 -ne (Get-FileHash -LiteralPath $saveFile -Algorithm MD5).Hash) { throw 'copied save md5 differs from the run save' }
$worldMd5 = (Get-FileHash -LiteralPath (Join-Path $dest 'world.txt') -Algorithm MD5).Hash
[IO.File]::WriteAllText((Join-Path $dest 'md5.txt'), "$saveMd5  $station/$station.save`n$worldMd5  world.txt`n")

$unity = ''
$player = Join-Path $Run 'Player.log'
if (Test-Path -LiteralPath $player) {
    $m = Select-String -LiteralPath $player -Pattern '^Initialize engine version: (.+)$' | Select-Object -First 1
    if ($m) { $unity = $m.Matches[0].Groups[1].Value }
}
$placedLines = @()
$bep = Join-Path $Run 'LogOutput.log'
foreach ($f in $bep, $player) {
    if ((Test-Path -LiteralPath $f) -and -not $placedLines) {
        $placedLines = @(Select-String -LiteralPath $f -Pattern 'LIVE builder-turns: \S+ turn \d+ (placed|refused) ' | % { ($_.Line -replace '^.*(LIVE builder-turns: )', '') })
    }
}
$refusedLines = @($placedLines | ? { $_ -match ' refused ' })
$mods = if ($result.info.mods) { (($result.info.mods.PSObject.Properties | % { "$($_.Name) $($_.Value)" }) -join ', ') } else { 'unknown' }
$kitSha = if ($result.info.kit -and $result.info.kit.sha) { $result.info.kit.sha } else { 'unknown' }
$vmCommit = if ($result.info.kit -and $result.info.kit.rigvmExtract) { $result.info.kit.rigvmExtract } elseif ($result.info.rigVM) { $result.info.rigVM } elseif ($result.info.rigvm) { $result.info.rigvm } else { 'unknown' }
$publisher = try { (& git -C $PSScriptRoot rev-parse --short HEAD 2>$null) } catch { $null }   # 5.1 turns git's stderr into an error under Stop
if (-not $publisher) { $publisher = 'unknown' }
$readme = @"
# $station

Post-landing test world: the player is on the ground, the pieces are built, and no lander sequence is pending
(world.txt ``landed $($world['landed'])``). Never copied from a player's saves: built on the VM by driving the game's own builder.

## What it holds
- one column of cells for each structure piece the catalogue limits to fewer than 24 turns and the builder places in a cell,
  one cell for each turn the builder's rotate keys reach, on a frame floor
- pieces tried $($world['pieces']) (skipped: $($world['pieces-skipped']) not placed in a cell or without a kit, $($world['pieces-no-cursor']) without a cursor)
- turns tried $($world['turns-tried']), placed $($world['turns-placed']), refused $($world['turns-refused']), placed in a turn the catalogue does not allow $($world['turns-outside'])
- frame floor $($world['floor']); structures in the world (world.txt ``structures``): $($world['structures'])
- world $($world['world']), difficulty $($world['difficulty']), start $($world['start'])
- Tests read counts from world.txt, never from literals.

## Identity
- save md5 (``$station/$station.save``): $saveMd5
- mods in the run: $mods (world.txt lattice-version $($world['lattice-version']))
- game build $gameVersion (from the save's world_meta.xml); Unity $unity
- RigVM commit: $vmCommit

## Refused turns
$(if ($refusedLines.Count) { ($refusedLines | % { "- $_" }) -join "`n" } else { '- none' })

## How it was made
- run: ``$($result.mod)`` stamp ``$(Split-Path $Run -Leaf)`` (runId $($result.runId)), owner $($result.owner), rig $($result.rig)
- build: ``vm-run.ps1 start -Mod Lattice -Kind proof -Proof builder-turns -Ref $kitSha``, args ``$($result.info.args)``
- published with: ``Lattice tests\vm\publish-builder-turns.ps1 at $publisher`` (``-Run <that run folder>``)

## How to rebuild
Run the build above and publish with ``-Replace``. ``AllowedTurnsSaveTests`` reads the world through make-fixtures.
"@
[IO.File]::WriteAllText((Join-Path $dest 'README.md'), $readme.Replace("`r`n", "`n"), (New-Object Text.UTF8Encoding $false))

Write-Host "publish-builder-turns: published $station to $dest"
Write-Host "  save md5 $saveMd5; pieces $($world['pieces']); turns tried $($world['turns-tried']), placed $($world['turns-placed']), refused $($world['turns-refused']), outside $($world['turns-outside'])"
exit 0
