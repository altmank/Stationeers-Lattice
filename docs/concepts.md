# Concepts

The words the rest of the docs use, and how the pieces fit. Read this once before the feature docs.

## The short version

- A **ghost** is one thing a player or a mod means to happen to one target: build a piece here, remove that
  structure, swap it, or paint, name or set it.
- Every ghost belongs to one **plan**. The **board** holds every ghost of the world.
- You change the board only by submitting a **command**. An applied command gives one **change set**, which is
  raised as an **event**, synced to every player and written to the plan's file.
- Questions that take work (validation, previews, analysis, plan facts and plan state) answer **pending** first. Lattice works them
  out over the next frames, raises `AnswersReady`, and you ask again.
- Everything is a value: immutable records, ids and value objects. Expected failures come back as a `Result` with a
  `Reason`, never as an exception.

## Ghosts

`Ghost` is a sealed record. Its main fields:

| Field | What |
|---|---|
| `Id` | A `GhostId`, never reused in a world. |
| `Plan` | The `PlanId` it belongs to. |
| `Intent` | What it asks for (below). |
| `State` | `Planned`, `Started` (a part-built structure of its piece stands in its spot) or `Stale` (it cannot be done as it is, with a reason). |
| `PlacedBy` | The `Actor` whose command placed it: its owner. |
| `MadeBy` | The `Maker` (a tool, an agent job) that made it. Lattice tells the maker when its ghosts are blocked. |
| `Visibility` | Who may see it, when an access policy enforces it. |
| `Label`, `Tint`, `Groups` | A player's label, the colour they sort it by, the named groups it is in. |
| `Attached` | Data mods attach under their own keys. Lattice saves and syncs it and never reads it. |

The four intents:

| Intent | Done when |
|---|---|
| `Intent.Build(Placement, Finish?)` | That piece stands in that cell and turn, fully built, with the finish if one is asked. |
| `Intent.Remove(StructureRef)` | The structure is gone. |
| `Intent.Swap(StructureRef Old, Placement New, Finish?)` | The old structure is gone and the new piece stands, fully built. |
| `Intent.FinishOnly(StructureRef, Finish)` | The structure has the colour, name or labeller setting asked. |

A ghost leaves the board when the world matches it (`RemovalCause.Done`), when a command removes it, when a new
ghost in its spot replaces it, or when the structure it points at is gone. Its record stays in the plan file.

A later contract minor may add intent kinds. Ask `lattice.Has(IntentKind)` before you use one newer than the
contract you built against, and skip and report a kind you do not know when you read the board.

## Plans

A `Plan` has an id, a name, an owner, a maker, a visibility and `HigherLayerData`: keyed data your mod stores on the
plan (a "draft" or "approved" flag, for example). Lattice stores and syncs that data and never acts on it.

- `lattice.Plans.Default` is the plan loose ghosts go in.
- `lattice.Plans.Facts(plan)` gives six facts Lattice works out: valid alone, valid in the world, buildable next,
  progress, safety warnings and cost.
- `lattice.Plans.State(plan)` gives `Empty`, `HasErrors`, `Valid`, `InProgress` or `Complete`, derived from the
  facts. Like the facts, it answers pending while they are worked out.
- A finished plan leaves the active list and stays on disk as a record. You can repair it, take it down or reopen
  it later.

See [plans and ghosts](plans-and-ghosts.md).

## Cells, turns and spots

- A `Cell` is a grid cell in decimetres, as the game keys its grid. A large cell is 2 m, a small cell 0.5 m.
  `Cell.FromWorld(point, grid)` gives the cell a piece takes at a world point, snapped as the game snaps it.
  `cell.Neighbour(face, grid)` gives the cell across a face.
- A `Turn` is one of the 24 turns of a cube. `Turn.Of(x, y)` gives the turn that sends +X and +Y where you say;
  `turn.Axes` tells you where it sends each axis.
