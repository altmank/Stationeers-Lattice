<#
.SYNOPSIS
    Builds Lattice's shaders into the asset bundle the mod ships: src/Lattice.Render/Bundle/lattice.shaders, plus
    lattice.shaders.sha, three lines: 'sources <hash of the sources it was built from>', 'unity <editor version>' and
    'target StandaloneWindows64' (tests fail when the sources change and the bundle is not rebuilt, or the editor is
    not the game's Unity version). Needs Unity 2022.3.62f3 (the game's Unity version) with an activated licence, and
    refuses any other editor: a bundle from another Unity (Unity 6 included) does not load in the game's 2022.3 player.
.PARAMETER Unity
    The editor executable (default: the LATTICE_UNITY environment variable).
.PARAMETER Project
    The Unity project the sources are copied into; made on first use (default: the
    LATTICE_UNITY_PROJECT environment variable).
.PARAMETER HashOnly
    Only rewrite the sources line of lattice.shaders.sha (no editor run); for checking the hash rule, never to skip a
    rebuild.
#>
param(
    [string] $Unity = $env:LATTICE_UNITY,
    [string] $Project = $env:LATTICE_UNITY_PROJECT,
    [switch] $HashOnly
)

$ErrorActionPreference = 'Stop'
$sources = $PSScriptRoot
$bundleDir = Join-Path (Split-Path $sources -Parent) 'Bundle'

# The sources hash: SHA-256 over every .shader, .cginc and Editor .cs file, ordered by its path relative to this
# folder ('/' separators, ordinal), each as "<path>\n<text with LF line ends>\n" in UTF-8. ShaderSourcesTests computes
# the same.
function Get-SourcesHash {
    $files = Get-ChildItem -LiteralPath $sources -Recurse -File |
        Where-Object { $_.Extension -in '.shader', '.cginc', '.cs' } |
        ForEach-Object { [pscustomobject]@{ Rel = $_.FullName.Substring($sources.Length + 1).Replace('\', '/'); Full = $_.FullName } }
    $sorted = [System.Collections.Generic.List[object]]::new()
    $sorted.AddRange([object[]]@($files))
    $sorted.Sort([System.Comparison[object]] { param($a, $b) [string]::CompareOrdinal($a.Rel, $b.Rel) })
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $stream = [System.IO.MemoryStream]::new()
    foreach ($f in $sorted) {
        $text = [System.IO.File]::ReadAllText($f.Full, $utf8).Replace("`r`n", "`n")
        $bytes = $utf8.GetBytes($f.Rel + "`n" + $text + "`n")
        $stream.Write($bytes, 0, $bytes.Length)
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    -join ($sha.ComputeHash($stream.ToArray()) | ForEach-Object { $_.ToString('x2') })
}

function Write-Manifest([string] $hash, [string] $unityVersion) {
    $text = "sources $hash`nunity $unityVersion`ntarget StandaloneWindows64`n"
    [System.IO.File]::WriteAllText((Join-Path $bundleDir 'lattice.shaders.sha'), $text, [System.Text.Encoding]::ASCII)
}

New-Item -ItemType Directory -Force $bundleDir | Out-Null
$hash = Get-SourcesHash
if ($HashOnly) {
    $manifest = Join-Path $bundleDir 'lattice.shaders.sha'
    $version = 'none'
    if (Test-Path $manifest) {
        $line = Get-Content -LiteralPath $manifest | Where-Object { $_ -like 'unity *' } | Select-Object -First 1
        if ($line) { $version = $line.Substring(6) }
    }
    Write-Manifest $hash $version
    Write-Host "== sources hash $hash"
    exit 0
}

if (-not $Unity -or -not $Project) { Write-Host '== set LATTICE_UNITY and LATTICE_UNITY_PROJECT, or pass -Unity and -Project' -ForegroundColor Red; exit 1 }
if (-not (Test-Path $Unity)) { Write-Host "== no Unity editor at $Unity" -ForegroundColor Red; exit 1 }
$GameUnity = '2022.3.62f3'
$editorVersion = ((Get-Item -LiteralPath $Unity).VersionInfo.ProductVersion -split '_')[0]
if ($editorVersion -ne $GameUnity) {
    Write-Host "== refused: $Unity is Unity $editorVersion; the game runs Unity $GameUnity and only a bundle from that editor loads in it" -ForegroundColor Red
    exit 1
}
$logs = Join-Path (Split-Path $Project -Parent) 'logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if (-not (Test-Path (Join-Path $Project 'ProjectSettings'))) {
    Write-Host "== creating the Unity project $Project"
    & $Unity -batchmode -nographics -quit -createProject $Project -logFile (Join-Path $logs "create-$stamp.log") | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "== project creation failed (exit $LASTEXITCODE), see $logs" -ForegroundColor Red; exit 1 }
}

# The project's Lattice folder mirrors the sources exactly: shaders and include under Shaders, the build script under Editor.
$target = Join-Path $Project 'Assets\Lattice'
foreach ($sub in 'Shaders', 'Editor') {
    $dir = Join-Path $target $sub
    if (Test-Path $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    New-Item -ItemType Directory -Force $dir | Out-Null
}
Get-ChildItem -LiteralPath $sources -File | Where-Object { $_.Extension -in '.shader', '.cginc' } |
    Copy-Item -Destination (Join-Path $target 'Shaders')
Copy-Item (Join-Path $sources 'Editor\*.cs') (Join-Path $target 'Editor')

$out = Join-Path $Project 'BundleOut'
$log = Join-Path $logs "build-$stamp.log"
Write-Host "== building (log $log)"
& $Unity -batchmode -nographics -quit -projectPath $Project -executeMethod LatticeBundleBuild.Build -latticeOut $out -logFile $log | Out-Null
$exit = $LASTEXITCODE
$built = Join-Path $out 'lattice.shaders'
if ($exit -ne 0 -or -not (Select-String -LiteralPath $log -Pattern 'LATTICE BUNDLE BUILT' -Quiet)) {
    Select-String -LiteralPath $log -Pattern 'LATTICE BUNDLE FAILED|Shader error|error CS|licen' | Select-Object -First 20 | ForEach-Object { Write-Host $_.Line }
    Write-Host "== build failed (exit $exit), see $log" -ForegroundColor Red
    exit 1
}

Copy-Item -LiteralPath $built -Destination (Join-Path $bundleDir 'lattice.shaders') -Force
$unityVersion = (Get-Content -LiteralPath (Join-Path $out 'lattice.shaders.unity') -Raw).Trim()
if ($unityVersion -ne $GameUnity) { Write-Host "== refused: the bundle was built by Unity $unityVersion, not $GameUnity" -ForegroundColor Red; exit 1 }
Write-Manifest $hash $unityVersion
Write-Host "== built $(Join-Path $bundleDir 'lattice.shaders') from sources $hash" -ForegroundColor Green
exit 0
