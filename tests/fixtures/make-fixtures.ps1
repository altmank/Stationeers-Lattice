<#
.SYNOPSIS
    Fills tests/fixtures/generated/ (git-ignored): verbatim copies of the named saves and the catalogue data file.
.DESCRIPTION
    Saves are copied byte for byte and checked by SHA1 against their source; nothing is ever written to a source.
    The catalogue data file is the Catalogue reader's output when given (-Catalogue), else the test kit's
    hand-written catalogue.
.PARAMETER SaveDir
    Where the saves are read from (default: the nearest LatticeFixtures\saves in a folder above the repo).
.PARAMETER Saves
    The save files to copy (default: Vulcan2_4.save).
.PARAMETER Catalogue
    A catalogue data file written by the Catalogue reader; copied as catalogue.lcat when given.
#>
param(
    [string] $SaveDir = '',
    [string[]] $Saves = @('Vulcan2_4.save'),
    [string] $Catalogue = ''
)

$ErrorActionPreference = 'Stop'
if (-not $SaveDir) {
    for ($dir = Get-Item $PSScriptRoot; $dir; $dir = $dir.Parent) {
        $candidate = Join-Path $dir.FullName 'LatticeFixtures\saves'
        if (Test-Path $candidate) { $SaveDir = $candidate; break }
    }
    if (-not $SaveDir) { throw 'No LatticeFixtures\saves above the repo; pass -SaveDir.' }
}
$generated = Join-Path $PSScriptRoot 'generated'
New-Item -ItemType Directory -Force -Path $generated | Out-Null

foreach ($save in $Saves) {
    $source = Join-Path $SaveDir $save
    if (-not (Test-Path $source)) { throw "No save at $source" }
    $target = Join-Path $generated $save
    Copy-Item -LiteralPath $source -Destination $target -Force
    $want = (Get-FileHash -Algorithm SHA1 -LiteralPath $source).Hash
    $got = (Get-FileHash -Algorithm SHA1 -LiteralPath $target).Hash
    if ($want -ne $got) { throw "Copy of $save differs from its source ($got, expected $want)" }
    Write-Host "save      $save  SHA1 $got"
}

# The world bridge's known save and the bridge's reads of it (written by Lattice.Game.Live on the test instance), when
# this machine has them: LatticeFixtures\saves\lattice-known.save, LatticeFixtures\bridge\world-dump-*.txt and the live
# run logs LatticeFixtures\bridge\live-bridge-*.log.
$bridgeDir = Join-Path (Split-Path $SaveDir -Parent) 'bridge'
$known = Join-Path $SaveDir 'lattice-known.save'
if ((Test-Path $known) -and ($Saves -notcontains 'lattice-known.save')) {
    $bridgeFiles = @(Get-Item -LiteralPath $known)
    if (Test-Path $bridgeDir) {
        $bridgeFiles += @(Get-ChildItem -LiteralPath $bridgeDir -Filter 'world-dump-*.txt')
        $bridgeFiles += @(Get-ChildItem -LiteralPath $bridgeDir -Filter 'live-bridge-*.log')
    }
    foreach ($file in $bridgeFiles) {
        $target = Join-Path $generated $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        $want = (Get-FileHash -Algorithm SHA1 -LiteralPath $file.FullName).Hash
        $got = (Get-FileHash -Algorithm SHA1 -LiteralPath $target).Hash
        if ($want -ne $got) { throw "Copy of $($file.Name) differs from its source ($got, expected $want)" }
        Write-Host "bridge    $($file.Name)  SHA1 $got"
    }
}

# The saving module's live runs on the test instance (Lattice.Game.Live), when this machine has them:
# LatticeFixtures\saving\saving-*.log.
$savingDir = Join-Path (Split-Path $SaveDir -Parent) 'saving'
if (Test-Path $savingDir) {
    foreach ($file in Get-ChildItem -LiteralPath $savingDir -Filter 'saving-*.log') {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $generated $file.Name) -Force
        Write-Host "saving    $($file.Name)"
    }
}

$catalogueTarget = Join-Path $generated 'catalogue.lcat'
$catalogueSource = if ($Catalogue) { $Catalogue } else { Join-Path $PSScriptRoot '..\Lattice.TestKit\Catalogue\hand-catalogue.lcat' }
Copy-Item -LiteralPath $catalogueSource -Destination $catalogueTarget -Force
Write-Host "catalogue $catalogueSource -> catalogue.lcat"
exit 0
