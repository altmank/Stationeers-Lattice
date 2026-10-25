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

Every lookup is constant cost and allocates nothing (after a piece's family is first read).

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
