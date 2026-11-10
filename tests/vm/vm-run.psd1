@{
    Schema = 1
    Mod = 'Lattice'
    OwnerPrefix = 'Lattice'
    RigVMPin = '015e263'
    Build = @{
        Command = 'build.ps1'
        Package = 'artifacts\Release\Lattice'
        Extra = @(
            @{ Name = 'LatticeLiveChecks'
               Command = 'dotnet build tests\Lattice.LiveChecks\Lattice.LiveChecks.csproj -c Release --no-restore'
               About = 'tests\Lattice.LiveChecks\About'
               Files = @('tests\Lattice.LiveChecks\bin\Release\LatticeLiveChecks.dll',
                         'tests\Lattice.Game.Live\bin\Release\netstandard2.1\Lattice.Game.Live.dll') }
        )
    }
    Runs = @{
        'render-timing' = @{
            Kind = 'proof'; NoWorld = $true; ExpectedMinutes = 5; StallSeconds = 600
            GameArgs = '-lattice-live render-timing -lattice-client-data {data} -lattice-out {out}'
            Phases = [ordered]@{ bepinex = 'Chainloader startup complete'; mods = 'Loaded Entrypoints'; live = 'LIVECHECK plugin loaded'
                                 started = 'LIVECHECK starting Lattice\.Game\.Live mode'; timing = 'LIVE TIMING calls'; finished = 'LIVE done$' }
            DoneGlob = '{out}\done-render.txt'; NoteRegex = '(LIVE|LIVECHECK) .*\bFAIL\b[^\r\n]*'
            PreLaunch = 'tests\vm\hooks\prelaunch.ps1'; OnPoll = 'tests\vm\hooks\onpoll.ps1'; Judge = 'tests\vm\hooks\judge.ps1'
            Pass = @{ Require = @(@{ Regex = '^LIVE done$'; Min = 1; In = 'out\live-bridge-render-timing.log' })
                      Forbid = @(@{ Regex = '\bFAIL\b'; In = 'out\live-bridge-render-timing.log' }) }
        }
        'render' = @{
            Kind = 'proof'; World = 'lattice-gen-big'; ExpectedMinutes = 5; StallSeconds = 600; NoPlans = $true
            GameArgs = '-lattice-live render -lattice-client-data {data} -lattice-out {out} -lattice-save {savefile} -lattice-shots {out}\shots'
            Phases = [ordered]@{ bepinex = 'Chainloader startup complete'; mods = 'Loaded Entrypoints'; live = 'LIVECHECK plugin loaded'
                                 started = 'LIVECHECK starting Lattice\.Game\.Live mode'; menu = 'LIVE render: main menu'; world = 'LIVE render: world open'
                                 shots = 'LIVE render: screenshot'; finished = 'LIVE done$' }
            DoneGlob = '{out}\done-render.txt'; NoteRegex = '(LIVE|LIVECHECK) .*\bFAIL\b[^\r\n]*'
            PreLaunch = 'tests\vm\hooks\prelaunch.ps1'; OnPoll = 'tests\vm\hooks\onpoll.ps1'; Judge = 'tests\vm\hooks\judge.ps1'
            Pass = @{ Require = @(@{ Regex = '^LIVE done$'; Min = 1; In = 'out\live-bridge-render.log' })
                      Forbid = @(@{ Regex = '\bFAIL\b'; In = 'out\live-bridge-render.log' }) }
        }
        'render-look' = @{
            Kind = 'proof'; World = 'lattice-gen-big'; ExpectedMinutes = 8; StallSeconds = 600; NoPlans = $true
            GameArgs = '-lattice-live render-look -lattice-client-data {data} -lattice-out {out} -lattice-save {savefile} -lattice-shots {out}\shots'
            Phases = [ordered]@{ bepinex = 'Chainloader startup complete'; mods = 'Loaded Entrypoints'; live = 'LIVECHECK plugin loaded'
                                 started = 'LIVECHECK starting Lattice\.Game\.Live mode'; menu = 'LIVE render: main menu'; world = 'LIVE render: world open'
                                 shots = 'LIVE render: screenshot'; finished = 'LIVE done$' }
            DoneGlob = '{out}\done-render.txt'; NoteRegex = '(LIVE|LIVECHECK) .*\bFAIL\b[^\r\n]*'
            PreLaunch = 'tests\vm\hooks\prelaunch.ps1'; OnPoll = 'tests\vm\hooks\onpoll.ps1'; Judge = 'tests\vm\hooks\judge.ps1'
            Pass = @{ Require = @(@{ Regex = '^LIVE done$'; Min = 1; In = 'out\live-bridge-render-look.log' })
                      Forbid = @(@{ Regex = '\bFAIL\b'; In = 'out\live-bridge-render-look.log' }) }
        }
        'render-perf' = @{
            Kind = 'proof'; World = 'lattice-gen-big'; ExpectedMinutes = 10; StallSeconds = 600; NoPlans = $true
            GameArgs = '-lattice-live render-perf -lattice-client-data {data} -lattice-out {out} -lattice-save {savefile} -lattice-shots {out}\shots'
            Phases = [ordered]@{ bepinex = 'Chainloader startup complete'; mods = 'Loaded Entrypoints'; live = 'LIVECHECK plugin loaded'
                                 started = 'LIVECHECK starting Lattice\.Game\.Live mode'
                                 menu = 'LIVE render: main menu'; world = 'LIVE render: world open'; plan = "LIVE render-perf: plan 'Perf lifted'"
                                 recorded = 'LIVE render: \d+ ghosts perf-overview with Lattice drawing'
                                 edges = 'LIVE render: \d+ ghosts dashed still perf-overview with Lattice drawing'; finished = 'LIVE done$' }
            PhaseLimit = @{ world = 180; plan = 180; recorded = 180; edges = 180 }
            DoneGlob = '{out}\done-render.txt'; NoteRegex = '(LIVE|LIVECHECK) .*\bFAIL\b[^\r\n]*'
            PreLaunch = 'tests\vm\hooks\prelaunch.ps1'; OnPoll = 'tests\vm\hooks\onpoll.ps1'; Judge = 'tests\vm\hooks\judge.ps1'
            Pass = @{ Require = @(@{ Regex = '^LIVE done$'; Min = 1; In = 'out\live-bridge-render-perf.log' })
                      Forbid = @(@{ Regex = '\bFAIL\b'; In = 'out\live-bridge-render-perf.log' }) }
        }
        'bigbase-build' = @{
            Kind = 'proof'; WorldBuild = $true; ExpectedMinutes = 8; StallSeconds = 600
            Params = @{ held = '5000'; bigbase = '' }
            GameArgs = '-lattice-live bigbase-build -lattice-client-data {data} -lattice-out {out} -lattice-bigbase {out}\bigbase.tsv -lattice-held {held} -lattice-bigbase-host "{bigbase}"'
            Phases = [ordered]@{ bepinex = 'Chainloader startup complete'; mods = 'Loaded Entrypoints'; live = 'LIVECHECK plugin loaded'
                                 started = 'LIVECHECK starting Lattice\.Game\.Live mode bigbase'
                                 menu = 'LIVE bigbase: main menu up'; world = 'LIVE bigbase: new world .* running'; stood = 'LIVE bigbase: body stands'
                                 spawning = 'LIVE bigbase: spawning: \d+ of'; spawned = 'LIVE bigbase: spawned \d+'
                                 settled = 'LIVE bigbase: after the spawn: (NOT )?settled'; saved = 'LIVE bigbase: save .+: success'; finished = 'LIVE done$' }
            PhaseLimit = @{ started = 300; menu = 600; world = 300; stood = 180; spawning = 180; spawned = 300; settled = 300; saved = 180 }
            DoneGlob = '{out}\done.txt'; NoteRegex = '(LIVE|LIVECHECK) .*\bFAIL\b[^\r\n]*'
            PreLaunch = 'tests\vm\hooks\prelaunch.ps1'; OnPoll = 'tests\vm\hooks\onpoll.ps1'; Judge = 'tests\vm\hooks\judge.ps1'
            Pass = @{ Require = @(@{ Regex = '^LIVE done$'; Min = 1; In = 'out\live-bridge-bigbase-build.log' })
                      Forbid = @(@{ Regex = '\bFAIL\b'; In = 'out\live-bridge-bigbase-build.log' }) }
        }
        'builder-turns' = @{
            Kind = 'proof'; WorldBuild = $true; NoPlans = $true; ExpectedMinutes = 10; StallSeconds = 600
            GameArgs = '-lattice-live builder-turns -lattice-client-data {data} -lattice-out {out}'
            Phases = [ordered]@{ bepinex = 'Chainloader startup complete'; mods = 'Loaded Entrypoints'; live = 'LIVECHECK plugin loaded'
                                 started = 'LIVECHECK starting Lattice\.Game\.Live mode builder-turns'
                                 menu = 'LIVE builder-turns: main menu up'; world = 'LIVE builder-turns: new world .* running'; landed = 'LIVE builder-turns: landed'
                                 floor = 'LIVE builder-turns: floor \d+ frames'; reached = 'LIVE builder-turns: \S+ reaches \d+ turns'
                                 placed = 'LIVE builder-turns: placed \d+, refused \d+'; saved = 'LIVE builder-turns: save .+: success'; finished = 'LIVE done$' }
            PhaseLimit = @{ started = 300; menu = 600; world = 300; landed = 240; floor = 180; reached = 300; placed = 900; saved = 300 }
            DoneGlob = '{out}\done.txt'; NoteRegex = '(LIVE|LIVECHECK) .*\bFAIL\b[^\r\n]*'
            PreLaunch = 'tests\vm\hooks\prelaunch.ps1'; OnPoll = 'tests\vm\hooks\onpoll.ps1'; Judge = 'tests\vm\hooks\judge.ps1'
            Pass = @{ Require = @(@{ Regex = '^LIVE done$'; Min = 1; In = 'out\live-bridge-builder-turns.log' }
                                  @{ Regex = '^LIVE builder-turns: \S+ turn \d+ placed '; Min = 1; In = 'out\live-bridge-builder-turns.log' }
                                  @{ Regex = '^LIVE builder-turns: save .+: success True'; Min = 1; In = 'out\live-bridge-builder-turns.log' })
                      Forbid = @(@{ Regex = '\bFAIL\b'; In = 'out\live-bridge-builder-turns.log' }) }
        }
        'bigbase-load' = @{
            Kind = 'proof'; World = 'lattice-gen-big-held'; ExpectedMinutes = 8; StallSeconds = 600; NoPlans = $true
            Params = @{ held = '5000'; bigbase = '' }
            GameArgs = '-lattice-live bigbase-load -lattice-client-data {data} -lattice-out {out} -lattice-bigbase {out}\bigbase.tsv -lattice-held {held} -lattice-bigbase-host "{bigbase}" -lattice-save {savefile}'
            Phases = [ordered]@{ bepinex = 'Chainloader startup complete'; mods = 'Loaded Entrypoints'; live = 'LIVECHECK plugin loaded'
                                 started = 'LIVECHECK starting Lattice\.Game\.Live mode bigbase'
                                 menu = 'LIVE bigbase: main menu up'; world = 'LIVE bigbase: world running'; held = 'LIVE bigbase: held-back structures spawned'
                                 reloaded = 'LIVE PASS plans after the autosave reload'; older = 'LIVE PASS the older save loads'; finished = 'LIVE done$' }
            PhaseLimit = @{ started = 300; menu = 300; world = 300; held = 600; reloaded = 600; older = 600; finished = 300 }
            DoneGlob = '{out}\done.txt'; NoteRegex = '(LIVE|LIVECHECK) .*\bFAIL\b[^\r\n]*'
            PreLaunch = 'tests\vm\hooks\prelaunch.ps1'; OnPoll = 'tests\vm\hooks\onpoll.ps1'; Judge = 'tests\vm\hooks\judge.ps1'
            Pass = @{ Require = @(@{ Regex = '^LIVE done$'; Min = 1; In = 'out\live-bridge-bigbase-load.log' })
                      Forbid = @(@{ Regex = '\bFAIL\b'; In = 'out\live-bridge-bigbase-load.log' }) }
        }
    }
}