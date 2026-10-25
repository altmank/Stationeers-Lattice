# Plans and ghosts

How to make, read, edit and finish ghosts and plans. The words are in [concepts](concepts.md).

The samples here and in the other docs use a small helper that throws on a refusal. Your mod handles refusals its own
way.

```csharp
static T Must<T>(Result<T, Reason> result) where T : notnull =>
    result.Match(static value => value, static why => throw new InvalidOperationException(why.Code.Value));
```

## Make ghosts

A `GhostDraft` is a ghost before the board gives it an id, a revision, a placer, a time and a state.

```csharp
var mod = Must(ModId.Create("mymod"));
var planner = Must(Maker.Create(mod, "planner"));

GhostDraft BuildAt(PrefabKey piece, WorldPoint point, PieceGrid grid, Turn turn) => new(
    new Intent.Build(new Placement(piece, Cell.FromWorld(point, grid), turn), Option<Finish>.None),
    Option<PlanId>.None,                 // none: the default plan
    planner,
    new Visibility.Everyone(),
    Option<Label>.None,
    Option<Tint>.None,
    ValueSet<GroupName>.Empty,
    ValueMap<AttachmentKey, Attachment>.Empty);
```

Place them in the default plan or a named one with `Command.Place`, or make a plan and its ghosts in one change with
`Command.CreatePlan`:

```csharp
var create = new Command.CreatePlan(new PlanDraft(
    Must(PlanName.Create("North store")), planner, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty))
{
    Ghosts = ValueList.From(drafts),
};

switch (lattice.Commands.Submit(CommandId.New(), create, player))
{
    case Submission.Answered { Outcome: Outcome.Applied applied }:
        var plan = applied.Changes.PlansOpened[0].Id;
        break;
    case Submission.Answered { Outcome: Outcome.Refused refused }:
        Report(refused.Why);             // each Refusal names its reason and the ghosts it concerns
        break;
    case Submission.Sent sent:
        Await(sent.Ticket);              // on a client: the outcome arrives as Events.Answered
        break;
}
```

- A ghost placed in a spot that already holds one replaces it (`RemovalCause.Replaced`).
- The submitting actor owns the new plan. `PlacedBy` on each ghost is that actor.
- A `Finish` asks for a colour, a name or a labeller setting: `Finish.Create(colour, name, setting)` refuses one that
  asks for nothing.
- `Intent.Remove`, `Intent.Swap` and `Intent.FinishOnly` point at a built structure by `StructureRef`: its reference
  id, piece and cell, so it is still found when the game gives it a new id.

## Read the board

| Read | Gives | Cost |
|---|---|---|
| `Board.Find(id)` | One ghost, or none | Constant |
| `Board.At(cell)` | The ghosts in a cell | Constant |
| `Board.On(structure)` | The ghosts that point at a built structure (remove, swap, finish, or started on it) | Constant |
| `Board.Select(selector)` | The ghosts a selector picks | Linear in the result |
| `Board.Groups` | Every group with how many ghosts the viewer may see in it (contract 0.5) | Cached per revision |
| `Board.As(actor)` | The same board as another actor may see it | Constant |
| `Plans.All`, `Plans.Find(id)` | The active plans the local side may see | Cached per revision |

`lattice.Board` is the local viewer's board: the local player, or Lattice's own actor on a dedicated server. An agent
reads through `Board.As(agent)` so the access policy filters for it. Reads on an unchanged board allocate nothing.

`lattice.World` reads the built world the same way: `Structure(id)`, `At(cell)`, `Select(structureSelector)`,
`NetworkOf(id)`, `RoomAt(cell)`.

## Edit ghosts

| Command | Does |
|---|---|
| `Change(which, edit, unchangedSince)` | One `GhostEdit` on every ghost a selector picks: `SetLabel`, `SetTint`, `JoinGroup`, `LeaveGroup`, `SetFinish`, `SetVisibility`, `Attach`, `Detach`. |
| `Move(to, unchangedSince)` | Moves build ghosts to new placements, keeping their ids, all at once. |
| `MoveToPlan(which, plan)` | Moves ghosts into another plan. |
| `Remove(which)` | Removes ghosts from the board. |
| `Revert(changeSet)` | Undoes one of the actor's own change sets; refused if another actor changed those ghosts since. It undoes plan changes, never built work. |
| `Batch(steps)` | Several commands, applied all or none as one change set, each on the board as the earlier ones leave it. A run, route or template in a batch is laid against the earlier steps' ghosts too (from contract 0.8): a trunk and its branches in one batch join with tees. |