- A `Placement` is a piece in a cell in a turn.
- A **spot** is a place one piece takes: a large cell's centre or one of its faces, or one of a small cell's slots
  (cable, pipe, chute, device, rail, other). The board holds at most one ghost per spot, so placing a ghost in a spot
  that holds one replaces it. A wall takes the face of the cell it faces into, as the game keys it, so two walls back
  to back on one boundary are two spots.

## Pieces, kits and the catalogue

- A **piece** is a structure prefab, keyed by `PrefabKey` (the game's prefab hash).
- A **kit** is the item a piece is placed from, keyed by `KitKey`. A kit with several pieces has **options**
  (`KitOption`), in the order the game scrolls them.
- Every piece sits on one `PieceGrid`: `Small` or `Large`.
- A piece has a **family** (`PieceFamily`): cable, pipe, chute, frame, wall, device or other. Lattice finds it by
  type, so a modded subclass takes its base's family. A mod can put a piece in another family.

`lattice.Catalogue` lists every piece and kit the game holds, modded ones included. Lattice finds everything by type
from the prefabs the game loads: nothing works only for base-game pieces. See [pieces and the catalogue](catalogue.md).

## Actors and makers

An `Actor` is who asks: `Actor.Player(ClientId)`, `Actor.Agent(AgentId)` or `Actor.Mod(ModId)`. Every command is
submitted for an actor, and the access policy judges it.

A `Maker` is what made a ghost, written `owner/name` (`mymod/run-tool`, `mymod/job-412`). It answers "which tool or
job do these ghosts come from", and it is who `Events.Blocked` tells when they cannot proceed.

## Commands and change sets

A `Command` is a change as data. It can be sent to the host, previewed, written to a plan file and undone.

```csharp
var submission = lattice.Commands.Submit(CommandId.New(), new Command.Place(drafts), actor);
```

- The **primitive** commands are applied by the board itself: `Place`, `Remove`, `Change`, `Move`, `CreatePlan`,
  `EditPlan`, `MoveToPlan`, `Revert`, `FinishPlan`, `CatchUp`, and a `Batch` of them (all or none).
- The others are **expanded** into primitive ones by the module that owns them: `MarkRemoval`, `PlanSwap`,
  `PlanFinish`, `PlanRun`, `PlanRoute`, `Transform`, `PlaceTemplate`, `Repair`, `TakeDown`, `Reopen`.
- `Submit` returns a `Submission`. On the host it is usually `Answered`, with an `Outcome`: `Applied` with the
  `ChangeSet`, or `Refused` with the reasons. On a client it is `Sent`, and the outcome arrives later as
  `Events.Answered`. Commands worked out over several frames (`PlanRoute`, `PlanSwap`, `Transform`, `PlaceTemplate`,
  `Repair`, `TakeDown`) can answer `Sent` on the host too. Handle both everywhere.
- The same `CommandId` sent twice applies once.
- A `ChangeSet` lists the ghosts added, changed (before and after) and removed (with the cause), and the plans
  opened, changed and finished. It is the unit of sync, events and undo.

## Selectors

A `Selector` describes a set of ghosts: by ids, a box of cells, a run through a cell, a network, a room, a group, an
owner, a maker, a plan, attached data, a piece, or `All` / `Any` of other selectors. The same value reads the board
(`Board.Select`) and drives bulk commands (`Command.Remove`, `Command.Change`, `Command.Transform`), validation,
bills and analysis.

`StructureSelector` is the same idea for built structures. It drives `MarkRemoval`, `PlanSwap` and `PlanFinish`.

## Answers that take work

Validation, previews, analysis, plan facts and plan state never stall a frame. They return `Answer<T>`:

- `Answer<T>.Ready(value, revision)`: the answer for that board revision.
- `Answer<T>.Pending(revision)`: Lattice is working it out. When it is done, `Events.AnswersReady` is raised. Ask
  again then.

Answers are cached per revision, so asking again on an unchanged board costs nothing.

## Revisions

The board's `Revision` rises by one per applied change set. A ghost's or plan's revision is the board revision of its
last change. Commands that edit (`Change`, `Move`, `EditPlan`) take `UnchangedSince`: pass the revision you read, and
the command is refused `changed-since` if someone changed those ghosts after it.

## Events

`lattice.Events` raises `Changed`, `Completed`, `StatusChanged`, `Answered`, `PlanStateChanged`, `Blocked`,
`StructuresMoved`, `AnswersReady`, `Loaded` and `BoardReset`.

- Every event is raised on the main thread, in revision order, at most one burst per frame.
- Handlers get immutable values. Reads inside a handler see the board as of the event.
- A command you submit from a handler is queued and applied after the current burst, never nested.
- `BoardReset` means every earlier answer is stale: the board was replaced or the world ended.

## Values, results and reasons

- **Ids and value objects.** Ids are `readonly record struct`s (`GhostId`, `PlanId`, `PrefabKey`, ...). Values with
  rules have a `Create` that returns a `Result`: `Lease.Create(30)`, `Label.Create("North store")`,
  `KitOption.Create(2)`. `default` of each has a stated meaning, usually "none", and commands refuse it.
- **Mod-owned names** are written `owner/name` from your `ModId`: `Maker.Create(mod, "run-tool")`,
  `RuleId.Create(mod, "window-clear")`, `ReasonCode.Create(mod, "needs-launch-mount")`. Names are lowercase words.
- **`Option<T>`** is a value that may be absent. `TryGet` reads it without allocating.
- **`Result<T, TError>`** is a value or the reason it could not be had. `Match`, `Map`, `Bind` and `TryGet` read it.
- **`ValueList<T>`, `ValueMap<K, V>`, `ValueSet<T>`** are immutable collections with value equality. Build them with
  `ValueList.Of(x)`, `ValueList.From(items)` or `.Empty`. They enumerate without allocating.
- **`Reason`** says why something was refused, fails, waits or warns: a `ReasonCode` agents act on and a UI words,
  `Facts` by name, the game's own message when there is one, and the `RuleId` that produced it. `ReasonCodes` lists
  Lattice's own codes. A mod's codes carry its prefix.

## Unions grow

Most "kinds" in the contract are closed record hierarchies: `Intent`, `Command`, `Selector`, `Step`, `Finding`,
`StyleMatch` and others. A later minor may add cases.

- When you **read** one, keep a default branch that skips the case and reports it.
- When you **send** one Lattice does not know (an older Lattice than you built against), it is refused
  `unknown-kind`.

## The frame and its budgets

Lattice runs inside the game's frame on the main thread, and is built to keep a large base smooth:

- Reads of the board on an unchanged board allocate nothing.
- Heavy checks run on Lattice's own worker thread over a snapshot, or in time slices across frames.
- Lattice's frame work runs in a fixed time budget, its own work first. Long drawing work of yours goes in
  `lattice.Slices` and runs in what remains.
- A command's own cost (its expansion and checks) is paid in the `Submit` call, on the host.

See [extension points](extension-points.md) for what each callback of yours may cost.

## Modules and seats

Lattice is split into modules: `catalogue`, `world`, `mirror`, `matching`, `selectors`, `validation`, `steps`,
`safety`, `plans`, `runs`, `routes`, `swaps`, `transforms`, `templates`, `saving`, `finished-plans`, `inspector`,
`executors`, `sync`, `preview`, `analysis`, `styles` and `render`.

- `lattice.Modules` lists each one, on or off with the reason. The list is fixed for the world.
- A module turns itself off when a game update removes members it uses, or when it is host only and you are on a
  client (`matching`, `saving`, `finished-plans`, `inspector`). `render` is off where nothing draws.
- A call into an off module answers `module-missing`, with the module named in the reason's facts.
