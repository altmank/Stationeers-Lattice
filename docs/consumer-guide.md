# Building on Lattice: setup

Lattice is the ghost core library for Stationeers: planned structures (ghosts), plans, validation, build order,
steps and bills, previews, analysis, saving and drawing. It does nothing on its own. Your mod uses it through
`Lattice.Api`, plus `Lattice.Api.Unity` when you hand Lattice's drawing Unity objects (materials, fonts, icons,
meshes, renderer claims) or read its draw order and camera.

This guide sets up a mod that uses Lattice, starting with a complete minimal one. Read [concepts](concepts.md) for the words, and
[building a layer](building-a-layer.md) for worked examples. The [docs index](README.md) lists every feature doc.

## A minimal mod

A complete mod that uses Lattice and logs each world it starts in. Sections 1 to 4 explain each part.

**Where Lattice is installed.** You reference Lattice's dlls from its installed mod folder:

- subscribed on the Steam Workshop: `<Steam library>\steamapps\workshop\content\544550\3826716939`;
- installed by hand: `Documents\My Games\Stationeers\mods\Lattice`.

**`MyMod.csproj`.** Set the two folders for your machine.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <StationeersDir>C:\Program Files (x86)\Steam\steamapps\common\Stationeers</StationeersDir>
    <LatticeDir>C:\Program Files (x86)\Steam\steamapps\workshop\content\544550\3826716939</LatticeDir>
    <GameManaged>$(StationeersDir)\rocketstation_Data\Managed</GameManaged>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Lattice.Api" HintPath="$(LatticeDir)\Lattice.Api.dll" Private="false" />
    <Reference Include="BepInEx" HintPath="$(StationeersDir)\BepInEx\core\BepInEx.dll" Private="false" />
    <Reference Include="UnityEngine" HintPath="$(GameManaged)\UnityEngine.dll" Private="false" />
    <Reference Include="UnityEngine.CoreModule" HintPath="$(GameManaged)\UnityEngine.CoreModule.dll" Private="false" />
    <!-- Only if you read the game's own types (the cursor, the local player, input): -->
    <Reference Include="Assembly-CSharp" HintPath="$(GameManaged)\Assembly-CSharp.dll" Private="false" />
    <Reference Include="UnityEngine.InputLegacyModule" HintPath="$(GameManaged)\UnityEngine.InputLegacyModule.dll" Private="false" />
  </ItemGroup>
</Project>
```

**`MyModPlugin.cs`.** StationeersLaunchPad loads a BepInEx plugin class from each enabled mod's dlls.

```csharp
using BepInEx;
using Lattice.Api;

namespace MyMod;

[BepInPlugin("com.example.mymod", "My Mod", "0.1.0")]
public sealed class MyModPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        if (LatticeSystem.Current.TryGet(out var running))
            OnWorld(running);
        LatticeSystem.Started += OnWorld;
    }

    private void OnWorld(IGhostSystem lattice) =>
        Logger.LogInfo($"Lattice world started: contract {lattice.Version.Major}.{lattice.Version.Minor}, {lattice.Plans.All.Count} plans");
}
```

**`About/About.xml`.** Your mod folder holds this file and your built dll, never Lattice's dlls.

```xml
<?xml version="1.0" encoding="utf-8"?>
<ModMetadata xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <Name>My Mod</Name>
  <ModID>com.example.mymod</ModID>
  <Author>You</Author>
  <Version>0.1.0</Version>
  <Description>Plans with Lattice.</Description>
  <DependsOn ModID="net.xceled.stationeers.lattice" WorkshopHandle="3826716939" />
</ModMetadata>
```

Put `About\About.xml` and `MyMod.dll` in `Documents\My Games\Stationeers\mods\MyMod`, enable it in the game's mod
list, and load a world. The game's `Player.log` (in `AppData\LocalLow\Rocketwerkz\rocketstation`) shows the line.

**Names the game shares.** The game's own assembly has types with three of Lattice's names. `Visibility` is in the
game's global namespace, so in your code it means the game's type, silently. `Label` and `BuildState` are also in
`Assets.Scripts.Objects`, so with that namespace imported they are ambiguous. In files that reference the game, alias
them inside your namespace:

```csharp
namespace MyMod;

