# Tests for make-fixtures.ps1's refusal of a player's base save. Touches no game or VM.
# Run: powershell -NoProfile -File make-fixtures.tests.ps1   Exit 0 when every case holds, 1 otherwise.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '..\fixtures\make-fixtures.ps1'
$bad = 0
function Check([string] $name, [bool] $ok, [string] $detail = '') {
    if ($ok) { Write-Host "ok   $name" } else { $script:bad++; Write-Host "FAIL $name $detail" }
}
function Make([string[]] $MakeArgs) {
    $exe = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh' } else { 'powershell.exe' }
    $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns a native command's stderr into an error under Stop
    $out = & $exe -NoProfile -ExecutionPolicy Bypass -File $script @MakeArgs 2>&1 | Out-String
    @{ Code = $LASTEXITCODE; Text = $out }
}
$empty = Join-Path ([IO.Path]::GetTempPath()) "make-fixtures-test-$PID"
try {
    New-Item -ItemType Directory -Force "$empty\saves", "$empty\worlds" | Out-Null
    foreach ($name in 'Vulcan2_4.save', 'vulcan24', 'VULCAN 2_4.save', 'Vulcan2_4') {
        $r = Make @('-SaveDir', "$empty\saves", '-WorldDir', "$empty\worlds", '-Saves', $name)
        Check "refuses '$name'" ($r.Code -ne 0 -and $r.Text -match 'refused' -and $r.Text -match 'world library') $r.Text
    }
    $r = Make @('-SaveDir', "$empty\saves", '-WorldDir', "$empty\worlds", '-Saves', 'some-other-world')
    Check 'a library world name is not refused' ($r.Code -ne 0 -and $r.Text -notmatch 'refused') $r.Text
}
finally { Remove-Item -Recurse -Force $empty -ErrorAction SilentlyContinue }
if ($bad) { Write-Host "$bad case(s) failed"; exit 1 }
Write-Host 'all cases hold'
exit 0
