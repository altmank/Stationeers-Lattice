# vm-client-run -Judge hook for tests\vm\vm-run.psd1's runs (dot-sourced at the end, game still up: $r, $gOut, $GameArgs, Invoke-Rig).
# Writes <res>\timing.csv, then only ever lowers a pass: a FAIL line, no confirmed NVIDIA renderer, no live-bridge log,
# or a missing timing line for the mode fails the run; for render-perf also a recorded line without GPU span or Lattice
# GPU numbers, a view that draws no Lattice batches, or no PASS for the GPU control pair.
#
# The bigbase modes are judged apart (Get-BigBaseVerdict, pure: lines in, failure text or $null out; judge.tests.ps1
# dot-sourcing this file with $JudgeFunctionsOnly set gets the functions and nothing else). A bigbase run passes when
# no live line says FAIL, the spawn adds up, the save is good and world.txt agrees with the log (build), or when every
# line of the load checks is there (load).

# world.txt: one "key value" per line.
function ConvertFrom-WorldText([string[]] $Lines) {
    $facts = @{}
    foreach ($l in $Lines) { if ($l -match '^(\S+) (.*)$') { $facts[$Matches[1]] = $Matches[2].Trim() } }
    $facts
}

$script:BigBaseRefusedListed = 200
$script:BigBaseLoadLines = [ordered]@{
    'the loaded world is running' = '^LIVE bigbase: world running'
    'the held-back structures spawned' = '^LIVE bigbase: held-back structures spawned'
    'plans equal after the autosave reload' = '^LIVE PASS plans after the autosave reload'
    'the older save loads its plans' = '^LIVE PASS the older save loads'
}

# $WorldLines: world.txt lines, $null when absent. $SaveBytes: size of the guest's <station>.save, -1 when absent.
function Get-BigBaseVerdict([string] $Mode, [string[]] $Lines, [string[]] $WorldLines, [long] $SaveBytes) {
    $fails = @($Lines | ? { $_ -match '\bFAIL\b' })
    if ($fails) { return "$($fails.Count) FAIL line(s): $(($fails | Select-Object -First 3) -join ' | ')" }
    if ($Mode -eq 'bigbase-load') {
        $gone = @($script:BigBaseLoadLines.GetEnumerator() | ? { $re = $_.Value; -not @($Lines | ? { $_ -match $re }).Count } | % { $_.Key })
        if ($gone) { return "done, but no line for: $($gone -join '; ')" }
        return $null
    }

    $unsettled = @($Lines | ? { $_ -match '^LIVE bigbase: after the spawn: NOT settled' })
    if ($unsettled) { return "the settle did not finish, so the world was saved unsettled: $($unsettled[0])" }
    if (-not @($Lines | ? { $_ -match '^LIVE bigbase: after the spawn: settled' }).Count) { return 'no settle line after the spawn' }

    $spawnLine = @($Lines | ? { $_ -match '^LIVE bigbase: spawned \d+, refused \d+ ' }) | Select-Object -First 1
    if (-not $spawnLine -or $spawnLine -notmatch '^LIVE bigbase: spawned (\d+), refused (\d+) \(prefabs the game lacks (\d+)\), of (\d+) to spawn') { return 'no spawned line' }
    $spawned = [int]$Matches[1]; $refused = [int]$Matches[2]; $unknown = [int]$Matches[3]; $total = [int]$Matches[4]
    if ($unknown -ne 0) {
        $lacking = @($Lines | ? { $_ -match '^LIVE bigbase: the game lacks prefab (.+)$' } | % { $Matches[1].Trim() })
        return "the game lacks $unknown prefab(s) of the tsv: regenerate from this build's catalogue$(if ($lacking) { " (lacking: $($lacking -join ', '))" })"
    }
    if ($spawned + $refused -ne $total) { return "spawned $spawned + refused $refused is not the $total to spawn" }
    $listed = @($Lines | ? { $_ -match '^LIVE bigbase: refused \S+ at ' }).Count
    $wantListed = [Math]::Min($refused, $script:BigBaseRefusedListed)
    if ($listed -ne $wantListed) { return "$listed refused line(s) for $refused refused: expected $wantListed" }
    if ($refused * 100 -gt $total) { return "refused $refused of $total to spawn is over the 1% ceiling" }
    if (-not @($Lines | ? { $_ -match '^LIVE PASS bigbase: the body ends' }).Count) { return 'no PASS line for the body ending at the stand spot (the body check did not run)' }

    $saveLine = @($Lines | ? { $_ -match '^LIVE bigbase: save .+: success' }) | Select-Object -First 1
    if (-not $saveLine) { return 'no save line' }
    if ($saveLine -notmatch ': success True\b') { return "the save failed: $saveLine" }
    if ($null -eq $WorldLines) { return 'no world.txt' }
    $world = ConvertFrom-WorldText $WorldLines
    $mismatch = @(foreach ($pair in @(@('spawned', "$spawned"), @('refused', "$refused"), @('unknown', "$unknown"), @('save-success', 'True'))) {
        if ($world[$pair[0]] -ne $pair[1]) { "$($pair[0]) $($world[$pair[0]]) in world.txt, $($pair[1]) in the log" } })
    if ($mismatch) { return "world.txt disagrees with the log: $($mismatch -join '; ')" }
    if (-not $world['station']) { return 'world.txt has no station' }
    if ([int]$world['held'] -gt 0 -and ([int]$world['plans'] -lt 1 -or [int]$world['ghosts'] -lt 1)) {
        return "held $($world['held']) but the world has $([int]$world['plans']) plan(s) and $([int]$world['ghosts']) ghost(s): the held-back plan was not made"
    }
    if ($SaveBytes -lt 0) { return "no save file for station $($world['station']) in the guest" }
    if ($SaveBytes -le 1MB) { return "the save file of station $($world['station']) is only $SaveBytes bytes" }
    $null
}

