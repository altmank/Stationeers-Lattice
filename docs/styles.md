# Styles: how ghosts look

Lattice draws every ghost, draft and built-structure overlay itself. Your mod says how they look with **style
layers**: rules that match things and set properties. Styles live in `Lattice.Api` (`lattice.Styles`, contract 0.2)
and need no Unity types. The Unity side (materials, fonts, icons, meshes, claims) is in [drawing](drawing.md).

Everything here is the local viewer's. It never syncs, never saves and never changes what another player sees. What
everyone should see alike is ghost data a rule reads: tint, label, groups and attachments.

## The cascade

For each property of each drawn thing, every rule of every enabled layer whose match holds is put in order, and the
last one that sets the property wins. The order is (band, the layer's `Order`, registration, the rule's position).
There is no specificity: order alone decides, so `Styles.Explain` can name the one rule behind each property. That
holds for families too: a `Family(window)` rule does not outrank a `Family(wall)` rule. To style windows apart from
walls, put the window rule after the wall rule.

| Band | For |
|---|---|
| Library | Lattice's starter library, below all. Not nameable. |
| `Meaning` | Mods adding meaning: an executor's working look, an add-on's icon. |
| `Theme` | The viewer's theme: restyles states, palette and motion. |
| `View` | Filters, hiding, planning mode, others' ghosts. |
| `Selection` | Hover and selection. |
| `Highlight` | Timed highlights. |
| Inspect | Lattice's "why" overlay, above all. Not nameable. |

A property no rule sets takes Lattice's default: ghost fill, the `planned` colour, opacity 0.3.

## A layer

```csharp
var failures = new StyleLayerDescriptor(
    Must(StyleLayerId.Create(mod, "failures")),
    StyleBand.Theme,
    0,
    ValueList.Of(new StyleRule(
        new StyleMatch.Finding(FindingKind.WillFail, Option<ReasonCode>.None),
        new Style
        {
            Edge = Option.Some<EdgeLook>(new EdgeLook.Line(LineStyle.Dashed, Must(Pixels.Create(2f)), new Paint.Token(LibraryPalette.Fail))),
            Pattern = Option.Some(Pattern.Hatch),
            Icon = Option.Some<IconLook>(new IconLook.Shown(LibraryIcons.Fails)),
        })));

var layer = Must(lattice.Styles.Add(failures));
layer.Disable();   // and layer.Enable(): cheap, one resolve per look in use
```

- Add a layer once per world and **toggle** it. Adding, replacing or removing a layer re-resolves every drawn thing,
  in time slices.
- A layer can carry a `Palette` and a `Motion`, which apply while it is enabled.
- Disposing the layer removes it.

## Matches

