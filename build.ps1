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
.PARAMETER DeployOnly
    Copy the already-built artifacts/<Configuration>/Lattice into the mods folder and do nothing else: no restore,
    build or test. Refuses unless the package's Lattice.Api.dll was built from the commit at HEAD (run build.ps1
    first); -Force does not skip that check. Same running-game refusal and target as -Deploy.
.PARAMETER Target
    The folder the package is copied into (default Documents/My Games/Stationeers/mods/Lattice). Its contents are
    replaced.
.PARAMETER Force
    Deploy even while Stationeers runs (only for a mod the running game has never loaded).
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $Perf,
    [switch] $Deploy,
    [switch] $DeployOnly,
    [string] $Target = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Stationeers\mods\Lattice'),
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

$package = Join-Path $PSScriptRoot "artifacts\$Configuration\Lattice"

function Invoke-Deploy {
    # A running game holds the old dlls and keeps running them; someone may be playing.
    if (-not $Force -and (Get-Process -Name 'rocketstation' -ErrorAction SilentlyContinue)) {
        Write-Host '== deploy refused: Stationeers is running (close it, or -Force for a mod it has never loaded)' -ForegroundColor Red
        exit 1
    }
    if (-not (Test-Path (Join-Path $package 'About\About.xml'))) {
        Write-Host "== deploy failed: no package at $package" -ForegroundColor Red
        exit 1
    }
    if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
    New-Item -ItemType Directory -Path $Target -Force | Out-Null
    Copy-Item (Join-Path $package '*') $Target -Recurse
    Write-Host "== deployed to $Target" -ForegroundColor Green
}

function Assert-PackageCurrent {
    $head = (git -C $PSScriptRoot rev-parse HEAD).Trim()
    $api = Get-ChildItem $package -Recurse -Filter 'Lattice.Api.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
    $built = if ($api) { $api.VersionInfo.ProductVersion } else { 'nowhere (no Lattice.Api.dll)' }
    if (-not $api -or -not $built.Contains($head)) {
        Write-Host "== deploy refused: package is from $built, HEAD is $head`: run build.ps1 first" -ForegroundColor Red
        exit 1
    }
}

if ($DeployOnly) {
    Assert-PackageCurrent
    Invoke-Deploy
    exit 0
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

if ($Deploy) { Invoke-Deploy }
exit 0