if ($JudgeFunctionsOnly) { return }

$latticeMode = if ($GameArgs -match '-lattice-live (\S+)') { $Matches[1] } else { '' }
$bridge = @(Invoke-Rig -ArgumentList "$gOut\live-bridge-$latticeMode.log" { param($f) if (Test-Path $f) { Get-Content $f } })
if ($latticeMode -like 'bigbase-*') {
    if ($r.Status -ne 'pass') { return }
    if (-not $bridge) { Complete-Rig fail "done, but no live-bridge-$latticeMode.log"; return }
    $worldLines = $null; $saveBytes = [long]-1
    if ($latticeMode -eq 'bigbase-build') {
        $worldLines = Invoke-Rig -ArgumentList "$gOut\world.txt" { param($f) if (Test-Path $f) { Get-Content $f } }
        if ($worldLines) {
            $worldLines = @($worldLines)
            $station = (ConvertFrom-WorldText $worldLines)['station']
            if ($station) {
                $saveBytes = Invoke-Rig -ArgumentList "$gData\saves\$station\$station.save" { param($f) if (Test-Path -LiteralPath $f) { (Get-Item -LiteralPath $f).Length } else { [long]-1 } }
            }
        }
    }
    $verdict = Get-BigBaseVerdict $latticeMode $bridge $worldLines $saveBytes
    if ($verdict) { Complete-Rig fail $verdict } else { Complete-Rig pass "done: $latticeMode, no FAIL" }
    return
}
$bridgeFails = @($bridge | ? { $_ -match '\bFAIL\b' })
$timing = @($bridge | ? { $_ -match '^LIVE (TIMING |render: .*: frames |render: Render.s own frame|render-perf: |PASS render-perf: |FAIL render-perf: )' })
$r.Info.timingLines = $timing

# timing.csv: one row per recorded scene side (frame = the player's whole frame, unscaled delta; cpu = the main thread
# from frame start to end of frame; gpuSpan = D3D11 timestamps from the frame's first GPU command to its last, idle
# gaps included; latticeGpu = D3D11 timestamps around the camera's transparent pass, where all of Lattice's draws go;
# passSubmit = the render thread's time over that pass; RenderRun's FrameClock), with the medians of its interleaved
# blocks and the view's Lattice calls and instances; and one row per render-timing configuration.
$num = '([\d.]+)'
$stat = "(\d+) median $num ms p90 $num ms(?: p95 $num ms)? max $num ms"
function Get-Stat([string] $text) {
    if ($text -match "^$stat") { [pscustomobject]@{ n = $Matches[1]; median = $Matches[2]; p90 = $Matches[3]; p95 = $Matches[4]; max = $Matches[5] } }
    else { [pscustomobject]@{ n = ''; median = ''; p90 = ''; p95 = ''; max = '' } }
}
$counts = @{}
foreach ($l in $timing) { if ($l -match '^LIVE render-perf: (.+?): calls (\d+), instances (\d+),') { $counts[$Matches[1]] = @($Matches[2], $Matches[3]) } }
$rows = foreach ($l in $timing) {
    if ($l -match '^LIVE render: (?<scene>.+?): frames (?<frame>.+?); CPU \(main thread\) (?<cpu>.+?); GPU span (?<gpu>.+?); Lattice GPU (?<lat>.+?); median of (?<blocks>\d+) block medians: frame (?<fb>\S+) ms, CPU (?<cb>\S+) ms, GPU span (?<gb>\S+) ms, Lattice GPU (?<lb>\S+) ms;.*transparent pass submit \(render thread\) median (?<sub>\S+) ms') {
        $m = $Matches; $f = Get-Stat $m.frame; $c = Get-Stat $m.cpu; $g = Get-Stat $m.gpu; $lg = Get-Stat $m.lat
        $view = $m.scene -replace " (with Lattice drawing|with Lattice's drawing claimed away|with the overdraw layers|without)$", ''
        $cc = $counts[$view]
        [pscustomobject]@{ scene = $m.scene; frames = $f.n; frameMedian = $f.median; frameP95 = $f.p95; frameMax = $f.max
            cpuMedian = $c.median; cpuP95 = $c.p95; gpuFrames = $g.n; gpuSpanMedian = $g.median; gpuSpanP95 = $g.p95; gpuSpanMax = $g.max
            latticeGpuMedian = $lg.median; latticeGpuP95 = $lg.p95; latticeGpuMax = $lg.max; passSubmitMedian = $m.sub
            blocks = $m.blocks; frameBlockMedian = $m.fb; cpuBlockMedian = $m.cb; gpuSpanBlockMedian = $m.gb; latticeGpuBlockMedian = $m.lb
            calls = $(if ($cc) { $cc[0] } else { '' }); instances = $(if ($cc) { $cc[1] } else { '' }); submitMedian = '' }
    } elseif ($l -match "^LIVE TIMING calls (\d+) instances (\d+): submit median $num ms .*frame median $num ms p90 $num ms") {
        [pscustomobject]@{ scene = "calls $($Matches[1]) x $($Matches[2])"; frames = ''; frameMedian = $Matches[4]; frameP95 = ''; frameMax = ''
            cpuMedian = ''; cpuP95 = ''; gpuFrames = ''; gpuSpanMedian = ''; gpuSpanP95 = ''; gpuSpanMax = ''
            latticeGpuMedian = ''; latticeGpuP95 = ''; latticeGpuMax = ''; passSubmitMedian = ''
            blocks = ''; frameBlockMedian = ''; cpuBlockMedian = ''; gpuSpanBlockMedian = ''; latticeGpuBlockMedian = ''; calls = $Matches[1]; instances = $Matches[2]; submitMedian = $Matches[3] }
    }
}
if ($rows) { $rows | Export-Csv -NoTypeInformation -Encoding utf8 (Join-Path $r.Res 'timing.csv'); Rig-Log "timing.csv: $(@($rows).Count) row(s)" }

