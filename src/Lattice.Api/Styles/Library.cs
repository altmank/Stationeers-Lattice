namespace Lattice.Api;

/// <summary>
/// Every palette token Lattice's starter library paints with. Each library palette defines every one of these, so a
/// theme that covers them all restyles every library look.
/// </summary>
public static class LibraryPalette
{
    /// <summary>A planned ghost's fill (the game's blueprint blue), and the unchecked edge.</summary>
    public static readonly PaletteKey Planned = PaletteKey.Known("planned");

    /// <summary>The edge of a ghost that can be built now (the game's placeable green), and the working glow.</summary>
    public static readonly PaletteKey Next = PaletteKey.Known("next");

    /// <summary>The edge of a ghost that waits for others.</summary>
    public static readonly PaletteKey Waits = PaletteKey.Known("waits");

    /// <summary>The edge of a ghost that will fail (the game's refused red).</summary>
    public static readonly PaletteKey Fail = PaletteKey.Known("fail");

    /// <summary>The edge of a ghost that warns (the game's network yellow).</summary>
    public static readonly PaletteKey Warn = PaletteKey.Known("warn");

    /// <summary>The edge of a structure to be taken down.</summary>
    public static readonly PaletteKey Remove = PaletteKey.Known("remove");

    /// <summary>A stale ghost's fill and edge.</summary>
    public static readonly PaletteKey Stale = PaletteKey.Known("stale");

    /// <summary>A draft's edge.</summary>
    public static readonly PaletteKey Draft = PaletteKey.Known("draft");

    /// <summary>The edge and glow of hovered and selected things.</summary>
    public static readonly PaletteKey Select = PaletteKey.Known("select");
}

/// <summary>Lattice's palettes, which a theme or view layer can include as its own palette's base.</summary>
public static class LibraryPalettes
{
    /// <summary>The default palette: the game cursor's colour language.</summary>
    public static readonly PaletteId Default = PaletteId.Known("default");

    /// <summary>Okabe-Ito hues, distinct for the common colour-vision differences.</summary>
    public static readonly PaletteId ColourSafe = PaletteId.Known("colour-safe");

    /// <summary>High contrast.</summary>
    public static readonly PaletteId HighContrast = PaletteId.Known("high-contrast");
}

/// <summary>
/// Lattice's ready-made layers, for a consumer to add as they are or to copy and change (<see cref="IGhostStyles.Add"/>).
/// Each read gives a new descriptor. Their ids are Lattice's (bare), so each can be added once per world.
/// </summary>
public static class LibraryLayers
{
    /// <summary>Where the label layers' labels show from.</summary>
    private const float LabelRange = 30f;

    /// <summary>
    /// The <c>blueprint</c> example theme (Theme band): the game construction cursor's look. The whole piece takes the
    /// cursor's colour by finding (green can be built, red will fail, yellow warns, the blueprint blue otherwise), and a
    /// failing ghost holds still as the cursor does, which shows how a theme turns one library effect off. The colours are
    /// Unity's <c>Color.blue</c>, <c>green</c>, <c>red</c> and <c>yellow</c>, which the game's cursor uses.
    /// </summary>
    public static StyleLayerDescriptor Blueprint
    {
        get
        {
            var placement = new StyleMatch.Part(PartKind.Placement);
            var colours = new System.Collections.Generic.Dictionary<PaletteKey, Rgba>(4)
            {
                [LibraryPalette.Planned] = GameColours.Blue,
                [LibraryPalette.Next] = GameColours.Green,
                [LibraryPalette.Fail] = GameColours.Red,
                [LibraryPalette.Warn] = GameColours.Yellow,
            };
            var rules = new[]
            {
                new StyleRule(placement, new Style
                {
                    Fill = Option.Some(FillMode.Ghost),
                    FillPaint = Option.Some<Paint>(new Paint.Token(LibraryPalette.Planned)),
                    FillOpacity = Option.Some(Known.Fraction(0.3f)),
                }),
                new StyleRule(All(placement, Finding(FindingKind.Passes)), new Style { FillPaint = Option.Some<Paint>(new Paint.Token(LibraryPalette.Next)) }),
                new StyleRule(All(placement, Finding(FindingKind.Warns)), new Style { FillPaint = Option.Some<Paint>(new Paint.Token(LibraryPalette.Warn)) }),
                new StyleRule(All(placement, Finding(FindingKind.WillFail)), new Style { FillPaint = Option.Some<Paint>(new Paint.Token(LibraryPalette.Fail)) }),
                new StyleRule(Finding(FindingKind.WillFail), new Style
                {
                    Effect = Option.Some<EffectLook>(new EffectLook.Steady()),
                    Enter = Option.Some<EnterLook>(new EnterLook.None()),
                }),
            };
            return new StyleLayerDescriptor(StyleLayerId.Known("blueprint"), StyleBand.Theme, 0, ValueList.From(rules))
            {
                Palette = Option.Some(new Palette(PaletteId.Known("blueprint"), ValueMap.From(colours))),
            };
        }
    }