A `StyleMatch` is evaluated per drawn thing from its facets. There are no predicate delegates: no code of yours runs
per ghost. A mod with logic of its own computes a set and pushes it ([sets](#sets)).

| Match | Holds for |
|---|---|
| `Everything` | Every drawn thing. |
| `Picks(selector)` | Ghosts a selector picks. |
| `Intent(kind)` | Build, remove, swap or finish ghosts. |
| `Part(kind)` | `Placement` (the planned piece) or `Existing` (an overlay on a built structure: a removal, a swap's old piece, a finish, a structure set member). |
| `State(kind)` | `Planned`, `Started`, `Stale`. |
| `Finding(kind, code?)` | `Unchecked`, `Passes`, `WaitsFor`, `WillFail`, `Warns`, optionally one reason code. Drawing never starts a check: a pending answer reads as `Unchecked`. |
| `Status(kind, by?)` | An executor's `Working`, `Waiting`, `Paused`, or `None`. |
| `Family(family)` | The piece's family or one above it, modded pieces included: `Family(wall)` holds for windows too ([families](catalogue.md#families)). |
| `Facing(axis)` | A piece on a face of a large cell whose face, after its turn, is square to the axis: `Facing(Y)` for floors and ceilings, `Facing(X)` and `Facing(Z)` for upright faces. Never a piece in a cell's centre or on the small grid. `IPieceCatalogue.FacingOf` answers the same axis for a placement ([facing](catalogue.md#facing)). Contract 0.7. |
| `InSet(set)` | Members of a ghost set or structure set. |
| `Mine`, `Tinted`, `Finishes` | Placed by the local viewer; has a tint; asks for a finish. |
| `Draft`, `Pending`, `Structure` | Shown by a drafts handle; a draft sent to the host and not yet answered; a structure overlay. |
| `All`, `Any`, `Not` | Combinations. |

`Styles.Matches(match, structure)` asks a match of a built structure, for a mod that styles the built world the same
way (a faded view of everything but pipes, for example). `Facing` reads the structure's turn from Lattice's copy of the
world; a structure Lattice has not seen faces nothing.

A floor is where a face piece lies, not a family: a wall, a window or a grating laid flat holds `Facing(Y)` as well as
its family. For a floor look that wins over a window look, put the `Facing(Y)` rule after the window rule.

```csharp
var walls = new StyleLayerDescriptor(layerId, StyleBand.View, 0, ValueList.From(new[]
{
    new StyleRule(new StyleMatch.Family(PieceFamilies.Wall), wallLook),
    new StyleRule(new StyleMatch.Family(PieceFamilies.Window), windowLook),  // after walls: windows take this one
    new StyleRule(new StyleMatch.Facing(Axis.Y), floorLook),                 // last: anything laid flat
}));
```

## Properties

A `Style` is partial: every property is none ("no opinion") unless set. To clear a value set lower down, set the
property's explicit none case (`EdgeLook.None`, `GlowLook.None`, ...).

| Group | Properties |
|---|---|
| Body | `Presence` (drawn, hidden), `Fill` (ghost, solid, wire, none), `FillPaint`, `FillOpacity`, `Material` |
| Lines and marks | `Edge` (solid, dashed, dotted, a width, a paint), `Pattern` (hatch, cross-hatch, dots), `Glow` |
| Motion | `Effect` (steady, pulse, blink), `Enter` (flash when a thing comes to match), `Ease`, `Appear`, `Vanish` |
| Depth and distance | `SeeThrough` (through walls, with a strength, a range and optionally its own opacity), `BeyondClip`, `DistanceFade`, `NearLimit`, `Dim` |
| Words and icons | `Label` (what it says, text style, anchor, priority, range), `Icon` |
| From the catalogue | `OpenEnds`, `Ports`, `FlowArrows`, `WaitsLinks` (links to the ghosts it waits for) |
| Decorations | `Mark` (a registered mesh) |
| Picking | `Pickable` |

Colours are `Paint`: `Fixed(rgba)`, `Token(key)` from the palette in force, `GhostTint(fallback)` for the ghost's own
tint, `OwnerColour` from the palette's owner ramp, `FinishColour(fallback)` for the paint a finish asks for, or
`PlanColour(fallback)` for the ghost's plan's colour ([plan colour](#plan-colour), 0.22.9).

Labels say a `LabelText`: the ghost's label, its finish name, its finding's reason (worded by the text style), its
plan's name, its step number in build order, its piece count in the plan's bill, or fixed words. Lattice shows the
nearest labels in range first, by priority, culls overlaps and caps how many show.

## Plan colour

`Paint.PlanColour(fallback)` (added in 0.22.9) paints with the ghost's plan's `Plan.Colour`. It uses the palette's
colour for `fallback` when the plan has no colour, for a draft (even one that names a coloured plan), and for a
structure overlay. To fill every planned ghost with its plan's colour:

```csharp
var planFill = new StyleLayerDescriptor(Must(StyleLayerId.Create(mod, "plan-fill")), StyleBand.Theme, 0,
    ValueList.Of(new StyleRule(new StyleMatch.Everything(),
        new Style { FillPaint = Option.Some<Paint>(new Paint.PlanColour(LibraryPalette.Planned)) })));
```

- A plan recoloured with `PlanEdit.Recolour` restyles that plan's ghosts in the next drawing burst. The ghosts
  themselves are not written: no change set names them and their revisions stay. The cost is linear in the plan's
  ghosts, once.
- A ghost moved to another plan takes the new plan's colour, in the same pass as any other change to it.
- While no rule paints with `PlanColour`, a recolour restyles nothing. An unchanged board allocates nothing either way.

## Moving lines

`EdgeLook.Line` has three `init` properties, added in 0.22.9. A line built as before has none of them set and draws
exactly as before.

| Property | Default | Meaning |
|---|---|---|
| `Dash` | none | The length of one dash (`Dashed`) or dot (`Dotted`), in world metres. None or 0: the style's own, 0.15 m dashes and 0.0625 m dots. |
| `Speed` | 0 | How fast the dashes move along the line, in metres per second. The sign is the direction. 0 is still. |
| `Under` | none | An `UnderLine(width, paint)`: a wider, unbroken line in its own paint, drawn beneath. |

Solid lines ignore `Dash` and `Speed`. Motion `Reduced` and `Off` draw every line still. The marching ants of a removal
or a selection: 3 px red or white dashes moving over a 5 px dark line.

```csharp
var removing = new EdgeLook.Line(LineStyle.Dashed, Must(Pixels.Create(3f)), new Paint.Token(LibraryPalette.Remove))
{
    Dash = Option.Some(Must(Metres.Create(0.2f))),
    Speed = Must(Speed.Create(0.5f)),
    Under = Option.Some(new UnderLine(Must(Pixels.Create(5f)), new Paint.Fixed(new Rgba(20, 20, 20, 200)))),
};
```

Moving costs no CPU work per frame and nothing per ghost: the shader takes the phase from Unity's shared clock. An
under-line draws the edge with Lattice's internal edge-and-under-line material, so only looks that have one pay for
the extra pass. With the `edge` material overridden, your material draws the edge and is given `_LatticeEdgeUnder`
and `_LatticeEdgeUnderColor` to draw its own under-line.

### The dash rule, for other renderers

A renderer that draws its own lines (an outline round built pieces) matches Lattice's ghosts with this rule, from
`LatticeDash` in `src/Lattice.Render/Shaders/LatticeCommon.cginc`:

```
duty   = 0.6 for dashed, 0.25 for dotted (solid keeps every point)
period = dash > 0 ? dash / duty : 0.25          metres
phase  = frac((s - speed * _Time.y) / period)
kept   = phase <= duty
```

- `s` is the stripe coordinate in metres. Lattice's ghost edges use `s = x + y + z`, the world position's sum. Along
  an edge parallel to a world axis, one metre of edge is one metre of `s`. An edge square to (1, 1, 1) keeps one
  value of `s` all along, so it shows no dashes, as dashed edges always have. Dashes are cut in the world, so they
  don't crawl when the camera moves.
- `speed` is `Line.Speed` in metres per second. A positive speed moves the dashes towards rising `s`.
- `_Time.y` is Unity's shared shader time in seconds since the level loaded. It follows the game's time scale, so
  the dashes stop while the game is paused. Its origin is the level load, the same for every material, so two
  renderers that read it stay in step.
- In the material, `_LatticeEdge` is (x line style: 0 solid, 1 dashed, 2 dotted; y width in pixels; z `dash` in
  metres, 0 for the style's own; w `speed` in metres per second).

### The ants material

`MaterialKeys.Ants` (added in 0.22.9) is Lattice's material for a mod's own line meshes. Get it with
`IGhostDrawing.Material(MaterialKeys.Ants)`. It cuts dashes with the same `LatticeDash`, with `s` the length along the
line instead of `x + y + z`. That spaces dashes evenly round corners and draws them on edges of every direction.

- Properties: `_Color` (the colour), and `_LatticeEdge` laid out as above: style, width in pixels, dash in metres,
  speed in metres per second. Set them per draw through a `MaterialPropertyBlock`. The clip globals apply.
- Mesh: triangles, never `MeshTopology.Lines`. Unity's lines are one pixel wide on D3D11 and Vulkan, so they can't
  draw a 3 px line. Each segment from A to B is a quad of four vertices (two triangles):

| Channel | Holds |
|---|---|
| `POSITION` | This vertex's end of the segment, in object space. |
| `TEXCOORD0` | xyz: the segment's other end, in object space. w: the side, +1 or -1. |
| `TEXCOORD1.x` | The length along the line at this vertex's end, in metres, counted from the line's start. The ants material requires it: its dashes are cut by it. |

  The shader pushes each vertex sideways in screen space by half the width, so the line is the width in pixels at
  any distance. Write `TEXCOORD1` with `Mesh.SetUVs(1, ...)`.
- Under-line: draw the same mesh twice with the ants material, the under-line first: style 0 (solid), the wider
  width and the dark colour in one property block, then the dashes in another. That costs the same as a second pass,
  and the shader stays one pass for lines without one.
- It draws alpha-blended, with no depth write, both sides, depth-tested against the world.

## Palettes and motion

- A `Palette` maps `PaletteKey` tokens to colours. The palette in force is Lattice's default, overlaid key by key by
  each enabled layer's palette in cascade order.
- `Palette.Base` lays a library palette down first: `LibraryPalettes.Default` (the game cursor's colours),
  `ColourSafe` (Okabe-Ito hues) or `HighContrast`. A theme takes one whole and changes a few tokens.
- `LibraryPalette` lists every token the starter library paints with (`planned`, `next`, `waits`, `fail`, `warn`,
  `remove`, `stale`, `draft`, `select`). A theme that covers them all restyles every library look.
- `Motion`: `Full`, `Reduced` (pulses, blinks, flashes and moving dashes steady; eases at most 100 ms) or `Off`.
  The last enabled layer that states one wins.

## The starter library

Lattice ships a look for every state, so a mod that adds no layer still draws something readable. State lives on
edge, pattern, effect and icon, so no two states differ by hue alone:

- planned: ghost fill at 0.3 opacity, in the ghost's tint or `planned`; not checked yet: a dotted edge;
- can be built now: a solid `next` edge; waits: a dashed `waits` edge, fainter fill;
- will fail: a `fail` edge, hatch, a slow pulse, icon "!", three flashes when it starts failing; warns: a `warn`
  edge, dots, icon "⚠"; stale: `stale` fill, dashed edge, icon "?";
- remove: no fill, a `remove` edge, cross-hatch, icon "×"; swap: icon "⇄"; finish: icon "✎";
- drafts: fainter, dashed `draft` edge; sent and not yet answered: fainter still;
- executors: a `next` glow while working, icons "…" and "⏸" for waiting and paused;
- others' ghosts: dimmed, edge in the owner's colour; selected and hovered: a `select` edge and glow.

Ready-made layers in `LibraryLayers`, to add as they are or copy and change: `Blueprint` (the game construction
cursor's look, a Theme), and the off-until-enabled View layers `StepNumbers`, `BillCounts`, `Reasons` and
`PlanNames`. Each can be added once per world.

## Sets

A set is a list of ids a rule matches with `InSet`. The same id gives the same set to every caller.

```csharp
var hovered = Must(lattice.Styles.Set(LibrarySets.Hovered));    // the library draws it with a glow
var selected = Must(lattice.Styles.Set(LibrarySets.Selected));  // and this with a select edge and glow
hovered.Put(ghost);                    // the set becomes this one ghost: constant cost
selected.Replace(ValueList.From(ids));
```

- `Styles.Set(id)` holds ghost ids. Ghosts that leave the board leave the set.
- `Styles.StructureSet(id)` holds built structures, drawn as overlays while they are members (a box select of built
  things).
- Your own sets are how your logic reaches styles: compute the ghosts, push them into a set, match the set.

## Highlights

```csharp
Must(lattice.Styles.Highlight(new HighlightRequest(
    new HighlightTargets.Picked(new Selector.Group(group)), flash, Must(Duration.Create(2f)))));
```

A highlight draws targets (ghosts, the ghosts a selector picks at the call, or built structures) with a style in the
Highlight band for a time, then ends itself. Disposing ends it early.

`PickedAndBuilt` (contract 0.12) lights the ghosts a selector picks and the built structures a structure selector
picks as one highlight, with one handle and one timer. A whole network, its pieces and the ghosts on it:

```csharp
if (lattice.World.NetworkOf(cable.Id).TryGet(out var network))
    Must(lattice.Styles.Highlight(new HighlightRequest(
        new HighlightTargets.PickedAndBuilt(new Selector.Network(network), new StructureSelector.Network(network)),
        flash, Must(Duration.Create(2f)))));
```

Both selectors are resolved at the call. If either is refused (a kind this Lattice does not know, the selectors
module off), the highlight is refused with that reason and nothing is lit. A target case this Lattice does not know
is refused `unknown-kind`.

## Drafts

A drafts handle shows uncommitted ghosts: the piece at the cursor, a run being dragged.

```csharp
var cursor = Must(lattice.Styles.Drafts(Must(DraftsId.Create(mod, "cursor"))));
cursor.Show(ValueList.Of(draft));            // at once, in the call, as unchecked
cursor.Show(preview);                        // a preview's ghosts with their findings
cursor.Hand(lattice.Commands.Submit(CommandId.New(), command, player));
```

`Hand` passes the drafts to a submission with no frame where neither drafts nor ghosts are drawn: applied, they go in
the frame the new ghosts first draw; refused, they go at once; sent, they turn `Pending` until the host answers.

## The clip

`Styles.Clip(shape)` installs a viewer clip like a CAD section: a `ClipShape.Plane` or `ClipShape.Box`. Things
beyond it look as their `BeyondClip` says (drawn, hidden, dimmed or wire). Clips stack: the last installed applies,
and disposing brings back the one under it. `IClip.Move` moves it every frame if need be, at constant cost.
`Styles.InForce` and `Styles.ClipChanged` tell another mod which clip applies.

## Picking

```csharp
var ray = Must(PickRay.Create(origin, direction, 30f));
if (lattice.Styles.TryPick(ray, PickOptions.Default, out var hit) && hit.Kind == PickKind.Ghost)
    hovered.Put(hit.Ghost);
```

- `Pick` and `TryPick` find the nearest drawn, pickable ghost or draft along a ray. A hit names the ghost (or draft),
  the part, the footprint cell, the face entered, the point and the distance.
- `TryPick` (contract 0.4) allocates nothing: use it every frame the pointer moves.
- `PickOptions` filter by a match (`Only`), drafts (include, exclude, only) and things beyond the clip.
- Picking reads Lattice's own data (catalogue footprints and bounds), never renderers. Hidden and unpickable things
  are never hit. Pick built structures with the game's own raycast.

## Why it looks like that

`Styles.Explain(ghost)` gives a `StyleStory`: the resolved look, per property the source that set it (a library row,
your layer and rule, a highlight), and the facets that hold for the ghost. Use it in a debug overlay, or to answer a
player's "why is this red?".

## Cost

A look is resolved per signature (the facts it depends on), not per ghost, so twenty thousand ghosts in a dozen
states cost a dozen resolves. Toggling a layer costs one resolve per look in use. Moving the clip and pulsing effects
cost shader values only.