$missing = switch ($latticeMode) {
    'render-timing' { if (@($bridge | ? { $_ -match '^LIVE TIMING calls' }).Count -lt 10) { 'fewer than 10 TIMING lines' } }
    'render-perf' {
        # Five scenes (5,000; 20,000; dashed still; dashed moving; under-line), two views each, with and without.
        $want = foreach ($s in '', ' dashed still', ' dashed moving', ' under-line', 'small') { foreach ($v in 'perf-overview', 'perf-inside') { foreach ($w in 'with Lattice drawing', "with Lattice's drawing claimed away") {
            if ($s -eq 'small') { "5000 ghosts $v $w" } else { "20000 ghosts$s $v $w" } } } }
        $gone = @($want | ? { $w = $_; -not @($bridge | ? { $_ -like "LIVE render: ${w}: frames *" }).Count })
        # Every recorded side needs both GPU columns (span and Lattice GPU) as numbers; every view must log its Lattice
        # calls and draw Lattice batches; the control must show both GPU columns follow GPU load.
        $noGpu = @($bridge | ? { $_ -match '^LIVE render: .+?: frames ' -and $_ -notmatch '; GPU span \d+ median .*; Lattice GPU \d+ median ' })
        $views = foreach ($s in '5000 ghosts', '20000 ghosts', '20000 ghosts dashed still', '20000 ghosts dashed moving', '20000 ghosts under-line') { foreach ($v in 'perf-overview', 'perf-inside') { "$s $v" } }
        $noCalls = @($views | ? { $c = $counts[$_]; -not $c -or [int]$c[0] -le 0 -or [int]$c[1] -le 0 } | % { "$_ (calls/instances $(if ($counts[$_]) { $counts[$_] -join '/' } else { 'not logged' }))" })
        $control = @($bridge | ? { $_ -match '^LIVE PASS render-perf: GPU control' })
        if ($gone) { "no frame line for: $($gone -join ', ')" }
        elseif ($noGpu) { "$($noGpu.Count) recorded line(s) with no GPU numbers: $(($noGpu | Select-Object -First 2) -join ' | ')" }
        elseif ($noCalls) { "$($noCalls.Count) view(s) draw no Lattice batches: $(($noCalls | Select-Object -First 2) -join ' | ')" }
        elseif (-not $control) { 'no PASS line for the GPU control pair' }
    }
    default { $null }
}
if ($r.Status -ne 'pass') { return }
if (-not $r.Info.rendererSeen) { Complete-Rig fail 'no Renderer line in Player.log: the GPU is not confirmed' }
elseif ($bridgeFails) { Complete-Rig fail "$($bridgeFails.Count) FAIL line(s): $(($bridgeFails | Select-Object -First 3) -join ' | ')" }
elseif ($missing) { Complete-Rig fail "done, but $missing" }
elseif (-not $bridge) { Complete-Rig fail "done, but no live-bridge-$latticeMode.log" }
else { Complete-Rig pass "done on '$($r.Info.renderer)'; $($timing.Count) timing line(s), no FAIL" }
