# vm-client-run -OnPoll hook for tests\vm\vm-run.psd1's runs (dot-sourced every poll: $r, $phase, $log, Invoke-Rig).
# The NVIDIA card or nothing, for the render* modes: the first poll that finds Player.log's Renderer line ends the run
# unless it names NVIDIA. The bigbase modes only record the renderer; any renderer builds a valid world.
if ($r.Info.rendererSeen -or $phase -in 'launching', 'launched') { return }
$rendererLine = Invoke-Rig -ArgumentList $log { param($l) if (Test-Path $l) { Select-String -LiteralPath $l -Pattern '^\s*Renderer:\s*(.+)$' | Select-Object -First 1 | % { $_.Matches[0].Groups[1].Value.Trim() } } }
if (-not $rendererLine) { return }
$r.Info.rendererSeen = $true; $r.Info.renderer = [string]$rendererLine
Rig-Log "renderer: $rendererLine"
if ($rendererLine -notmatch 'NVIDIA' -and $r.Info.mode -like 'render*') { Save-RigThumb warp; Stop-Rig fail "the renderer is '$rendererLine', not the NVIDIA card: no GPU numbers from this run" }
