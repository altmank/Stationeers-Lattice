<#
.SYNOPSIS
    Restores, builds and tests the Lattice solution. Exits non-zero on any failure.
    The default run leaves out the tests with the trait Category=Perf: they assert wall-clock time (frame budgets,
    slice steps, rules on real-clock threads), and load on the machine fails them without a fault in the code.
.PARAMETER Configuration
    Debug or Release (default Release).
.PARAMETER Perf
    Run only the Category=Perf tests, on demand, on an otherwise idle machine: ./build.ps1 -Perf
    The test run has .NET tiered compilation off: the game's Mono compiles each method once, optimised, so .NET's
    unoptimised first tier and its later recompiles are not what the game runs.
.PARAMETER Deploy
    After a green run, copy artifacts/<Configuration>/Lattice into the local mods folder
    (Documents/My Games/Stationeers/mods/Lattice). Refuses while Stationeers runs.
.PARAMETER Force
    Deploy even while Stationeers runs (only for a mod the running game has never loaded).
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $Perf,
    [switch] $Deploy,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot 'Lattice.sln'

# A fresh checkout (the deploy queue builds in a temporary worktree) has no Directory.Build.props.user: fall back to
# the usual Steam folder when STATIONEERS_DIR is unset and the game is there.
if (-not (Test-Path (Join-Path $PSScriptRoot 'Directory.Build.props.user')) -and -not $env:STATIONEERS_DIR) {
    $steam = 'C:\Program Files (x86)\Steam\steamapps\common\Stationeers'
    if (Test-Path (Join-Path $steam 'rocketstation_Data\Managed\Assembly-CSharp.dll')) { $env:STATIONEERS_DIR = $steam }
}

function Invoke-Step([string] $name, [scriptblock] $command) {
    Write-Host "== $name" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) {
        Write-Host "== $name failed (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Invoke-Step 'restore' { dotnet restore $solution }
Invoke-Step 'build'   { dotnet build $solution --no-restore -c $Configuration }
$filter = if ($Perf) { 'Category=Perf' } else { 'Category!=Perf' }
$tiered = $env:DOTNET_TieredCompilation
if ($Perf) { $env:DOTNET_TieredCompilation = '0' }
try {
    Invoke-Step 'test'    { dotnet test $solution --no-build -c $Configuration --filter $filter }
}
finally {
    $env:DOTNET_TieredCompilation = $tiered
}

Write-Host '== green' -ForegroundColor Green

if ($Deploy) {
    # A running game holds the old dlls and keeps running them; someone may be playing.
    if (-not $Force -and (Get-Process -Name 'rocketstation' -ErrorAction SilentlyContinue)) {
        Write-Host '== deploy refused: Stationeers is running (close it, or -Force for a mod it has never loaded)' -ForegroundColor Red
        exit 1
    }
    $package = Join-Path $PSScriptRoot "artifacts\$Configuration\Lattice"
    if (-not (Test-Path (Join-Path $package 'About\About.xml'))) {
        Write-Host "== deploy failed: no package at $package" -ForegroundColor Red
        exit 1
    }
    $mods = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Stationeers\mods\Lattice'
    if (Test-Path $mods) { Remove-Item $mods -Recurse -Force }
    New-Item -ItemType Directory -Path $mods -Force | Out-Null
    Copy-Item (Join-Path $package '*') $mods -Recurse
    Write-Host "== deployed to $mods" -ForegroundColor Green
}
exit 0
