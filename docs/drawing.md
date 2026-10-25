# Drawing: the Unity side

[Styles](styles.md) say how things look, in plain values. `Lattice.Api.Unity` is for what needs Unity types: named
materials, fonts, icons, meshes, renderer claims, the draw order and the camera. Reference it only if you use them; it
needs `UnityEngine.CoreModule` and `Unity.TextMeshPro` from the game.

## Get the drawing

```csharp
void Awake()
{
    if (LatticeDrawing.Current.TryGet(out var drawing))
        OnDrawing(drawing);
    LatticeDrawing.Started += OnDrawing;
}
```

- `LatticeDrawing.Current` is set when the `render` module starts, before `LatticeSystem.Started`, and cleared at
  world end before `BoardReset`.
- It stays none on a seat that cannot draw: a dedicated server, a headless client, or while `render` is off. Your
  styles still register there.
- Registrations end with the world, like every other.

## Materials

Lattice draws with its own shaders, shipped in its folder. `MaterialKeys` names its materials: `ghost` (translucent
fill), `solid`, `wire`, `edge` (outlines), `see-through`, `glow`, `label` and `faded`.

- `drawing.Material(key)` gives Lattice's material for a key (the override on top, if one is installed), so your own
  overlays match the ghosts. Changing it changes Lattice's drawing.
- `drawing.Override(key, material)` draws a key's things with your material. Overrides stack per key: the last one
  draws, and disposing it reveals the one under it. Lattice copies the material where it needs another queue, and
  never writes to yours.
- A `Style` picks a material for the fill with `Style.Material`.

**The material contract.** Every material Lattice draws with, its own or an override, gets:

- `Graphics.DrawMeshInstanced`, so it must support GPU instancing. An override without `enableInstancing` is refused
  at the call.
- `_Color`, the final colour with opacity, with eases, effects and dim already applied. A material that reads only
  `_Color` looks right.
- For shaders that declare them: `_LatticePattern`, `_LatticePatternColor`, `_LatticeEffect`, `_LatticeTime`,
  `_LatticeSeeThrough` (strength, range in metres, and the share of the strength a face seen square-on shows), and on
  edge materials `_LatticeEdge`.
- The clip as globals, set once a frame before any camera renders: `_LatticeClipMode`, `_LatticeClipPlane` or
  `_LatticeClipMin` and `_LatticeClipMax`, and `_LatticeBeyond`. Your own shaders can read them all frame.
- Overlays on built structures draw from Lattice's copy at the overlays queue. A shader that declares
  `_LatticeOffsetFactor` and `_LatticeOffsetUnits` gets a depth offset there, so it does not z-fight the original.

The XML docs on `MaterialKeys` give every value's layout. Where an override's shader cannot pattern or clip, Lattice
falls back on the CPU.

## Text styles, icons and decorations

```csharp
var labels = Must(TextStyleKey.Create(mod, "labels"));
Must(drawing.Register(labels, new TextStyle(font, 3.5f, new Rgba(240, 240, 240, 255))
{
    Wording = ValueMap.Of(ReasonCodes.NeedsSupport, Must(Label.Create("Needs a frame under it"))),
}));
Must(drawing.Register(Must(IconKey.Create(mod, "wrench")), wrenchTexture));
Must(drawing.Register(Must(DecorationKey.Create(mod, "arrow")), arrowMesh, arrowMaterial));
```

- A `TextStyle` is a TextMeshPro font asset, a size and a colour, with `Wording` for the reason codes a label shows.
  A label names it by key (`LabelLook.Shown`). `LibraryText.Default` uses a font the game ships.
- An icon is a `Texture2D` a style shows with `IconLook.Shown`. `LibraryIcons` are Lattice's glyph icons.
- A decoration is a mesh with its material, shown with `Style.Mark`.

## Piece looks

Lattice reads a ghost's mesh from the piece's prefab, modded pieces included. A modded piece that draws through
something Lattice cannot read gets a better look:

```csharp
Must(drawing.Register(new PieceLook(myPiece, ValueList.Of(new PieceMesh(mesh, Matrix4x4.identity)))));
```

Each `PieceMesh` sits in the piece's own space.

## Renderer claims

A claim says "I draw these myself". Lattice leaves the claimed parts out of its own drawing and hands you the claimed
things in view, once a frame, after its own submission.

```csharp
var xray = new RendererClaimDescriptor(
    Must(ClaimId.Create(mod, "x-ray")),
    new StyleMatch.Family(PieceFamilies.Pipe),
    ClaimParts.Mesh,                      // Lattice still draws edges, glows, labels, icons and decorations
    frame =>
    {
        for (var i = 0; i < frame.Count; i++)
        {
            var thing = frame[i];        // what, which part, its piece, its matrix, its resolved style
            DrawMine(thing.Piece, thing.Matrix, thing.Style);
        }
    })
{
    Order = 10,
};
Must(drawing.Claim(xray));
```

- `ClaimParts.Mesh` takes the mesh only. `ClaimParts.Everything` takes the whole thing.
- Several claims on one thing: the highest `Order` wins, then the later registration.
- The `IClaimFrame` is valid only during the callback, and allocates nothing.
- An exception leaves your callback. Lattice's drawing is already submitted; later claims are skipped that frame;
  your claim stays registered.

## Draw order and the camera

`drawing.Order` gives the render queues Lattice draws at, lowest first: `FadedWorld`, `Ghosts`, `Overlays`,
`SeeThrough`, `Labels`. Put your own drawing between them. `drawing.Camera` is the camera Lattice draws for, an `Option<Camera>`: none while the game has no main camera.

## A faded world

Lattice fades nothing of the built world. A mod that wants a faded view (an x-ray of a base while planning) does it
itself: it hides the original renderers and draws them with `drawing.Material(MaterialKeys.Faded)` at
`Order.FadedWorld`. That material writes no depth, so a ghost behind a faded wall still draws over it, and
see-through keeps meaning "through solid walls". Use `Styles.Matches` to pick which structures to fade with the same
matches as your ghost styles.
