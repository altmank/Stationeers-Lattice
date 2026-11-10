<#
.SYNOPSIS
    Tests build.ps1's deploy backup (-BackupRoot, -NoBackup) against scratch folders only. Exits non-zero on any failure.
    build.ps1 -DeployOnly refuses unless the package's Lattice.Api.dll carries the HEAD commit, so the test copies
    build.ps1 into a scratch git repo and builds a tiny stand-in Lattice.Api.dll stamped with that repo's HEAD. The
    real build.ps1 logic is unchanged; only the repo it runs in is a scratch one.
#>
$ErrorActionPreference = 'Stop'
$buildScript = Join-Path $PSScriptRoot '..\..\build.ps1' | Resolve-Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) "lattice-deploy-backup-$PID"
$failures = 0
function Check([string] $name, [bool] $ok) {
    if ($ok) { Write-Host "PASS $name" } else { Write-Host "FAIL $name" -ForegroundColor Red; $script:failures++ }
}
function Tree-Hash([string] $dir) {
    (Get-ChildItem $dir -Recurse -File | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($dir.Length) + ':' + (Get-FileHash $_.FullName).Hash }) -join '|'
}
try {
    New-Item -ItemType Directory $scratch | Out-Null
    $repo = Join-Path $scratch 'repo'
    New-Item -ItemType Directory $repo | Out-Null
    Copy-Item $buildScript (Join-Path $repo 'build.ps1')
    git -C $repo init -q
    git -C $repo -c user.name=t -c user.email=test@users.noreply.github.com commit -q --allow-empty -m t
    $head = (git -C $repo rev-parse HEAD).Trim()

    # Stand-in package: About.xml plus an assembly whose product version contains HEAD.
    $proj = Join-Path $scratch 'api'
    New-Item -ItemType Directory $proj | Out-Null
    Set-Content (Join-Path $proj 'Lattice.Api.csproj') @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.0</TargetFramework>
<InformationalVersion>1.0+$head</InformationalVersion><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>
"@
    $out = dotnet build $proj -c Release -o (Join-Path $proj 'out') 2>&1
    if ($LASTEXITCODE -ne 0) { $out | Write-Host; throw 'stand-in assembly build failed' }
    $pkg = Join-Path $repo 'artifacts\Release\Lattice'
    New-Item -ItemType Directory (Join-Path $pkg 'About') -Force | Out-Null
    Set-Content (Join-Path $pkg 'About\About.xml') '<ModMetadata><Version>9.9.9</Version></ModMetadata>'
    Copy-Item (Join-Path $proj 'out\Lattice.Api.dll') $pkg
    $pkgHash = Tree-Hash $pkg

    function Run-Deploy([string[]] $extra) {
        $o = & pwsh -NoProfile -File (Join-Path $repo 'build.ps1') -DeployOnly -NoLock @extra 2>&1 | Out-String
        [pscustomobject]@{ Code = $LASTEXITCODE; Out = $o }
    }
    function New-Old([string] $target, [string] $version) {
        New-Item -ItemType Directory (Join-Path $target 'About') -Force | Out-Null
        if ($version) { Set-Content (Join-Path $target 'About\About.xml') "<ModMetadata><Version>$version</Version></ModMetadata>" }
        Set-Content (Join-Path $target 'old.dll') "old $version"
    }

    # 1. existing target is moved byte for byte, new package installed, path printed
    $t = Join-Path $scratch 't1\Lattice'; $b = Join-Path $scratch 'b1'
    New-Item -ItemType Directory $b | Out-Null; New-Old $t '0.0.1'; $oldHash = Tree-Hash $t
    $r = Run-Deploy @('-Target', $t, '-BackupRoot', $b)
    $kept = Get-ChildItem (Join-Path $b 'Lattice') -Directory -ErrorAction SilentlyContinue
    Check '1 exit 0' ($r.Code -eq 0)
    Check '1 one backup named 0.0.1-<stamp>' (@($kept).Count -eq 1 -and $kept.Name -match '^0\.0\.1-\d{8}-\d{6}$')
    Check '1 backup identical to old target' ($kept -and (Tree-Hash $kept.FullName) -eq $oldHash)
    Check '1 target holds new package' ((Tree-Hash $t) -eq $pkgHash)
    Check '1 path printed' ($kept -and $r.Out.Contains("== kept 0.0.1 at $($kept.FullName)"))

    # 2. no target: deploys, no backup folder made
    $t = Join-Path $scratch 't2\Lattice'; $b = Join-Path $scratch 'b2'
    New-Item -ItemType Directory $b | Out-Null
    $r = Run-Deploy @('-Target', $t, '-BackupRoot', $b)
    Check '2 deployed' ($r.Code -eq 0 -and (Tree-Hash $t) -eq $pkgHash)
    Check '2 no backup folder' (-not (Test-Path (Join-Path $b 'Lattice')))

    # 3. target exists, no backup root, no -NoBackup: refused, unchanged (also a nonexistent root)
    foreach ($root in @('', (Join-Path $scratch 'nope'))) {
        $t = Join-Path $scratch 't3\Lattice'; Remove-Item (Split-Path $t) -Recurse -Force -ErrorAction SilentlyContinue
        New-Old $t '0.0.1'; $oldHash = Tree-Hash $t
        $saved = $env:STATIONEERS_MOD_BACKUPS; $env:STATIONEERS_MOD_BACKUPS = $null
        try { $r = Run-Deploy @('-Target', $t, '-BackupRoot', $root) } finally { $env:STATIONEERS_MOD_BACKUPS = $saved }
        Check "3 refused (root '$root')" ($r.Code -ne 0 -and $r.Out.Contains('pass -BackupRoot or set STATIONEERS_MOD_BACKUPS, or -NoBackup for a test install'))
        Check "3 target unchanged (root '$root')" ((Tree-Hash $t) -eq $oldHash)
    }

    # 4. -NoBackup: replaced, no backup
    $t = Join-Path $scratch 't4\Lattice'; $b = Join-Path $scratch 'b4'
    New-Item -ItemType Directory $b | Out-Null; New-Old $t '0.0.1'
    $r = Run-Deploy @('-Target', $t, '-BackupRoot', $b, '-NoBackup')
    Check '4 replaced' ($r.Code -eq 0 -and (Tree-Hash $t) -eq $pkgHash)
    Check '4 no backup' (-not (Test-Path (Join-Path $b 'Lattice')))

    # 5. move fails (a file in the target held open): refused, unchanged, nothing copied
    $t = Join-Path $scratch 't5\Lattice'; $b = Join-Path $scratch 'b5'
    New-Item -ItemType Directory $b | Out-Null; New-Old $t '0.0.1'; $oldHash = Tree-Hash $t
    $hold = [IO.File]::Open((Join-Path $t 'old.dll'), 'Open', 'Read', 'None')
    try { $r = Run-Deploy @('-Target', $t, '-BackupRoot', $b) } finally { $hold.Dispose() }
    Check '5 refused non-zero' ($r.Code -ne 0)
    Check '5 target unchanged, nothing copied' ((Tree-Hash $t) -eq $oldHash)

    # 6. no About.xml: unknown-<stamp>
    $t = Join-Path $scratch 't6\Lattice'; $b = Join-Path $scratch 'b6'
    New-Item -ItemType Directory $b | Out-Null; New-Old $t ''
    $r = Run-Deploy @('-Target', $t, '-BackupRoot', $b)
    $kept = Get-ChildItem (Join-Path $b 'Lattice') -Directory -ErrorAction SilentlyContinue
    Check '6 backed up as unknown-<stamp>' ($r.Code -eq 0 -and @($kept).Count -eq 1 -and $kept.Name -match '^unknown-\d{8}-\d{6}$')
}
finally {
    Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
if ($failures) { Write-Host "$failures check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host 'all deploy-backup checks passed'
exit 0
