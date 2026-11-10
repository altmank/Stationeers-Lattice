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
    Deploy even while Stationeers runs (only for a mod the running game has never loaded). It skips no lock check.
.PARAMETER LockScript
    The script that guards the game folder while a deploy copies (default: the STATIONEERS_GAME_LOCK environment
    variable). The deploy calls `& <script> acquire -Owner <owner>` before it replaces the target and
    `& <script> release -Owner <owner>` afterwards, even when the copy fails; each must exit 0 on success. acquire may
    wait while someone else holds the lock; if it fails, the deploy stops without touching the target. A deploy with no
    lock script, or one naming a file that does not exist, refuses unless -NoLock or -LockHeld is given.
.PARAMETER BackupRoot
    Optional: a folder that keeps the version a deploy replaces. Given, the deploy moves an existing target (inside the
    lock window) to <BackupRoot>\Lattice\<old version>-<yyyyMMdd-HHmmss>, reading the version from the target's
    About\About.xml (`unknown` if there is none), and prints where; if the move fails it stops and copies nothing. A
    BackupRoot that is not an existing folder is refused before any lock. Not given, the target is replaced; every
    deployed version is a commit that can be rebuilt.
.PARAMETER NoLock
    Deploy without any lock (for a machine where nothing else uses the game folder).
.PARAMETER LockHeld
    The caller already holds the game lock for its whole run: the deploy neither takes nor releases it (the lock is
    not re-entrant). No -LockScript is needed.
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $Perf,
    [switch] $Deploy,
    [switch] $DeployOnly,
    [string] $Target = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Stationeers\mods\Lattice'),
    [switch] $Force,
    [string] $LockScript = $env:STATIONEERS_GAME_LOCK,
    [string] $BackupRoot = '',
    [switch] $NoLock,
    [switch] $LockHeld
)

$ErrorActionPreference = 'Stop'
# Idle MSBuild node workers outlive the build and hold its process tree open for whoever waits on it.
$env:MSBUILDDISABLENODEREUSE = '1'
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
$takeLock = -not ($NoLock -or $LockHeld)

# Decided before anything is built or copied, so a deploy with no lock fails fast and leaves the target alone.
if (($Deploy -or $DeployOnly) -and $takeLock -and -not ($LockScript -and (Test-Path $LockScript -PathType Leaf))) {
    $why = if ($LockScript) { "no lock script at $LockScript" } else { 'no game lock' }
    Write-Host "== deploy refused: $why`: pass -LockScript or set STATIONEERS_GAME_LOCK, or -NoLock" -ForegroundColor Red
    exit 1
}

# Also decided before the lock: a backup folder that was asked for must exist.
if (($Deploy -or $DeployOnly) -and $BackupRoot -and -not (Test-Path $BackupRoot -PathType Container)) {
    Write-Host "== deploy refused: no backup folder at $BackupRoot" -ForegroundColor Red
    exit 1
}

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
    $owner = "lattice-deploy $(Split-Path $PSScriptRoot -Leaf) pid $PID"
    if ($takeLock) {
        & $LockScript acquire -Owner $owner | ForEach-Object { Write-Host "   $_" }
        if ($LASTEXITCODE -ne 0) {
            Write-Host "== deploy failed: could not take the game lock (exit $LASTEXITCODE)" -ForegroundColor Red
            exit 1
        }
    }
    try {
        if (Test-Path $Target) {
            if (-not $BackupRoot) {
                Remove-Item $Target -Recurse -Force
            }
            else {
                $old = 'unknown'
                $aboutXml = Join-Path $Target 'About\About.xml'
                if (Test-Path $aboutXml -PathType Leaf) {
                    try {
                        $v = ([xml](Get-Content $aboutXml -Raw)).SelectSingleNode('//Version')
                        if ($v -and $v.InnerText.Trim()) { $old = $v.InnerText.Trim() }
                    }
                    catch { }
                }
                $old = $old -replace '[^\w.+-]', '_'
                $kept = Join-Path $BackupRoot ("Lattice\$old-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
                try {
                    New-Item -ItemType Directory -Path (Split-Path $kept) -Force | Out-Null
                    Move-Item -LiteralPath $Target -Destination $kept -ErrorAction Stop
                }
                catch {
                    Write-Host "== deploy refused: could not keep the old version at $kept`: $($_.Exception.Message)" -ForegroundColor Red
                    exit 1
                }
                Write-Host "== kept $old at $kept"
            }
        }
        New-Item -ItemType Directory -Path $Target -Force | Out-Null
        Copy-Item (Join-Path $package '*') $Target -Recurse
    }
    finally {
        if ($takeLock) { & $LockScript release -Owner $owner | ForEach-Object { Write-Host "   $_" } }
    }
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
Invoke-Step 'build'   { dotnet build $solution --no-restore -c $Configuration -nodeReuse:false }
$filter = if ($Perf) { 'Category=Perf' } else { 'Category!=Perf' }
$tiered = $env:DOTNET_TieredCompilation
if ($Perf) { $env:DOTNET_TieredCompilation = '0' }
try {
    if ($Perf) {
        # The cold pin needs a process in which nothing compiled the code before it: its own step, first.
        Invoke-Step 'test (cold arrival, own process)' { dotnet test $solution --no-build -c $Configuration --filter 'FullyQualifiedName~PlanArrivalTests' }
        $filter = 'Category=Perf&FullyQualifiedName!~PlanArrivalTests'
    }
    Invoke-Step 'test'    { dotnet test $solution --no-build -c $Configuration --filter $filter }
}
finally {
    $env:DOTNET_TieredCompilation = $tiered
}

# The VM run hooks and the publish script run under Windows PowerShell 5.1 inside the kit, so their tests do too.
# They touch no game and no VM, and take a few seconds.
if (-not $Perf) {
    foreach ($t in Get-ChildItem (Join-Path $PSScriptRoot 'tests\vm') -Filter *.tests.ps1) {
        Invoke-Step "script test $($t.Name)" { powershell.exe -NoProfile -ExecutionPolicy Bypass -File $t.FullName }
    }
}

Write-Host '== green' -ForegroundColor Green

if ($Deploy) { Invoke-Deploy }
exit 0
