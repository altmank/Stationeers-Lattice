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
| `Batch(steps)` | Several commands, applied all or none. |

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
| `PlanRun(RunRequest)` | `runs` | A drawn run of cable, pipe or chute from a kit, from one cell through bend cells to another. |
| `PlanRoute(RouteRequest)` | `routes` | A run found between two ends, under `RouteToggles`: on frames only, fewest bends, doors and windows clear, ports free, no bridging. `SearchBound` caps the search. |
| `Transform`, `PlaceTemplate` | `transforms`, `templates` | See [transforms and templates](transforms-and-templates.md). |
| `Repair`, `TakeDown`, `Reopen` | `finished-plans` | See [finished plans](#finished-plans) below. |

Preview a run or route before you submit it: the preview carries a `RunReport` with its cells, the pieces placed, the
first cell that fails and its cost against single pieces. See [previews and analysis](previews-and-analysis.md).

To list what one built structure can become in place, ask `lattice.Swaps.OptionsFor(structure)`. Each `SwapOption`
is a placement in the structure's cell, and for a face piece with a rating, the load across that face now.

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

## Limits

- A host can set a cap on ghosts per plan with `Registries.LimitPlanSize(max)`. Commands past it are refused.
- The host checks commands from clients against fixed limits. See [multiplayer](multiplayer.md).
