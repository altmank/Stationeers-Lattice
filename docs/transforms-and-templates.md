# Move, turn, mirror and copy; templates

Ghosts move, turn and mirror as a set, whole plans or parts of them. Any plan can become a template to place again.

## Transform ghosts

```csharp
var up = new Command.Transform(
    new Selector.InPlan(plan),
    new Transformation.Move(new CellOffset(0, 20, 0)),   // one large cell up (decimetres)
    new TransformMode.Move());

var turned = new Command.Transform(
    new Selector.Group(wing),
    new Transformation.Rotate(pivot, Must(Turn.Of(SignedAxis.ZMinus, SignedAxis.YPlus))),   // a quarter yaw: +Z to +X
    new TransformMode.Copy(Must(Maker.Create(mod, "copy-tool"))));
```

| Transformation | Does |
|---|---|
| `Move(offset)` | Moves by a `CellOffset` in decimetres. |
| `Rotate(pivot, turn)` | Turns about a cell by one of the 24 turns. |
| `Mirror(plane)` | Mirrors across a `MirrorPlane(normal, at)`: square to an axis, through a coordinate in decimetres. |

| Mode | Does |
|---|---|
| `TransformMode.Move` | The ghosts themselves move, ids and makers kept. It expands into one `Command.Move`. |
| `TransformMode.Copy(madeBy)` | Transformed copies are placed, made by that maker. The originals stay. |

- **All or nothing.** When any picked ghost cannot be transformed (a remove, swap or finish ghost, an unknown piece,
  a piece with no mirror image, a result off its grid), the command is refused with one reason that names every ghost
  of the first such problem, so you can narrow the selector. A selection of no ghosts is refused `not-found`.
- Every moved ghost lands at once, so ghosts moved onto each other's old spots never replace each other. A ghost
  outside the moved set in a spot one lands on is replaced, as a placement would.
- Copies go into their originals' plans, with the originals' labels, tints, groups and attached data.
- A piece mirrors by its own ends and footprint, modded pieces included. Where the mirror image is another piece (a
  left-hand piece and its right-hand partner), a mod declares it with `CatalogueAddition.MirrorPartner(piece, mirror)`.
  A piece with neither is refused `no-mirror-image`.
- A large selection is worked out over several frames, so `Submit` may answer `Submission.Sent` even on the host.
  The outcome then arrives as `Events.Answered`.
- To move ghosts to placements you computed yourself, use `Command.Move(map, unchangedSince)` directly.

Preview a transform like any command to see where things land and what fails there.

## Templates

A `Template` is a plan in local coordinates: its origin is cell 0,0,0. It is a value your mod holds, stores and shares
as it likes.

```csharp
var template = Must(lattice.Plans.AsTemplate(plan, Must(TemplateName.Create("Airlock"))));

var place = new Command.PlaceTemplate(
    template,
    origin,                       // the cell the template's origin lands on
    Turn.None,                    // its turn about the origin
    new PlanDraft(Must(PlanName.Create("Airlock 2")), stamper, new Visibility.Everyone(),
        ValueMap<AttachmentKey, Attachment>.Empty));
```

- `Plans.AsTemplate` reads an active or a finished plan.
- `PlaceTemplate` makes a new plan of ordinary ghosts. They are not linked to the template: editing one changes
  nothing else.
- You can also build a `Template` yourself from `GhostDraft`s in local coordinates. Their plan fields are ignored
  when it is placed.
