# Pieces and the catalogue

`lattice.Catalogue` lists every piece and kit the game holds, so your mod keeps no lists of its own. Contract 0.3
added it: check `lattice.Version` before you read it.

## What it lists

- **Pieces.** `Pieces` holds every structure prefab, kitless ones included, as `PieceEntry(Key, PrefabName, Name,
  Grid, Kits)`.
- **Kits.** `Kits` holds every kit as `KitEntry(Key, PrefabName, Name, Options)`. `Options[i]` is the piece at
  `KitOption` index `i`, in the game's scroll order. A single-piece kit has one option.
- Both lists are sorted by prefab name (ordinal), so every seat lists them alike.
- `Name` is display text in the local player's language, read at world load. It differs between peers. Use `Key` or
  `PrefabName` as the identity.

## Lookups

| Call | Gives | Refused |
|---|---|---|
| `Piece(key)` | A `PieceEntry` | `unknown-piece` |
| `Kit(key)` | A `KitEntry` | `unknown-piece`, fact `kit` |
| `PieceOf(kit, option)` | The piece a kit places at an option | as `Kit`; `invalid-value` past the last option |
| `KitOf(piece)` | The `KitPlace(kit, option)` the piece's first build step uses: the game's own choice when several kits place it | `unknown-piece`; `not-found` with `problem=no-kit` for a piece no kit places |
| `FamilyOf(piece)` | The piece's `PieceFamily`: the top of its chain, so a window gives `wall` | `unknown-piece` |
| `FamiliesOf(piece)` | The piece's family and every family above it, narrowest first: `[window, wall]` for a window (contract 0.7) | `unknown-piece` |
| `ParentOf(family)` | The family a family lies under: `wall` for `window`, none for the rest (contract 0.7) | never refused |
| `PortsAt(placement)` | The piece's ports placed at a cell and turn, in the game's order ([ports](#ports), contract 0.8) | `unknown-piece` |
| `FacingOf(placement)` | The axis a face piece's face is square to under the placement; none for a piece off the faces ([facing](#facing), contract 0.9) | `unknown-piece` |

Every lookup but `PortsAt` is constant cost and allocates nothing (after a piece's family is first read). `PortsAt`
is linear in the piece's ports and allocates the list it returns.

```csharp
var catalogue = lattice.Catalogue;

// A picker scrolling a kit's options, in the game's order.
if (catalogue.Kit(kit).TryGet(out var entry))
    for (var i = 0; i < entry.Options.Count; i++)
        Show(i, catalogue.Piece(entry.Options[i]).Match(static p => p.Name, static _ => "?"));

// The kit a built piece came from.
if (catalogue.KitOf(structure.Piece).TryGet(out var place))
    Select(place.Kit, place.Option);
```

`PieceEntry.Kits` lists every kit that places a piece, `KitOf`'s answer first.

## Families

A family is what styles and filters group pieces by. Lattice's own are in `PieceFamilies`: `cable`, `pipe`, `chute`,
`frame`, `wall`, `window`, `device` and `other`. Lattice finds them by type, so a modded subclass of a base-game piece
takes its base's family.

Families form a tree. `window` lies under `wall`; the others have no parent. `StyleMatch.Family(f)` holds for a piece
whose family is `f` or lies under `f`, so a `Family(wall)` rule covers windows as well, and `Family(window)` picks out
windows alone. A window is a see-through face that seals air once built (its last build step blocks air): the game's
windows, the shuttered window, and a mod's subclass of them by its own build steps. A see-through face that never seals,
such as a floor grating, is a `wall`. Floors are not a family: match them with `StyleMatch.Facing(Axis.Y)`
([styles](styles.md#matches)).

`FamilyOf` answers the top of a piece's chain (`wall` for a window). If you compare families yourself, read
`FamiliesOf` instead, once `lattice.Version` is at least 0.7. A catalogue data file written before 0.7 says `wall` for
windows, and reads that way until it is exported again.

A mod puts a piece in another family, its own or one of Lattice's, while a handle lives:

```csharp
var rails = Must(PieceFamily.Create(mod, "rails"));
var handle = Must(lattice.Registries.Register(new CatalogueAddition.InFamily(myPiece, rails)));
```

The family a mod registers comes first, for every style rule and filter, until the handle is disposed. It is the
piece's narrowest family: put a piece in `PieceFamilies.Window` and it is a wall too; put it in a family of your own and
that is its only one. `FamilyOf` and `FamiliesOf` are the catalogue answers that change during a world, as these
handles come and go.

## Ports

`PortsAt(placement)` gives a placed piece's connection ports: the pipe, cable and chute ends it joins runs through.
It takes a `Placement`, so it answers for a ghost (`Intent.Build.Target`, or `Intent.Swap.New`), a draft, a preview's
new ghost, a built structure (`new Placement(info.Ref.Piece, info.Ref.At, info.Turn)`) and a piece nobody has placed
yet, all alike. Contract 0.8 added it.

| `Port` field | What it is |
|---|---|
| `Index` | The port's index in the game's own list of the piece's ends: the index the game and a built structure's port use, so a ghost's port 2 is the built device's port 2. |
| `Kinds` | The networks it joins, as `PortKinds` (`power`, `data`, `gas-pipe`, `liquid-pipe`, `chute`, ...). A cable end joins power and data. A network a mod adds to the game is kept under a key made from its bits, the same on every seat. |
| `GameName` | The game's own name for its kinds, such as `PowerAndData` or `Pipe`. |
| `Role` | What it does for its device: `Input`, `Input2`, `Output`, `Output2` or `Waste`; none for a plain run end. |
| `From` | The small cell it leaves from, inside the piece. |
| `Toward` | The way it points out of the piece. |
| `Joins` | The small cell a joining piece takes: `From`, one small cell `Toward`. A run that ends here, leaving back toward the port, joins it. |

```csharp
// The ports of a pump ghost, as the game numbers them.
var placement = ((Intent.Build)pump.Intent).Target;
foreach (var port in lattice.Catalogue.PortsAt(placement).Match(static p => p, static _ => ValueList<Port>.Empty))
    Show($"port {port.Index} {port.GameName} {port.Role} at {port.Joins}, facing {port.Toward}");
```

To lay a run between two ghosts' ports, name the ports as the run's ends: `RunRequest.FromPort` and `ToPort` (and
the same on `RouteRequest`) take `PortEnd.OnGhost(ghost, index)`, and `From`/`To` are the ports' `Joins` cells as you
read them. The run's end pieces then leave toward the ports, so they join them, and the preview's
`RunReport.PortEnds` names the piece that joins each port. See the agent recipe in
[building a layer](building-a-layer.md#joining-ghosts-ports).

Whether a port is already joined is a world fact, not a catalogue one: read `lattice.Board.At(port.Joins)` and
`lattice.World.At(port.Joins)`.

A catalogue data file written before 0.8 does not hold the game's indexes: it numbers ports in their order among a
piece's ends, which differs from the game's for a piece whose list has an end with no network. Export it again.

## Facing

`FacingOf(placement)` answers the axis a face piece's face is square to at that cell and turn: `X` or `Z` for an
upright wall or window, `Y` for a floor or a wall laid flat. It is the axis `StyleMatch.Facing` matches, worked out
from the piece's own face points, so a modded face piece answers as the game places it. A piece that takes a cell
centre or small-grid slots (a frame, a cable, a device) answers none. Contract 0.9 added it.

A tool that lays face pieces in a drag uses it to keep the drag in the face's plane: a line along an axis square to
the answer, a rectangle over the two axes square to it, and layers one large cell apart along it.

```csharp
// The axes a wall's line or rectangle may run along: the two square to its face.
if (lattice.Catalogue.FacingOf(held).TryGet(out var facing) && facing.TryGet(out var normal))
    Span(normal == Axis.X ? (Axis.Y, Axis.Z) : normal == Axis.Y ? (Axis.X, Axis.Z) : (Axis.X, Axis.Y));
```

## Modded pieces

Lattice finds everything by type from the prefabs the game loads: a mod's kit is a kit with its options in its own
order, and a mod's structure is a piece. Nothing in Lattice works only for base-game pieces.

Where Lattice cannot read a fact from a modded piece, a mod tells it:

| You need | Register |
|---|---|
| A piece's mirror image is another piece | `CatalogueAddition.MirrorPartner(piece, mirror)` |
| A piece in a family | `CatalogueAddition.InFamily(piece, family)` |
| What holds a piece up | A `SupportRuleDescriptor` ([validation](validation.md#add-a-support-rule)) |
| A better ghost mesh than Lattice reads from the prefab | `IGhostDrawing.Register(PieceLook)` ([drawing](drawing.md#piece-looks)) |

## Cells and grids

- `PieceEntry.Grid` is `PieceGrid.Small` (0.5 m cells: cables, pipes, chutes, small devices) or `PieceGrid.Large`
  (2 m cells: frames, walls, large devices).
- `Cell.FromWorld(point, grid)` gives the cell a piece takes at a world point, snapped as the game snaps it (contract
  0.6).
- `cell.Neighbour(face, grid)` gives the cell across a face: one cell along its normal (contract 0.6).

## Testing with the catalogue

`Lattice.Testing` gives you a catalogue without the game: a hand-written one of about twenty base pieces, or the
catalogue the game wrote on your machine with `-lattice-write-catalogue <file>`. Your modded pieces go in with
`TestPiece`. See [testing](consumer-guide.md#9-testing-against-lattice).
