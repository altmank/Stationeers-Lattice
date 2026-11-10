<#
.SYNOPSIS
    Fills tests/fixtures/generated/ (git-ignored): verbatim copies of the named saves and the catalogue data file.
.DESCRIPTION
    Saves are copied byte for byte and checked by SHA1 against their source; nothing is ever written to a source.
    The catalogue data file is the Catalogue reader's output when given (-Catalogue), else the test kit's
    hand-written catalogue.
.PARAMETER FixturesDir
    The folder of live-run logs and data (bridge\, saving\, bigbase\, catalogue\); default: the nearest LatticeFixtures
    folder above the repo, none if there is none. Saves are never read from it.
.PARAMETER Saves
    The library worlds to copy (default: lattice-gen-big and lattice-known). Each save is copied from
    <WorldDir>\<name>\<name>.save, or <WorldDir>\<name>\<name>\<name>.save, as generated\<name>.save, with
    <WorldDir>\<name>\world.txt as generated\<name>.world.txt.
.PARAMETER WorldDir
    The library of test worlds (default: $env:STATIONEERS_WORLDS).
.PARAMETER Catalogue
    A catalogue data file written by the Catalogue reader; copied as catalogue.lcat when given.
#>
param(
    [string] $FixturesDir = '',
    [string[]] $Saves = @('lattice-gen-big', 'lattice-known'),
    [string] $WorldDir = $env:STATIONEERS_WORLDS,
    [string] $Catalogue = ''
)

$ErrorActionPreference = 'Stop'
foreach ($save in $Saves) {
    if ($save -match '(?i)vulcan\s*2_?4') {
        [Console]::Error.WriteLine("make-fixtures: refused '$save': test worlds come from the world library, not a player's save.")
        exit 1
    }
}
if (-not $FixturesDir) {
    for ($dir = Get-Item $PSScriptRoot; $dir; $dir = $dir.Parent) {
        $candidate = Join-Path $dir.FullName 'LatticeFixtures'
        if (Test-Path $candidate) { $FixturesDir = $candidate; break }
    }
}
$generated = Join-Path $PSScriptRoot 'generated'
New-Item -ItemType Directory -Force -Path $generated | Out-Null

function Copy-Checked([string] $source, [string] $target) {
    Copy-Item -LiteralPath $source -Destination $target -Force
    $want = (Get-FileHash -Algorithm SHA1 -LiteralPath $source).Hash
    $got = (Get-FileHash -Algorithm SHA1 -LiteralPath $target).Hash
    if ($want -ne $got) { throw "Copy of $(Split-Path $source -Leaf) differs from its source ($got, expected $want)" }
    $got
}

# Library layout: <WorldDir>\<name>\ holds README.md, md5.txt, world.txt and either <name>.save beside them (flat) or the
# save folder <name>\ holding <name>.save (nested, as lattice-gen-big).
$copied = @()
foreach ($save in $Saves) {
    if (-not $WorldDir -or -not (Test-Path -LiteralPath $WorldDir -PathType Container)) { throw 'no world library: pass -WorldDir or set STATIONEERS_WORLDS' }
    $world = Join-Path $WorldDir $save
    $source = Join-Path $world "$save.save"
    if (-not (Test-Path $source)) { $source = Join-Path $world "$save\$save.save" }
    if (-not (Test-Path $source)) { throw "No world $save in $WorldDir" }
    $worldFile = Join-Path $world 'world.txt'
    if (-not (Test-Path $worldFile)) { throw "World $save has no world.txt in $world" }
    $null = Copy-Checked $worldFile (Join-Path $generated "$save.world.txt")
    Write-Host "world     $save.world.txt"
    $copied += $save
    Write-Host "save      $save.save  SHA1 $(Copy-Checked $source (Join-Path $generated "$save.save"))"
}

# The world bridge's reads of the known world (written by Lattice.Game.Live on the test instance), when this machine has
# them: LatticeFixtures\bridge\world-dump-*.txt and the live run logs LatticeFixtures\bridge\live-bridge-*.log.
$bridgeDir = if ($FixturesDir) { Join-Path $FixturesDir 'bridge' } else { '' }
if ($bridgeDir -and (Test-Path $bridgeDir) -and ($copied -contains 'lattice-known')) {
    $bridgeFiles = @(Get-ChildItem -LiteralPath $bridgeDir -Filter 'world-dump-*.txt')
    $bridgeFiles += @(Get-ChildItem -LiteralPath $bridgeDir -Filter 'live-bridge-*.log')
    foreach ($file in $bridgeFiles) {
        $got = Copy-Checked $file.FullName (Join-Path $generated $file.Name)
        Write-Host "bridge    $($file.Name)  SHA1 $got"
    }
}
# The saving module's live runs on the test instance (Lattice.Game.Live), when this machine has them:
# LatticeFixtures\saving\saving-*.log.
$savingDir = if ($FixturesDir) { Join-Path $FixturesDir 'saving' } else { '' }
if ($savingDir -and (Test-Path $savingDir)) {
    foreach ($file in Get-ChildItem -LiteralPath $savingDir -Filter 'saving-*.log') {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $generated $file.Name) -Force
        Write-Host "saving    $($file.Name)"
    }
}

# The generated big base and its mix (tests/Lattice.BigBase writes them), when this machine has them:
# LatticeFixtures\bigbase\lattice-gen-big.tsv and lattice-gen-big-mix.txt.
$bigbaseDir = if ($FixturesDir) { Join-Path $FixturesDir 'bigbase' } else { '' }
foreach ($name in 'lattice-gen-big.tsv', 'lattice-gen-big-mix.txt') {
    $source = if ($bigbaseDir) { Join-Path $bigbaseDir $name } else { '' }
    if ($source -and (Test-Path $source)) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $generated $name) -Force
        Write-Host "bigbase   $name"
    }
}

# The base game's own catalogue (exported with no mod loaded), the source of truth for what a generated base may name.
$vanillaSource = if ($FixturesDir) { Join-Path $FixturesDir 'catalogue\catalogue-headless-vanilla.lcat' } else { '' }
if ($vanillaSource -and (Test-Path $vanillaSource)) {
    Copy-Item -LiteralPath $vanillaSource -Destination (Join-Path $generated 'catalogue-vanilla.lcat') -Force
    Write-Host "vanilla   $vanillaSource -> catalogue-vanilla.lcat"
}

$catalogueTarget = Join-Path $generated 'catalogue.lcat'
$catalogueSource = if ($Catalogue) { $Catalogue } else { Join-Path $PSScriptRoot '..\Lattice.TestKit\Catalogue\hand-catalogue.lcat' }
Copy-Item -LiteralPath $catalogueSource -Destination $catalogueTarget -Force
Write-Host "catalogue $catalogueSource -> catalogue.lcat"
exit 0