using BuildState = Lattice.Api.BuildState;
using Label = Lattice.Api.Label;
using Visibility = Lattice.Api.Visibility;
```

## 1. Reference it, don't ship it

```xml
<Reference Include="Lattice.Api" HintPath="$(LatticeDir)\Lattice.Api.dll" Private="false" />
<!-- Only if you use Lattice's drawing: -->
<Reference Include="Lattice.Api.Unity" HintPath="$(LatticeDir)\Lattice.Api.Unity.dll" Private="false" />
```

`LatticeDir` is Lattice's installed mod folder. `Private="false"` keeps both dlls out of your mod folder. LaunchPad
loads every dll in every mod folder, and a second copy gives the process two `LatticeSystem.Current` statics, of which
yours is never set. Lattice logs every copy it finds at start, so a second one shows in the log.

`Lattice.Api` targets netstandard2.1 and references nothing. `Lattice.Api.Unity` also needs `UnityEngine.CoreModule`
and `Unity.TextMeshPro` from the game's `Managed` folder, referenced with `Private="false"` too. Each dll ships its
XML docs beside it, so your IDE shows every member's contract.

`LatticeDrawing.Current` is none on seats that cannot draw (a dedicated server, a headless client, or while the
`render` module is off). Check it, then subscribe to `LatticeDrawing.Started`, as for the system below.

## 2. Declare the dependency

In your `About/About.xml`:

```xml
<DependsOn ModID="net.xceled.stationeers.lattice" WorkshopHandle="3826716939" />
```

The ModID never changes. 3826716939 is Lattice's Steam Workshop item. LaunchPad loads Lattice first and warns when it
is missing, but it ignores a `Version` attribute, so check the version yourself (section 4).

## 3. Get the system for each world

```csharp
void Awake()
{
    if (LatticeSystem.Current.TryGet(out var running))
        OnWorld(running);              // a world was already running when your mod loaded
    LatticeSystem.Started += OnWorld;  // every later world
}

void OnWorld(IGhostSystem lattice)
{
    lattice.Registries.Register(myRule);   // registrations last for this world only
    lattice.Events.Changed += OnChanged;
    lattice.Events.BoardReset += OnReset;
}
```

- `Current` is set when a world starts and cleared when it ends. Ask for it when you need it, not at load.
- Everything you register, subscribe or claim belongs to one world. When that world ends, `Current` is cleared first
  and `BoardReset` is raised. The `IGhostSystem` you held is then stale.
- `BoardReset` is also raised when the board is replaced inside a world. `Current` is still set then: drop the
  answers you cached and keep going.
- A command still being worked out in slices when the world ends is dropped, and its `Answered` never comes.

## 4. Versions and features

- `lattice.Version` is the running contract; `ApiVersion.Current` is the one you compiled against. A minor only adds;
  a major may break. A member added after the first contract says in its XML doc which minor added it ("Added in
  contract 0.3"), so compare before you call it.
- Every interface in the contract is Lattice's to implement. A minor may add members to any of them, so a class of
  yours that implements one can stop compiling against a newer minor. To fake one in a test, inherit its stub in
  `Lattice.Testing.Stubs` (`StubPlans` for `IPlans`): a minor adds its new members there too, so your fake keeps
  compiling. "A minor only adds" is a promise to callers, not to implementers.
- `lattice.Has(kind)` tells you whether this Lattice knows an intent kind. Skip and report kinds you don't know.
- Read `Version` before you send a request that uses a newer member. For example, check for 0.12 before a run end on
  a built structure (`PortEnd.OnStructure`) or a route's `Via` cells. An older host refuses either; it never drops it.
- `lattice.Modules` lists each module, on or off with the reason. A module turns itself off when a game update
  removes members it uses, or when it is host only and you are on a client. Calls into an off module answer
  `module-missing`, with the module named in the reason's facts.
- Lattice never checks or refuses the version your mod was built against.

## 5. Ask the catalogue by kind

Keep no lists of pieces or kits of your own. `lattice.Catalogue` (contract 0.3, so check `Version` first) lists every
structure and every kit the game holds, modded ones found by type like the game's own.

```csharp
if (lattice.Version.Major == 0 && lattice.Version.Minor < 3)
    return; // no catalogue read before contract 0.3
