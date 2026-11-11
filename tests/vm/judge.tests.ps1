#Requires -Version 7.5
# Tests for judge.ps1's bigbase verdict (Get-BigBaseVerdict) against the logs in fixtures\.
# Run: pwsh -NoProfile -File judge.tests.ps1   Exit 0 when every case holds, 1 otherwise.
$ErrorActionPreference = 'Stop'
$JudgeFunctionsOnly = $true
. "$PSScriptRoot\hooks\judge.ps1"
$fx = "$PSScriptRoot\fixtures"

function Read-Fixture([string] $name) {
    $log = @(Get-Content "$fx\$name.log")
    $world = if (Test-Path "$fx\$name.world.txt") { @(Get-Content "$fx\$name.world.txt") } else { $null }
    $log, $world
}

# expect: $null = pass; otherwise a regex the failure text must match.
$cases = @(
    @{ Name = 'build pass'; Fixture = 'pass'; Expect = $null }
    @{ Name = 'build, held 5000 with 1 plan and 5000 ghosts'; Fixture = 'held-plan'; Expect = $null }
    @{ Name = 'build, held 5000 but no plan or ghosts'; Fixture = 'held-no-plan'; Expect = 'held 5000 but the world has 0 plan\(s\) and 0 ghost\(s\)' }
    @{ Name = 'build, refused exactly 1% of to-spawn'; Fixture = 'refused-1pct'; Expect = $null }
    @{ Name = 'build, refused over 1% of to-spawn'; Fixture = 'refused-over-1pct'; Expect = 'refused 101 of 10000 to spawn is over the 1% ceiling' }
    @{ Name = 'build, no body (server) and no body PASS line'; Fixture = 'no-body'; Expect = 'no PASS line for the body' }
    @{ Name = 'build, 3 refused with 3 refused lines';Fixture = 'refused-listed'; Expect = $null }
    @{ Name = 'build, 250 refused with 200 refused lines (cap)'; Fixture = 'refused-cap'; Expect = $null }
    @{ Name = 'build, refused lines short of the count'; Fixture = 'refused-short'; Expect = '2 refused line\(s\) for 3 refused: expected 3' }
    @{ Name = 'build, unknown 2'; Fixture = 'unknown'; Expect = "regenerate from this build's catalogue.*ModdedThingA, ModdedThingB" }
    @{ Name = 'build, save failed'; Fixture = 'save-failed'; Expect = 'the save failed' }
    @{ Name = 'build, spawn total short'; Fixture = 'spawn-short'; Expect = 'spawned 31000 \+ refused 0 is not the 31120' }
    @{ Name = 'build, FAIL line'; Fixture = 'fail-line'; Expect = '1 FAIL line' }
    @{ Name = 'build, landing FAIL line'; Fixture = 'landing-fail'; Expect = 'FAIL bigbase: the landing is not over' }
    @{ Name = 'build, NOT settled'; Fixture = 'not-settled'; Expect = 'settle' }
    @{ Name = 'build, world.txt spawned differs from the log'; Fixture = 'world-mismatch'; Expect = 'spawned 31000 in world.txt, 31120 in the log' }
    @{ Name = 'build, no world.txt'; Fixture = 'pass'; NoWorld = $true; Expect = 'no world\.txt' }
    @{ Name = 'build, no save file in the guest'; Fixture = 'pass'; SaveBytes = -1; Expect = 'no save file' }
    @{ Name = 'build, save file under 1 MB'; Fixture = 'pass'; SaveBytes = 500KB; Expect = 'only \d+ bytes' }
    @{ Name = 'load pass'; Mode = 'bigbase-load'; Fixture = 'load-pass'; Expect = $null }
    @{ Name = 'load, a check line missing'; Mode = 'bigbase-load'; Fixture = 'load-missing'; Expect = 'older save loads' }
    @{ Name = 'load, FAIL line'; Mode = 'bigbase-load'; Fixture = 'load-fail'; Expect = '1 FAIL line' }
)

$bad = 0
foreach ($c in $cases) {
    $mode = if ($c.Mode) { $c.Mode } else { 'bigbase-build' }
    $log, $world = Read-Fixture $c.Fixture
    if ($c.NoWorld) { $world = $null }
    $bytes = if ($c.ContainsKey('SaveBytes')) { [long]$c.SaveBytes } else { [long]40MB }
    $got = Get-BigBaseVerdict $mode $log $world $bytes
    $ok = if ($null -eq $c.Expect) { $null -eq $got } else { $got -and $got -match $c.Expect }
    if ($ok) { Write-Host "ok   $($c.Name)" }
    else { $bad++; Write-Host "FAIL $($c.Name): got [$got], expected [$($c.Expect)]" }
}
Write-Host "$($cases.Count - $bad) of $($cases.Count) cases hold"
exit $(if ($bad) { 1 } else { 0 })