    /// <summary>Each placed ghost's step number in its plan's build order (View band, off until enabled).</summary>
    public static StyleLayerDescriptor StepNumbers => Labels("step-numbers", 0, new StyleMatch.Part(PartKind.Placement), new LabelText.StepNumber(), 40);

    /// <summary>Each placed ghost's piece count in its plan's bill (View band, off until enabled).</summary>
    public static StyleLayerDescriptor BillCounts => Labels("bill-counts", 1, new StyleMatch.Part(PartKind.Placement), new LabelText.BillCount(), 30);

    /// <summary>The reason of each failing or warning ghost, without hovering it (View band, off until enabled).</summary>
    public static StyleLayerDescriptor Reasons => Labels("reasons", 2,
        new StyleMatch.Any(ValueList.From(new[] { Finding(FindingKind.WillFail), Finding(FindingKind.Warns) })), new LabelText.FindingReason(), 60);

    /// <summary>Each placed ghost's plan name (View band, off until enabled).</summary>
    public static StyleLayerDescriptor PlanNames => Labels("plan-names", 3, new StyleMatch.Part(PartKind.Placement), new LabelText.PlanTitle(), 20);

    private static StyleLayerDescriptor Labels(string id, int order, StyleMatch match, LabelText says, int priority) =>
        new(StyleLayerId.Known(id), StyleBand.View, order, ValueList.Of(new StyleRule(match, new Style
        {
            Label = Option.Some<LabelLook>(new LabelLook.Shown(says, LibraryText.Default, LabelAnchor.Above, priority, Known.Metres(LabelRange))),
        })))
        {
            Enabled = false,
        };

    private static StyleMatch Finding(FindingKind kind) => new StyleMatch.Finding(kind, Option<ReasonCode>.None);

    private static StyleMatch All(StyleMatch a, StyleMatch b) => new StyleMatch.All(ValueList.From(new[] { a, b }));

    private static class Known
    {
        public static Fraction Fraction(float value) => Must(Api.Fraction.Create(value));

        public static Metres Metres(float value) => Must(Api.Metres.Create(value));

        private static T Must<T>(Result<T, Reason> result) where T : notnull =>
            result.Match(static v => v, static why => throw new System.InvalidOperationException(why.ToString()));
    }
}

/// <summary>Unity's named colours the game's construction cursor uses (Wireframe.cs:28, InventoryManager.cs:2900-2932 in the game's decompile).</summary>
internal static class GameColours
{
    /// <summary><c>Color.blue</c>: blueprints.</summary>
    public static Rgba Blue => new(0, 0, 255, 255);

    /// <summary><c>Color.green</c>: placeable.</summary>
    public static Rgba Green => new(0, 255, 0, 255);

    /// <summary><c>Color.red</c>: refused.</summary>
    public static Rgba Red => new(255, 0, 0, 255);

    /// <summary><c>Color.yellow</c> (1, 0.92, 0.016): joins a network.</summary>
    public static Rgba Yellow => new(255, 235, 4, 255);
}

/// <summary>Sets Lattice's library styles: drawn as selected or hovered when a consumer fills them, unused otherwise.</summary>
public static class LibrarySets
{
    /// <summary>Selected ghosts (edge <c>select</c>, glow).</summary>
    public static readonly StyleSetId Selected = StyleSetId.Known("selected");

