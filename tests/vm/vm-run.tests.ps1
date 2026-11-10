# Static checks of vm-run.psd1 (the file the shared VM-run kit reads). Touches no game, VM or build.
# Run: powershell -NoProfile -File vm-run.tests.ps1   Exit 0 when every check holds, 1 otherwise.
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$file = "$PSScriptRoot\vm-run.psd1"
$raw = [IO.File]::ReadAllText($file)
$bad = 0
function Check([string] $name, [bool] $ok, [string] $detail = '') {
    if ($ok) { Write-Host "ok   $name" } else { $script:bad++; Write-Host "FAIL $name $detail" }
}

# The kit strips [ordered] before Import-PowerShellDataFile, which refuses it.
$tmp = Join-Path ([IO.Path]::GetTempPath()) "vm-run-$PID.psd1"
try {
    [IO.File]::WriteAllText($tmp, [regex]::Replace($raw, '\[ordered\]\s*(?=@\{)', ''))
    $c = Import-PowerShellDataFile -LiteralPath $tmp
} finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
Check 'the file loads' ([bool] $c)

$names = @('render-timing', 'render', 'render-look', 'render-perf', 'bigbase-build', 'bigbase-load')
Check 'exactly the six runs' ((@($c.Runs.Keys | Sort-Object) -join ',') -eq (($names | Sort-Object) -join ','))
$tokens = 'data', 'out', 'savefile'   # vm-client-run's own tokens, passed through the kit untouched
foreach ($n in $names) {
    $r = $c.Runs[$n]
    Check "$n GameArgs names its own mode" ($r.GameArgs -match "-lattice-live $([regex]::Escape($n))( |$)")
    Check "$n ExpectedMinutes > 0" ($r.ExpectedMinutes -gt 0)
    Check "$n has a finished = 'LIVE done`$' phase" ($raw -match "(?s)'$([regex]::Escape($n))' = @\{.*?finished = 'LIVE done\`$'" -and $r.Phases.finished -eq 'LIVE done$')
    foreach ($h in 'PreLaunch', 'OnPoll', 'Judge') {
        Check "$n $h exists" ($r[$h] -and (Test-Path -LiteralPath (Join-Path $repo $r[$h])))
    }
    $params = @(@($r.Params.Keys) + $tokens)
    $used = @([regex]::Matches($r.GameArgs, '\{(\w+)\}') | ForEach-Object { $_.Groups[1].Value })
    $unknown = @($used | Where-Object { $params -notcontains $_ })
    Check "$n GameArgs tokens are Params or vm-client-run tokens" ($unknown.Count -eq 0) ($unknown -join ',')
    $reserved = @(@($r.Params.Keys) | Where-Object { $_ -in 'data', 'out', 'save', 'savefile', 'savename', 'client', 'res', 'log', 'runid' })
    Check "$n has no Param named like a vm-client-run token" ($reserved.Count -eq 0) ($reserved -join ',')
    Check "$n has no phase named loading" (-not $r.Phases.ContainsKey('loading'))
}
Check 'no drive path in the file' ($raw -notmatch '[A-Za-z]:\\')
Check 'no .. in the file' (-not $raw.Contains('..'))
$about = @($c.Build.Extra)[0].About
Check 'Build.Extra.About holds About.xml' (Test-Path -LiteralPath (Join-Path $repo "$about\About.xml"))
Check 'Build.Extra.About holds the bepinex marker' (Test-Path -LiteralPath (Join-Path $repo "$about\bepinex"))
Write-Host "$bad failed"
exit $(if ($bad) { 1 } else { 0 })