```csharp
var phase = Must(GroupName.Create("Phase 1"));
var label = new Command.Change(
    new Selector.Group(phase),
    new GhostEdit.SetLabel(Option.Some(Must(Label.Create("phase 1")))),
    Option.Some(lattice.Board.Revision));   // refused changed-since if any of them changed after this read
```

Keep the `ChangeSet.Id` of each applied change on an undo stack. `Command.Revert` with it is your undo.

## Commands Lattice expands

These are worked out by a module into primitive commands. With the module off they are refused `module-missing`.

| Command | Module | Makes |
|---|---|---|
| `MarkRemoval(structures, into, madeBy)` | `selectors` | A remove ghost per structure. |
| `PlanFinish(structures, finish, into, madeBy)` | `selectors` | A finish ghost per structure. |
| `PlanSwap(structures, to, into, madeBy)` | `swaps` | "Swap all": a swap ghost per structure. `SwapTo.Exactly(piece)`, or `SwapTo.SameShapeFrom(kit)` for the piece of the same shape that kit places (every cable shape to its heavy cable shape). |
| `PlanRun(RunRequest)` | `runs` | A drawn run of cable, pipe or chute from a kit, from one cell through bend cells to another. `FromPort` and `ToPort` name an end as a ghost's port (contract 0.8): the end piece joins that port. `Lay` chooses the pieces (contract 0.9, [below](#choosing-the-pieces)). |
| `PlanRoute(RouteRequest)` | `routes` | A run found between two ends, under `RouteToggles`: on frames only, fewest bends, doors and windows clear, ports free, no bridging; and from contract 0.9 which leg first and hugging frames and walls ([below](#route-choices)). `SearchBound` caps the search. `FromPort` and `ToPort` as for a run; with ports free, a named port's own cell stays open to the route. `Branches` and `Lay` from contract 0.9. |
| `PlanSplit(which, into, madeBy)` | `runs` | Long straights planned as single pieces (contract 0.9, [below](#splitting-long-straights)). |
| `Transform`, `PlaceTemplate`, `PlaceTemplateIn` | `transforms`, `templates` | See [transforms and templates](transforms-and-templates.md). `PlaceTemplate` makes a new plan; `PlaceTemplateIn` (contract 0.10) places into a plan that is already there, and reverts like a place. |
| `Repair`, `TakeDown`, `Reopen` | `finished-plans` | See [finished plans](#finished-plans) below. |

Preview a run or route before you submit it: the preview carries a `RunReport` with its cells, the pieces placed, the
first cell that fails and its cost against single pieces. See [previews and analysis](previews-and-analysis.md).

A port end (`PortEnd.OnGhost(ghost, index)`, contract 0.8) is checked when the run is laid, against the board as the
sender sees it:

| Refused | When |
|---|---|
| `not-found` | The ghost is not on the board, or the sender may not see it (the answer is the same, so it tells nothing about hidden ghosts); with `problem=port`, its piece has no port at that index. |
| `invalid-value`, `problem=no-new-piece` | The ghost removes or finishes a structure: it has no new piece, so no port of its own. The standing structure joins through its cell. |
| `changed-since` | `From` or `To` is not the port's `Joins` cell now: the ghost moved, turned or changed piece since you read its ports. Facts `expected` and `now`. Read `PortsAt` again. |
| `port-mismatch` | The kit's run pieces share no network kind with the port: a pipe kit to a power port, gas to liquid. |
| `invalid-value`, `problem=same-port` | Both ends name one port. |

Every refusal names `ghost` and `port`. The port end is a planning input, not a link: the run's ghosts are plain
build ghosts. Move or turn a device with its runs by transforming them together; moving the device alone leaves the
run's end open, and removing it leaves the run.

To list what one built structure can become in place, ask `lattice.Swaps.OptionsFor(structure)`. Each `SwapOption`
is a placement in the structure's cell, and for a face piece with a rating, the load across that face now.

### Route choices

Contract 0.9 adds two route toggles. Both are `init` members of `RouteToggles`, off by default, so a request built as
in 0.8 searches as it did.

| Toggle | Does |
|---|---|
| `First` | `FirstLeg.Vertical` takes the up or down leg first, `FirstLeg.Level` the across leg first, among routes of equal length and bends. It only breaks ties: it never makes a route longer or adds a bend, under `FewestBends` too. None keeps the search's own order. |
| `HugFramesAndWalls` | A cell no large cell with a frame or a face piece (wall, window, floor) holds costs one more cell, so the route follows structure. A soft preference: the route gets at most one cell longer for each open cell it avoids. `OnFrames` still refuses open cells. |

`RouteRequest.Branches` lists extra starts (`RouteBranch(from, port)`), at most 16. Each is routed, in order, to the
nearest cell of the run so far (the main route and the branches before it) and joins there with a T. A branch stops at
the first run cell it reaches, so no loop is made. The main route and every branch are laid as one change set.

| Refused | When |
|---|---|
| `no-route`, `search-limit` | A branch has no route: fact `branch` is its index, with the nearest cell, as for a main route. Nothing is laid. |
| `too-large`, fact `branches` | More than 16 branches. |
| As for `FromPort` | A branch's `Port` is checked as a route's `FromPort` is, with fact `branch`. |

With `KeepPortsFree`, a branch's own port cell stays open to it. With `NoBridging`, the branches count as the same run:
the networks at every branch's start are the route's own, never a bridge.

### Choosing the pieces

`LayOptions(SinglePiecesOnly, MatchGrade)` (contract 0.9) is `Lay` on both `RunRequest` and `RouteRequest`, so a run
bar's piece toggles mean the same for a drawn run and a found one. `LayOptions.Default` is what a 0.8 request does.

- **`SinglePiecesOnly`:** never a long straight; every cell gets its own piece.
- **`MatchGrade`:** the run is laid with the kit of the run it joins (a heavy cable off a heavy main), when that kit
  lays the same network kinds on the same grid, read from the catalogue's ends. A gas pipe never becomes a liquid pipe,
  and a modded grade works like the game's. The from end decides first: a piece of that kit in the from cell, or a
  neighbour's end pointing into it. Then the to end. The `RunReport` names the kit in `LaidWith` when it is not the
  request's.

### Splitting long straights

`PlanSplit(which, into, madeBy)` (contract 0.9) plans the long straights among its targets as single pieces, with the
same splitting rule a run uses when it joins one.

- `SplitTargets.Ghosts(selector)`: a ghost long straight is replaced at once by single-piece ghosts, which keep its
  plan, visibility, groups, label, tint, data and finish. One `Revert` brings the long ghost back.
- `SplitTargets.Structures(selector)`: a built long straight gets a removal ghost and a build ghost for each of its
  cells, in `into`, as a run joining it plans it. One a ghost already removes or swaps is left alone.
- Pieces that are not long straights are left alone. When no target is one, the command is refused `invalid-value`
  with `problem=nothing-to-split`.

## The life of a ghost

1. **Planned.** Nothing of it is built.
2. **Started.** A part-built structure of its piece stands in its spot. `GhostState.Started` names it.
3. **Done.** The world matches it. The ghost leaves the board with `RemovalCause.Done`, and `Events.Completed` is
   raised with a `Completion`.

At any point it can turn **stale**: something different stands in its spot, or what it points at changed. The reason
is on `GhostState.Stale`. It can also leave with `RemovalCause.TargetGone` when the structure it points at is
destroyed.

Matching built pieces to ghosts runs on the host (module `matching`). It works the same for a player's hands, a
builder mod or an agent: nobody reports "done". The world decides.

## Plan facts and state

```csharp
if (Must(lattice.Plans.Facts(plan)) is Answer<PlanFacts>.Ready { Value: var facts })
{
    var next = facts.BuildableNext;     // ghosts whose support exists and that nothing blocks
    var left = facts.Progress.Remaining;
    var cost = facts.Cost;              // materials and tools still needed
}
```

`ValidAlone`, `ValidInWorld`, `BuildableNext`, `Progress`, `Consequences` (safety warnings) and `Cost` are worked
out in the background for every plan. `Plans.State(plan)` derives `Empty`, `HasErrors`, `Valid`, `InProgress` or
`Complete` from them, and `Events.PlanStateChanged` tells you when it moves.

## Finished plans

- `Command.FinishPlan(plan)` takes a plan off the active list. Its file stays as a record.
- `lattice.Plans.Finished` lists finished plans from an index, without opening their files: name, owner, when, how
  many pieces, and the box around them.
- `Command.Repair(plan, madeBy)` makes ghosts for whatever the plan built that has gone or changed since.
- `Command.TakeDown(plan, madeBy)` makes remove ghosts for everything it built that still stands.
- `Command.Reopen(plan)` makes it active again, history intact.
- `lattice.Plans.AsTemplate(plan, name)` gives what a plan holds as a template to place again.
- Finished plans live on the host. On a client, `Plans.Finished` is empty and `AsTemplate` of a finished plan is
  refused `module-missing`; `AsTemplate` of an active plan answers there from the client's board, as on the host. A client can still submit `Repair`, `TakeDown` and `Reopen` for a finished plan id it
  learned from `ChangeSet.PlansFinished`; the host works them out. See [multiplayer](multiplayer.md#what-runs-where).

## Limits

- A host can set a cap on ghosts per plan with `Registries.LimitPlanSize(max)`. Commands past it are refused.
- The host checks commands from clients against fixed limits. See [multiplayer](multiplayer.md).