    /// <summary>The hovered ghost (glow easing in).</summary>
    public static readonly StyleSetId Hovered = StyleSetId.Known("hovered");
}

/// <summary>Lattice's text styles.</summary>
public static class LibraryText
{
    /// <summary>The default label style: a TextMeshPro font the game ships.</summary>
    public static readonly TextStyleKey Default = TextStyleKey.Known("default");
}

/// <summary>Lattice's glyph icons, drawn through the label system; image icons are registered by consumers.</summary>
public static class LibraryIcons
{
    /// <summary>"!": will fail.</summary>
    public static readonly IconKey Fails = IconKey.Known("fails");

    /// <summary>"⚠": warns.</summary>
    public static readonly IconKey Warns = IconKey.Known("warns");

    /// <summary>"×": will be taken down.</summary>
    public static readonly IconKey Remove = IconKey.Known("remove");

    /// <summary>"⇄": will be replaced.</summary>
    public static readonly IconKey Swap = IconKey.Known("swap");

    /// <summary>"✎": will be painted, named or set.</summary>
    public static readonly IconKey Finish = IconKey.Known("finish");

    /// <summary>"?": something different is there.</summary>
    public static readonly IconKey Stale = IconKey.Known("stale");

    /// <summary>"…": an executor waits.</summary>
    public static readonly IconKey Waiting = IconKey.Known("waiting");

    /// <summary>"⏸": an executor paused.</summary>
    public static readonly IconKey Paused = IconKey.Known("paused");
}

/// <summary>
/// Lattice's named materials, which consumers read (Lattice.Api.Unity's <c>IGhostDrawing.Material</c>) so their own
/// overlays match the ghosts, and may override.
/// <para>
/// The material contract: what every material Lattice draws with (its own or an override) gets. Frozen like a member
/// once published.
/// </para>
/// <list type="bullet">
/// <item>Drawn with <c>Graphics.DrawMeshInstanced</c>, so it must support GPU instancing (an override without
/// <c>enableInstancing</c> is refused at the call). Values are set per draw call through a
/// <c>MaterialPropertyBlock</c>, one style per batch, never per instance. Lattice's own materials draw through
/// <c>Graphics.DrawMeshInstancedProcedural</c> from Lattice's own copies (keyword <c>LATTICE_PROCEDURAL</c>); the
/// instance returned by <c>Material(key)</c> and every override draw instanced.</item>
/// <item>Always set: <c>_Color</c>, the final colour with opacity; eases, effects and dim already applied on the CPU.
/// A material that reads only <c>_Color</c> looks right.</item>
/// <item>Also set, for shaders that declare them: <c>_LatticePattern</c> (0 none, 1 hatch, 2 cross-hatch, 3 dots),
/// <c>_LatticePatternColor</c>, <c>_LatticeEffect</c> (x kind: 0 steady, 1 pulse, 2 blink; y period in seconds; z the
/// low opacity or the duty; w phase in seconds), <c>_LatticeTime</c> (seconds, unscaled), <c>_LatticeSeeThrough</c>
/// (x strength, y range in metres, z the share of the strength a face seen square-on shows), and on edge materials
/// <c>_LatticeEdge</c> (x line style: 0 solid, 1 dashed, 2 dotted; y width in pixels).</item>
/// <item>The clip, as globals: <c>_LatticeClipPlane</c> (xyz unit normal, w = dot(normal, a point on the plane); a
/// point p is beyond when dot(normal, p) &gt; w) or <c>_LatticeClipMin</c> and <c>_LatticeClipMax</c> (beyond is
/// outside the box), with <c>_LatticeClipMode</c> (0 none, 1 plane, 2 box) and <c>_LatticeBeyond</c> (x mode: 0 drawn,
/// 1 hidden, 2 dim, 3 wire; y the dim fraction). Set once a frame before any camera renders and not reset after
/// Lattice's pass, so another mod's shader reads them all frame. The global <c>_LatticeBeyond</c> is the viewer's
/// default; Lattice also sets it per draw call, so a style's beyond-clip look overrides it for Lattice's own
/// batches.</item>
/// <item>Render queue: Lattice's draw order values; an override keeps its own queue if it sets one.</item>
/// <item>Built-structure overlays (Existing parts) draw like placements, with the materials their look names, from
/// Lattice's copy of each material at the Overlays draw order (an override's copy is made at the <c>Override</c> call;
/// Lattice never writes to a consumer's material). A shader that wants no z-fight over the original declares
/// <c>_LatticeOffsetFactor</c> and <c>_LatticeOffsetUnits</c> (<c>Offset [_LatticeOffsetFactor],
/// [_LatticeOffsetUnits]</c>); Lattice sets them on its overlay copies and leaves them 0 for placements.</item>
/// </list>
/// <para>
/// Where an override's shader cannot pattern or clip, Lattice falls back on the CPU: patterns drop to the edge style,
/// and the clip moves things between inside and beyond batches in the drawing budget, a few frames behind a moving
/// clip. Built-structure overlays and picking read only Lattice's own data (catalogue meshes and footprints, the
/// world's structure refs), never a structure's renderer state, materials or batch membership; an overlay draws with a
/// depth offset and tests depth against the opaque world only, so it shows over a faded (translucent, no depth write)
/// or hidden original with no z-fight.
/// </para>
/// </summary>
public static class MaterialKeys
{
    /// <summary>The translucent ghost fill.</summary>
    public static readonly MaterialKey Ghost = MaterialKey.Known("ghost");

