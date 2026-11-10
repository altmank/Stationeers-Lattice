# Tests for publish-bigbase.ps1 against a temp run folder and a temp worlds library. Touches no game or VM.
# Run: powershell -NoProfile -File publish-bigbase.tests.ps1   Exit 0 when every case holds, 1 otherwise.
$ErrorActionPreference = 'Stop'
$script = "$PSScriptRoot\publish-bigbase.ps1"
$root = Join-Path ([IO.Path]::GetTempPath()) "publish-bigbase-test-$PID"
$savedWorlds = $env:STATIONEERS_WORLDS
$bad = 0
function Check([string] $name, [bool] $ok, [string] $detail = '') {
    if ($ok) { Write-Host "ok   $name" } else { $script:bad++; Write-Host "FAIL $name $detail" }
}
# Runs publish-bigbase.ps1 in a child shell of the same edition; returns exit code and stderr+stdout text.
function Publish([string[]] $PublishArgs) {
    $exe = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh' } else { 'powershell.exe' }
    $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns a native command's stderr into an error under Stop
    $out = & $exe -NoProfile -ExecutionPolicy Bypass -File $script @PublishArgs 2>&1 | Out-String
    @{ Code = $LASTEXITCODE; Text = $out }
}
# One run folder: world.txt, result.json and a save zip whose world.xml holds (or lacks) a HumanSaveData entry.
function New-Run([string] $name, [string] $saveSuccess = 'True', [bool] $human = $true) {
    $run = Join-Path $root $name
    $station = 'test-station'
    New-Item -ItemType Directory -Force "$run\out", "$run\data\saves\$station" | Out-Null
    $world = @("station $station", "save-success $saveSuccess", 'unknown 0', 'structures 100', 'spawned 95', 'refused 5', 'held 0', 'landed yes', 'tsv base.tsv')
    [IO.File]::WriteAllText("$run\out\world.txt", ($world -join "`n") + "`n")
    [IO.File]::WriteAllText("$run\result.json", '{"status":"pass","mod":"Lattice","runId":"r1","owner":"o","rig":"rig1","info":{"args":"a"}}')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::Open("$run\data\saves\$station\$station.save", 'Create')
    try {
        $e = $zip.CreateEntry('world.xml'); $w = New-Object IO.StreamWriter($e.Open())
        $w.Write($(if ($human) { '<W><Human xsi:type="HumanSaveData" /></W>' } else { '<W><Thing /></W>' })); $w.Dispose()
    } finally { $zip.Dispose() }
    $run
}
try {
    New-Item -ItemType Directory -Force $root | Out-Null
    $lib = Join-Path $root 'library'
    New-Item -ItemType Directory -Force $lib | Out-Null

    # (a) no library at all
    $env:STATIONEERS_WORLDS = ''
    $r = Publish @('-Run', (New-Run 'good'))
    Check '(a) no library: exit 1' ($r.Code -eq 1) "exit $($r.Code)"
    Check '(a) no library: says so' ($r.Text -match 'no worlds library')

    # (b) save-success False
    $r = Publish @('-Run', (New-Run 'nosave' 'False'), '-LibraryRoot', $lib)
    Check '(b) save-success False: exit 1' ($r.Code -eq 1) "exit $($r.Code)"
    Check '(b) nothing written' (-not (Test-Path "$lib\test-station"))

    # (c) no HumanSaveData
    $r = Publish @('-Run', (New-Run 'nohuman' 'True' $false), '-LibraryRoot', $lib)
    Check '(c) no HumanSaveData: exit 1' ($r.Code -eq 1) "exit $($r.Code)"
    Check '(c) nothing written' (-not (Test-Path "$lib\test-station"))

    # (d) a good run; the library comes from STATIONEERS_WORLDS this time
    $env:STATIONEERS_WORLDS = $lib
    $run = Join-Path $root 'good'
    $r = Publish @('-Run', $run)
    Check '(d) good run: exit 0' ($r.Code -eq 0) "exit $($r.Code): $($r.Text)"
    $dest = "$lib\test-station\test-station\test-station.save"
    Check '(d) save copied, md5 equal' ((Test-Path $dest) -and (Get-FileHash $dest -Algorithm MD5).Hash -eq (Get-FileHash "$run\data\saves\test-station\test-station.save" -Algorithm MD5).Hash)
    $readme = if (Test-Path "$lib\test-station\README.md") { [IO.File]::ReadAllText("$lib\test-station\README.md") } else { '' }
    Check '(d) README has no drive path' ($readme -and $readme -notmatch '[A-Za-z]:\\')
    Check '(d) README has no LU' ($readme -and $readme -notmatch '\bLU\b')

    # (e) the folder exists, no -Replace
    $marker = "$lib\test-station\marker.txt"
    [IO.File]::WriteAllText($marker, 'keep')
    $r = Publish @('-Run', $run)
    Check '(e) exists without -Replace: exit 1' ($r.Code -eq 1) "exit $($r.Code)"
    Check '(e) nothing changed' (Test-Path $marker)
}
finally {
    $env:STATIONEERS_WORLDS = $savedWorlds
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "$bad failed"
exit $(if ($bad) { 1 } else { 0 })