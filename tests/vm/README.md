# VM live checks

Lattice's in-game checks run in a VM rig through the shared VM-run kit. This folder holds what the kit reads:

- `vm-run.psd1`: the seven runs (what each launches, its phases and its pass rule).
- `hooks\`: `prelaunch.ps1` (shots folder, shader bundle, build md5s, the base file for the bigbase runs), `onpoll.ps1`
  (the NVIDIA renderer or the run fails), `judge.ps1` (FAIL lines, the mode's timing lines, the bigbase verdict).
- `judge.tests.ps1`, `prelaunch.tests.ps1`, `vm-run.tests.ps1`, `publish-bigbase.tests.ps1` and `fixtures\`: script tests.
  `build.ps1` runs them (not with `-Perf`) under Windows PowerShell 5.1, the shell the hooks run in. They touch no game.
- `publish-bigbase.ps1`: files a finished `bigbase-build` run in the worlds library (`STATIONEERS_WORLDS` or `-LibraryRoot`).
- `publish-builder-turns.ps1`: the same for a finished `builder-turns` run (its README and world.txt keys differ).
- The plugin that starts a mode lives in `tests\Lattice.LiveChecks`; it is test-only and never shipped.

## Start, wait, read, stop

Commit first: the kit builds the named ref, never uncommitted files.

```
powershell -NoProfile -File <RigVM>\host\vm-run.ps1 start -Mod Lattice -Kind proof -Proof <run> -Ref <branch|sha>
    [-World <library name>] [-Param bigbase=<tsv path>,held=<n>] [-ExpectedMinutes N] [-DryRun]
vm-wait.ps1 -KitId <id>      (repeat until it ends)
vm-result.ps1 -KitId <id>
vm-run.ps1 stop -KitId <id> -Reason <text>
```

`-DryRun` prints the spec and starts nothing.

## The runs

| Run | World | What it proves |
|---|---|---|
| `render-timing` | none | the draw calls' timing lines (ten TIMING lines) |
| `render` | `lattice-gen-big` | the world opens and renders, screenshots are taken |
| `render-look` | `lattice-gen-big` | as `render`, the look views |
| `render-perf` | `lattice-gen-big` | every scene and view with and without Lattice's drawing, GPU numbers, the control pair |
| `bigbase-build` | none (builds one) | spawns the generated base into a new world and saves it |
| `bigbase-load` | `lattice-gen-big-held` | the held-back structures, plans across an autosave reload, an older save |
| `builder-turns` | none (builds one) | drives the game's own builder with its keys answered by the plugin: each turn the rotate keys reach, for every cell-placed piece whose catalogue turns are limited, is built; saves `lattice-builder-turns` |

The bigbase runs need `-Param bigbase=<tsv path>`: the base file the guest reads. A run that was not given one stops
before the game starts.

A pass needs the done file, the NVIDIA renderer line (render runs), no live line saying FAIL and the mode's lines.
Results land in `runs\Lattice\<stamp>-<rig>\`.