    /// <summary>An opaque fill.</summary>
    public static readonly MaterialKey Solid = MaterialKey.Known("solid");

    /// <summary>Mesh edges.</summary>
    public static readonly MaterialKey Wire = MaterialKey.Known("wire");

    /// <summary>Outlines.</summary>
    public static readonly MaterialKey Edge = MaterialKey.Known("edge");

    /// <summary>The pass that shows things through walls and terrain.</summary>
    public static readonly MaterialKey SeeThrough = MaterialKey.Known("see-through");

    /// <summary>Glows.</summary>
    public static readonly MaterialKey Glow = MaterialKey.Known("glow");

    /// <summary>Labels and glyph icons.</summary>
    public static readonly MaterialKey Label = MaterialKey.Known("label");

    /// <summary>
    /// An instanced translucent material for a consumer's faded built pieces: reads <c>_Color</c> and the clip globals,
    /// writes no depth, drawn at the faded-world draw order. Lattice ships it and fades nothing itself.
    /// </summary>
    public static readonly MaterialKey Faded = MaterialKey.Known("faded");
}

/// <summary>
/// Lattice's piece families, found by type in the catalogue, so a modded subclass inherits its base's family. Families
/// form a tree: <see cref="Window"/> lies under <see cref="Wall"/>, the others have no parent
/// (<see cref="IPieceCatalogue.ParentOf"/>), and <see cref="StyleMatch.Family"/> holds for a family and every family
/// under it.
/// </summary>
public static class PieceFamilies
{
    /// <summary>Cables.</summary>
    public static readonly PieceFamily Cable = PieceFamily.Known("cable");

    /// <summary>Pipes, gas and liquid.</summary>
    public static readonly PieceFamily Pipe = PieceFamily.Known("pipe");

    /// <summary>Chutes.</summary>
    public static readonly PieceFamily Chute = PieceFamily.Known("chute");

    /// <summary>Frames.</summary>
    public static readonly PieceFamily Frame = PieceFamily.Known("frame");

    /// <summary>Walls and other faces of a large cell; <see cref="Window"/> lies under it, so a window is a wall too.</summary>
    public static readonly PieceFamily Wall = PieceFamily.Known("wall");

    /// <summary>
    /// Windows, under <see cref="Wall"/>: the game's see-through faces that seal air once built (their last build step
    /// blocks air), and a mod's subclass of them by its own build steps. A see-through face that never seals, such as a
    /// floor grating, stays a wall; one with no build steps is a window. Added in contract 0.7.
    /// </summary>
    public static readonly PieceFamily Window = PieceFamily.Known("window");

    /// <summary>Devices: pieces with ports.</summary>
    public static readonly PieceFamily Device = PieceFamily.Known("device");

    /// <summary>Pieces Lattice cannot classify.</summary>
    public static readonly PieceFamily Other = PieceFamily.Known("other");
}