var place = lattice.Catalogue.KitOf(structure.Piece); // the kit and option a built piece came from
```

Lookups, families, modded pieces and grids are in [pieces and the catalogue](catalogue.md).

## 6. Threads and the frame

- Call Lattice on the main thread. Events are raised there once a frame, in revision order.
- Answers that take work come back as `Answer.Pending`, then `AnswersReady` is raised and you ask again. This
  applies to validation, analysis, previews, plan facts and plan state.
- Put long work of your own in `lattice.Slices`. It runs in the drawing budget, after Lattice's own work.
- Rules marked `Worker` run on Lattice's rule threads over a snapshot of the world. Worker analyzers run on their own
  thread.

Every callback's thread, timing and failure rule is in [extension points](extension-points.md).

## 7. Multiplayer

- On a client, `Commands.Submit` sends the command to the host and returns `Sent`. The outcome arrives as
  `Events.Answered`, and the changes arrive as `Events.Changed`. The host's answer wins over a client's preview.
- Commands worked out over several frames (routes, swaps, transforms, templates, repair, take-down) can answer `Sent`
  on the host too. Handle `Sent` everywhere.
- Executors (claims and status) are host only. Saving, the inspector and matching built pieces to ghosts are host
  only too; clients see their results as change sets.

Limits on client commands, the access policy and what never syncs are in [multiplayer](multiplayer.md).

## 8. What Lattice never does

Lattice sits under your mod and does not police it. It does not catch, time or switch off your event handlers,
access policy, analyzers, command expanders or sliced work: what they throw leaves the call that ran them. The one
exception is validation rules from other mods (`RuleDescriptor`, `SupportRuleDescriptor`): each call has a time limit, a rule that runs over it gives a
`rule-timed-out` finding, and a rule that throws is switched off for the world and reported once.

## 9. Testing against Lattice

`Lattice.Testing` runs Lattice's real Core in your own .NET tests (any test framework): your tests check against
Lattice's actual answers, not a stand-in of your own that drifts from them. It is never in the Lattice mod folder and
must never be in yours: reference it from your test project only, and copy it into the test output.

```xml
<!-- LatticeTestingDir: artifacts/Release/Lattice.Testing of the Lattice build you run against. -->
<Reference Include="Lattice.Api" HintPath="$(LatticeTestingDir)\Lattice.Api.dll" Private="true" />
<Reference Include="Lattice.Testing" HintPath="$(LatticeTestingDir)\Lattice.Testing.dll" Private="true" />
<None Include="$(LatticeTestingDir)\Lattice.Core.dll;$(LatticeTestingDir)\Lattice.TestKit.dll" CopyToOutputDirectory="PreserveNewest" />
```

Build Lattice from source (see the repository README) to get that folder. It carries the Core of the build it came
from and has that build's version, so test against the `Lattice.Testing` of the Lattice your mod runs against.

**A scenario, then a world.** A `TestScenario` is an immutable value: the catalogue (`TestCatalogue.Hand`, about
twenty base pieces, or `TestCatalogue.FromFile` with a catalogue the game wrote on your machine:
`-lattice-write-catalogue <file>`), your modded pieces, structures standing at the start or a real save
(`WithSave`), and the local viewer (`As`). `TestWorld.Start` makes the world `LatticeSystem.Current`, so code written
for the game runs unchanged; `Dispose` ends it and clears `Current`.

```csharp
var scenario = TestScenario.Empty
    .WithStructure(TestCatalogue.Hand.Key("StructureFrame").Reduce(default(PrefabKey)), new Cell(11, 11, 11), default)
    .As(new Actor.Player(new ClientId(7)));
