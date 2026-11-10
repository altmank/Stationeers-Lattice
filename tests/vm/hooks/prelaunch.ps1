# vm-client-run -PreLaunch hook for tests\vm\vm-run.psd1's runs (dot-sourced inside the run: $r, $stage, $gOut, $GameArgs, Invoke-Rig).
# Puts what Lattice's live checks read from -lattice-out into the guest: the shots folder and the shader bundle
# (render-timing loads <out>\lattice.shaders), and records the staged builds in result.json.
# The bigbase host file in -GameArgs: returns @{ Path; Rest } (Rest is the arguments without it) or throws the reason.
function Get-BigBaseHostArg([string] $GameArgs) {
    if ($GameArgs -notmatch '\s-lattice-bigbase-host (?:"([^"]*)"|(\S+))') { throw 'no -lattice-bigbase-host <tsv> in -GameArgs' }
    $path = if ($null -ne $Matches[1]) { $Matches[1] } else { $Matches[2] }
    # A Param that was not given is empty, or still the literal {bigbase} token.
    if (-not $path -or $path -eq '{bigbase}') { throw 'no base file: pass -Param bigbase=<host tsv path>' }
    @{ Path = $path; Rest = ($GameArgs -replace '\s-lattice-bigbase-host (?:"[^"]*"|\S+)', '').Trim() }
}

if ($PreLaunchFunctionsOnly) { return }
$latticeMode = if ($GameArgs -match '-lattice-live (\S+)') { $Matches[1] } else { Stop-Rig infra 'PreLaunch: no -lattice-live <mode> in -GameArgs' }
$r.Info.mode = $latticeMode
$r.Info.rigvm = Split-Path $RigRepo -Leaf
foreach ($p in "$stage\mods\Lattice\Bundle\lattice.shaders", "$stage\mods\Lattice\Lattice.Api.dll",
    "$stage\mods\LatticeLiveChecks\Lattice.Game.Live.dll", "$stage\mods\LatticeLiveChecks\LatticeLiveChecks.dll") {
    if (-not (Test-Path $p)) { Stop-Rig infra "PreLaunch: not staged: $p" }
}
$r.Info.latticeApiMd5 = (Get-FileHash "$stage\mods\Lattice\Lattice.Api.dll" -Algorithm MD5).Hash
$r.Info.liveDll = (Get-FileHash "$stage\mods\LatticeLiveChecks\Lattice.Game.Live.dll" -Algorithm MD5).Hash
$r.Info.liveChecksDll = (Get-FileHash "$stage\mods\LatticeLiveChecks\LatticeLiveChecks.dll" -Algorithm MD5).Hash
Invoke-Rig -ArgumentList $gOut { param($o) New-Item -ItemType Directory -Force "$o\shots" | Out-Null }
Copy-Item -ToSession (Get-RigSession) -Path "$stage\mods\Lattice\Bundle\lattice.shaders" -Destination "$gOut\lattice.shaders" -Force
if ($latticeMode -like 'bigbase-*') {
    # The base file goes in beside the run's output; the host path is not a guest argument, so it leaves $GameArgs
    # (vm-client-run builds the game's command line after this hook; result.json keeps the original in args).
    $arg = try { Get-BigBaseHostArg $GameArgs } catch { Stop-Rig infra "PreLaunch: $($_.Exception.Message)" }
    $hostTsv = $arg.Path
    $GameArgs = $arg.Rest
    if (-not (Test-Path -LiteralPath $hostTsv)) { Stop-Rig infra "PreLaunch: no base file $hostTsv" }
    $r.Info.bigbaseMd5 = (Get-FileHash -LiteralPath $hostTsv -Algorithm MD5).Hash
    Copy-Item -ToSession (Get-RigSession) -LiteralPath $hostTsv -Destination "$gOut\bigbase.tsv" -Force
    Rig-Log "lattice: base file $hostTsv md5 $($r.Info.bigbaseMd5)"
}
Rig-Log "lattice: mode $latticeMode; Lattice.Api $($r.Info.latticeApiMd5); live $($r.Info.liveDll); checks $($r.Info.liveChecksDll); rig-lib $($r.Info.rigvm)"
