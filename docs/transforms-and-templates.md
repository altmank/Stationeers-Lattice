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

A `Template` is a plan in local coordinates: its origin is cell 0,0,0. It is a value your mod holds. To save or share
it, write it with `lattice.Plans.WriteTemplate(template)` and read it back with `ReadTemplate(text)` (contract 0.10).

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
- `PlaceTemplateIn(template, origin, turn, into)` (contract 0.10) places the same ghosts into a plan that is already
  there, or the default plan. It opens no plan, so `Revert` undoes it like any place, and a ghost it replaced comes
  back. It is refused as `PlaceTemplate` is (fact `drafts`), and `not-found` (fact `plan`) for a plan the actor may not
  see or the board does not hold. The plan's size cap and your access policy apply as for any place.
- You can also build a `Template` yourself from `GhostDraft`s in local coordinates. Their plan fields are ignored
  when it is placed.

### Saving and sharing a template

`WriteTemplate` gives the template as text in Lattice's template form: a header line (`lattice-template`, the schema,
the name), then one draft per line. Pieces are written as prefab hashes, so the text means the same in every world and
on every seat, and every later Lattice version reads it. Plan fields are not written.

```csharp
var text = lattice.Plans.WriteTemplate(template);
File.WriteAllText(path, text);

// Later, in any world:
if (lattice.Plans.ReadTemplate(File.ReadAllText(path)).TryGet(out var read))
    Stamp(read);
```

`ReadTemplate` gives the whole template or a refusal, never part of one:

- `invalid-value` (fact `problem`, and `line` where there is one): not a template, a bad header or name, or a cut or
  damaged line.
- `unknown-kind` (fact `drafts`, their indexes): drafts this version cannot read in full, written by a newer Lattice
  with an intent or a field it does not know.
- A piece this world does not hold still reads. Placing it is refused, naming the draft (`drafts`).

Both calls are pure: they read no board or world state, work with every module off, and give the same answer on the
host and on a client.