using var world = TestWorld.Start(scenario).Match(w => w, why => throw new Exception(why.ToString()));
```

One world at a time per process: turn off parallel test runs (xunit:
`[assembly: CollectionBehavior(DisableTestParallelization = true)]`). A second `Start` is refused `world-running`.

**Pump frames.** Nothing happens between your calls unless you run frames, as in game. `Frame()` runs one game frame
in the entry point's order (claims that lapsed, validation, plan state, sliced work, then events); `Frames(n)` runs n;
`Settle()` runs frames until Lattice has no work pending and returns how many, or refuses `not-settled` (a sliced job
of yours that never ends, for example). Heavy checks run inline in each frame by default, so a scenario gives the same
change sets and reports on every run; `TestScenario.WithWorkerThreads()` runs Lattice's own threads, as in game. Game time moves
only by `Advance(GameTime)`, for leases and claims.

**Pending, then `AnswersReady`.** Validation, previews and analysis answer `Pending` first. Ask, settle, ask again:

```csharp
var first = world.System.Preview.Of(command, world.Viewer);      // Answer<...>.Pending
world.Settle();                                                   // Events.AnswersReady is raised in here
var ready = (Answer<Result<Preview, ValueList<Refusal>>>.Ready)world.System.Preview.Of(command, world.Viewer);
```

A command that expands (a run, a swap) may return `Submission.Sent`; its outcome arrives as `Events.Answered`
after a settle.

**The world's hands.** `Build`, `Advance(StructureRef)` (one build step), `Remove` and `Paint` change structures as a
player's hands would, so completion, staleness and matching run as in game.

**Modded pieces.** `TestPiece.Create(name, kit, grid)` and its builder methods (`InFamily`, `WithSteps`, `WithEnd`
with its networks, `WithCell` for footprint cells, `WithCollision`, `SupportedBy` for a support group) add a piece no
base game has; `WithPiece` puts it in the
scenario's catalogue. A piece takes its kit's next option unless `AtOption(n)` names one; `AlsoIn(kit, n)` puts it in a
second kit and `Named(text)` gives it a display name. Lattice treats it as any piece the game holds, and
`TestWorld.System.Catalogue` lists it (`TestCatalogue.View` lists a catalogue without a world).

**Allocations.** `Allocations.Measure(call)` gives the bytes a repeated call allocates on your thread;
`Allocations.AssertNone(call, "what")` throws `AllocationException` naming the call and the bytes. Lattice's board
reads on an unchanged board allocate nothing.

**What the host does not do.** It draws nothing (no Unity: `LatticeDrawing.Current` stays none), has no other peers
(no sync, no client seat), runs none of the game's own placement rules (every placement the game would be asked about
passes; Lattice's own rules run), and saves plans only in a temporary folder deleted at `Dispose`, never in the game's
folders. `TestWorld.System.Styles` is Lattice's own style resolution (nothing is drawn); `FakeGhostStyles` only records
style calls, for tests that need no world. `TestWorld.Log` holds what Lattice logged.

**Fakes.** Test behaviour against `TestWorld` first: it is Lattice's real answers. For an isolated test that needs
one or two answers (a UI that lists plans, a tool that reads one cell), inherit the interface's stub in
`Lattice.Testing.Stubs` and override only what the test reads. A member you did not override throws
`NotSupportedException` naming itself, so a test that reaches further than it set up fails loudly. Each stub gains the
members a later minor adds, in the same release, so your fake keeps compiling; a class that implements the interface
itself does not. Events on a stub keep their subscribers, and its `Raise` methods fire them.

```csharp
sealed class PlansFake : StubPlans
{
    public override ValueList<Plan> All => ValueList.Of(hall);
}

var names = new PlanList(new PlansFake()).Names();   // your code under test reads only IPlans.All
```

`FakeGhostStyles` stays the fake for style calls: it records what your layer asks of Lattice's styles.
