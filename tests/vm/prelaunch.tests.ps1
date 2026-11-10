# Tests for prelaunch.ps1's Get-BigBaseHostArg: the host base file named in -GameArgs.
# Run: powershell -NoProfile -File prelaunch.tests.ps1   Exit 0 when every case holds, 1 otherwise.
$ErrorActionPreference = 'Stop'
$PreLaunchFunctionsOnly = $true
. "$PSScriptRoot\hooks\prelaunch.ps1"

$cases = @(
    @{ Name = 'quoted path'; Args = '-lattice-live bigbase-load -lattice-bigbase-host "C:\b\x.tsv" -lattice-save s'; Path = 'C:\b\x.tsv'; Rest = '-lattice-live bigbase-load -lattice-save s' }
    @{ Name = 'bare path'; Args = '-lattice-live bigbase-load -lattice-bigbase-host C:\b\x.tsv -lattice-save s'; Path = 'C:\b\x.tsv'; Rest = '-lattice-live bigbase-load -lattice-save s' }
    @{ Name = 'path last'; Args = '-lattice-live bigbase-build -lattice-bigbase-host "C:\b\x.tsv"'; Path = 'C:\b\x.tsv'; Rest = '-lattice-live bigbase-build' }
    @{ Name = 'empty Param'; Args = '-lattice-live bigbase-load -lattice-bigbase-host ""'; Throws = 'no base file: pass -Param bigbase=<host tsv path>' }
    @{ Name = 'Param token left in'; Args = '-lattice-live bigbase-load -lattice-bigbase-host "{bigbase}"'; Throws = 'no base file: pass -Param bigbase=<host tsv path>' }
    @{ Name = 'no argument'; Args = '-lattice-live bigbase-load -lattice-held 5000'; Throws = 'no -lattice-bigbase-host <tsv> in -GameArgs' }
)

$bad = 0
foreach ($c in $cases) {
    $got = $null; $err = $null
    try { $got = Get-BigBaseHostArg $c.Args } catch { $err = $_.Exception.Message }
    $ok = if ($c.Throws) { $err -eq $c.Throws } else { -not $err -and $got.Path -eq $c.Path -and $got.Rest -eq $c.Rest }
    if ($ok) { Write-Host "ok   $($c.Name)" }
    else { $bad++; Write-Host "FAIL $($c.Name): got path [$($got.Path)] rest [$($got.Rest)] error [$err]" }
}
Write-Host "$($cases.Count - $bad) of $($cases.Count) cases hold"
exit $(if ($bad) { 1 } else { 0 })