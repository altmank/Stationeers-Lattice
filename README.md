# Lattice

**Status:** version 0.6.0, contract 0.6. Early: the first public release. The API grows in minor versions, which
only add; check the running version before you use a newer member
([versions and features](docs/consumer-guide.md#4-versions-and-features)).

The ghost core library other Stationeers mods build on. Lattice holds planned structures, called ghosts: see-through
outlines of frames, walls, pipes and anything else, placed where someone means to build them. It owns every plan
(what should be built, removed, swapped or finished, and where) and answers every question about plans. It has no
camera, no keys and no building of its own: a planner UI, a builder or an agent tool is a separate mod on top, and
every one of them uses the same public API.

## For players

Lattice does nothing on its own. Install it when a mod you use asks for it, such as a building planner, a builder or
an agent tool. It needs [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad).

- **Saving.** Plans live in a `lattice` folder beside the game's saves, never inside a save. Each save loads the
  plans as they were when it was written.
- **Removing it.** Your saves load the same without Lattice. Removing it hides every ghost, and the mods that need it
  cannot work without it. Put it back and the plans come back. Delete the `lattice` folder to drop them for good.
- **Multiplayer.** The host and every player need Lattice at the same version. The host decides what happens to a
  plan; players see the host's plans as they change. A dedicated server keeps and saves plans without drawing them.
- **After a game update,** a part of Lattice that relies on game code that changed turns itself off and says so in
  the log; the rest keeps working.

## For mod authors

Lattice gives your mod one shared, tested system for plans instead of one you write yourself. Start with the
[docs](docs/README.md).

### Features

| Feature | What your mod gets |
|---|---|
| **Plans of ghosts** | Build, remove, swap, paint and finish structures as ghosts, in named plans, as many per world as you like. Every change is a command you can preview, sync and undo. [Plans and ghosts](docs/plans-and-ghosts.md) |
| **Placement checks** | Every ghost is checked against the game's own placement rules and Lattice's (support, small-cell clashes, ends, face ratings); a ghost that cannot be built says why. Add rules of your own. [Validation](docs/validation.md) |
| **Build order** | What has to stand before what, across plans, and which ghosts can be built next. [Validation and build order](docs/validation.md#build-order) |
| **Steps and materials** | The exact game steps to reach each ghost and the bill for any set of ghosts. Builders claim ghosts and report status. [Steps and materials](docs/steps-and-materials.md) |
| **Previews** | What a change would do before it is made: the ghosts it adds, its findings, its bill, and its effects on networks, rooms and gas. [Previews and analysis](docs/previews-and-analysis.md) |
| **Move, turn, mirror and copy** | Whole plans or parts of them, and templates to place again. [Transforms and templates](docs/transforms-and-templates.md) |
| **Done tracking** | Build a piece where its ghost stands and the ghost is done, whoever built it. [Plans and ghosts](docs/plans-and-ghosts.md#the-life-of-a-ghost) |
| **Drawing** | Lattice draws every ghost with its own shaders. Your mod restyles them with layers of rules (colours, edges, patterns, highlights, labels, icons) and adds looks of its own. [Styles](docs/styles.md), [drawing](docs/drawing.md) |
| **Saving** | Plans are saved per world, beside the saves, with full history, branches after an older save and recovery after a crash. [Saving](docs/saving.md) |
| **Multiplayer** | The host decides; every player sees the host's plans as they change. The same code runs on every seat. [Multiplayer](docs/multiplayer.md) |
| **Light on the frame** | Long work is spread over frames or runs on Lattice's own thread, and reads of an unchanged board allocate nothing, so a large base stays smooth. [Concepts](docs/concepts.md#the-frame-and-its-budgets) |

Every piece and kit the game holds is in Lattice's catalogue, modded ones found by type like the game's own
([catalogue](docs/catalogue.md)). Every place Lattice calls your code has a stated contract
([extension points](docs/extension-points.md)).

### What you can build on it

| Layer | For example |
|---|---|
| A planner UI | Place, select, move and check ghosts with the mouse; runs of cable and pipe; themes. |
| A builder | Drones or automation that claim buildable ghosts, carry out their steps and report progress. |
| An agent or tool | A chat assistant or upgrade script that reads plans, dry-runs changes and submits them. |
| An add-on | A layout rule, a look, a support rule for modded pieces, a fact shown in previews. |

[Building a layer](docs/building-a-layer.md) walks through each one with code.

### Quick start

1. Reference `Lattice.Api.dll` from Lattice's mod folder with `Private="false"`.
2. Add `<DependsOn ModID="net.xceled.stationeers.lattice" WorkshopHandle="3826716939" />` to your About.xml.
3. Take the running system from `LatticeSystem.Current`, and subscribe to `LatticeSystem.Started` for later worlds.
4. Submit commands with `lattice.Commands.Submit` and react to `lattice.Events.Changed`.
5. Test against Lattice's real core with `Lattice.Testing`.

The details are in the [setup guide](docs/consumer-guide.md).

## Repository

| Folder | What |
|---|---|
| `src/` | The mod: public contract (`Lattice.Api`, `Lattice.Api.Unity`), core, game bridge, renderer and shaders, test host (`Lattice.Testing`) |
| `tests/` | Unit, consumer and performance tests, and the live harness that runs inside the game |
| `docs/` | The guides for mod authors ([index](docs/README.md)) |

## Build from source

| Project | Target | Built for |
|---|---|---|
| `src/Lattice.Api` | netstandard2.1 | The public contract; references nothing |
| `src/Lattice.Api.Unity` | netstandard2.1 | The public drawing contract; references Api and two Unity assemblies |
| `src/Lattice.Core` | netstandard2.1 | Pure code; references Api only |
| `src/Lattice.Render` | netstandard2.1 | The renderer; references Api, Api.Unity, Core and Unity |
| `src/Lattice.Game` | netstandard2.1 | The game bridge; references Api, Core, Render, the game, BepInEx, StationeersLaunchPad, LaunchPadBooster |
| `src/Lattice.Testing` | netstandard2.1 | The test host for consumers' tests: Lattice's real Core over a fake world |
| `tests/Lattice.TestKit` | netstandard2.1 | Fakes and loaders for the tests |
| `tests/Lattice.Tests` | net8.0 | xunit tests of Api and Core (never Game) |
| `tests/Lattice.Consumer.Tests` | net8.0 | A consumer of `Lattice.Api` alone, over a stub system: the main flows and two sketches |
| `tests/Lattice.Testing.Tests` | net8.0 | Tests of the test host, with a planner's and an agent's flow against the real Core |

The `src` assemblies ban LINQ (`BannedSymbols.txt`, error RS0030); tests may use it. Their public API is listed in
`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` per project (PublicApiAnalyzers); a public change that is not listed
fails the build (RS0016). `dotnet format analyzers src/Lattice.Api/Lattice.Api.csproj --diagnostics RS0016` adds the
missing lines.

**Game paths.** `Lattice.Game` finds the game's assemblies from `StationeersDir`. Copy
`Directory.Build.props.user.example` to `Directory.Build.props.user` (git-ignored) and set `StationeersDir` to the
game folder. Without that file the `STATIONEERS_DIR` environment variable is used. `GameManagedDir`, `BepInExCoreDir`
and `LaunchPadDir` default from it and can be set one by one. The game's assemblies are referenced, never copied.

**Run.** `./build.ps1` (PowerShell) restores, builds and runs every test; it exits non-zero on any failure.
`./build.ps1 -Configuration Debug` for a debug build. Needs a .NET SDK that targets net8.0 and the .NET 8 runtime.
The mod lands in `artifacts/<Configuration>/Lattice`, and the test host for your own tests in
`artifacts/<Configuration>/Lattice.Testing`.

## Licence

MIT. See [LICENSE](LICENSE).
