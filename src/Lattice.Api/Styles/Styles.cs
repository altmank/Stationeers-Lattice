using System;

namespace Lattice.Api;

/// <summary>
/// How ghosts look to the local viewer: layers of style rules, sets, highlights, drafts, the viewer's clip, picking and
/// the story behind a look. Everything here is the local process's: it never syncs, never saves and never changes what
/// another player or agent sees (what everyone should see alike is ghost data a rule reads: tint, label, groups,
/// attachments). Main thread. Results are <see cref="Result{T, E}"/>; registrations end with the world. Styles are data
/// Lattice evaluates itself, so a consumer's style is never timed, checked or switched off; it changes one viewer's
/// picture only. Runs on every seat (module <c>styles</c>); drawing is module <c>render</c>, off in batch mode
/// (dedicated server, headless clients), where registrations are still accepted and held, and <see cref="Pick"/> and
/// <see cref="Explain"/> still answer. Cost: a look is resolved per signature (the facts it depends on), not per ghost.
/// Toggle layers; do not re-add them: adding, replacing or removing a layer re-signatures every drawn thing in
/// drawing-budget slices. Added in contract 0.2. Lattice implements this; consumers call it. A minor may add members:
/// to fake it in a test, inherit <c>Lattice.Testing.Stubs.StubGhostStyles</c>, which gains every added member in the
/// same release.
/// </summary>
public interface IGhostStyles
{
    /// <summary>
    /// Adds a layer of rules, with an optional palette and motion, in a band. Refused (<c>invalid-value</c>) for a
    /// <c>default</c> id, a band this contract does not name or a palette base that is not a library palette, and
    /// (<c>already-registered</c>) for an id already added. Cost: every drawn thing is re-signatured in drawing-budget slices.
    /// </summary>
    Result<IStyleLayer, Reason> Add(StyleLayerDescriptor layer);

    /// <summary>
    /// The ghost set with this id, made empty on first use; the same set for every caller that names it. Refused
    /// (<c>invalid-value</c>) for a <c>default</c> id. Constant cost.
    /// </summary>
    Result<IStyleSet, Reason> Set(StyleSetId id);

    /// <summary>
    /// The structure set with this id: built structures drawn as overlays while they are members. Made empty on first
    /// use; the same set for every caller that names it. Refused (<c>invalid-value</c>) for a <c>default</c> id.
    /// </summary>
    Result<IStructureSet, Reason> StructureSet(StyleSetId id);

    /// <summary>
    /// Draws targets with a style in the highlight band for a time, then ends itself; disposing ends it early. A selector
    /// is resolved once, at the call. Refused (<c>invalid-value</c>) for a zero duration. Cost: the targets.
    /// </summary>
    Result<IDisposable, Reason> Highlight(HighlightRequest request);

    /// <summary>
    /// The drafts handle with this id (one stream of uncommitted ghosts, such as the piece at the cursor), made on
    /// first use. Refused (<c>invalid-value</c>) for a <c>default</c> id.
    /// </summary>
    Result<IDrafts, Reason> Drafts(DraftsId id);

    /// <summary>
    /// Installs a viewer clip. The last installed clip applies; disposing it brings back the one under it. Drawing
    /// applies the clip per draw call, never re-signaturing; <see cref="Pick"/> honours it. Constant cost.
    /// </summary>
    Result<IClip, Reason> Clip(ClipShape shape);

    /// <summary>The clip that applies now, whoever installed it; none without a clip. Constant cost.</summary>
    Option<ClipShape> InForce { get; }

    /// <summary>
    /// The clip in force changed (installed, moved, disposed): raised on the main thread, at most once a frame, with
    /// the clip that applies then. A handler's failure is its own mod's to handle.
    /// </summary>
    event Action<Option<ClipShape>> ClipChanged;

    /// <summary>
    /// The nearest drawn, pickable ghost or draft along a ray: one box per cell of the piece's catalogue footprint,
    /// clipped to the piece's bounds; the nearest entry distance wins, then the smaller box, then the lower id. Hidden
    /// and unpickable things are never hit. Reads only Lattice's own data (catalogue footprints and bounds), never a
    /// structure's renderers. Built structures are picked with the game's own raycast. Cost: the chunks the ray
    /// crosses, then the boxes in them.
    /// </summary>
    Option<PickHit> Pick(PickRay ray, PickOptions options);

    /// <summary>
    /// <see cref="Pick"/> without allocating, for a pick every frame the pointer moves: the same hit, as a struct.
    /// True with the hit; false with <c>default</c> when nothing is hit. A draft's trial ghost is left out (ask
    /// <see cref="Pick"/> for it). Same cost as <see cref="Pick"/>; allocates nothing. Added in contract 0.4.
    /// </summary>
    bool TryPick(PickRay ray, PickOptions options, out PickResult hit);

    /// <summary>
    /// Why a drawn ghost looks as it does: per property, the value and the rule that set it, and the facets.
    /// Refused (<c>not-found</c>) for a ghost the viewer is not shown. Cost: one resolve.
    /// </summary>
    Result<StyleStory, Reason> Explain(GhostId ghost);

    /// <summary>
    /// Whether a match holds for a built structure: its piece's facets (<see cref="StyleMatch.Family"/>,
    /// <see cref="Selector.OfPiece"/> in <see cref="StyleMatch.Picks"/>), <see cref="StyleMatch.Facing"/> by the turn
    /// Lattice's copy of the world holds, its structure-set membership and
    /// <see cref="StyleMatch.Structure"/>; ghost-only cases (state, finding, status, draft, mine, tinted) are false.
    /// The same family vocabulary as ghost styles, modded pieces included. Constant cost after the first ask per piece
    /// and match.
    /// </summary>
    bool Matches(StyleMatch match, StructureRef structure);
}

/// <summary>A cascade band, lowest first. Lattice's own Library (below all) and Inspect (above all) are not nameable. Values are fixed for the life of the contract.</summary>
public enum StyleBand
{
    /// <summary>Mods adding meaning (an executor's working look, an add-on's icon).</summary>
    Meaning = 2,

    /// <summary>The viewer's theme: restyles states, palette, motion.</summary>
    Theme = 3,

    /// <summary>Filters, hiding, planning mode, others' ghosts.</summary>
    View = 4,

    /// <summary>Hover and selection.</summary>
    Selection = 5,

    /// <summary>Timed highlights.</summary>
    Highlight = 6,
}

/// <summary>How much things move. The last enabled layer that states one wins; Lattice's default is <see cref="Full"/>. Values are fixed for the life of the contract.</summary>
public enum Motion
{
    /// <summary>Every ease and effect plays.</summary>
    Full = 1,

    /// <summary>Pulses, blinks and flashes are drawn steady; eases are capped at 100 ms.</summary>
    Reduced = 2,

    /// <summary>As reduced, and every ease is 0.</summary>
    Off = 3,
}

/// <summary>One rule: where the match holds, the style's opinions apply.</summary>
/// <param name="Match">What it targets.</param>
/// <param name="Style">The opinions.</param>
public sealed record StyleRule(StyleMatch Match, Style Style);

/// <summary>
/// Colours for paint tokens. The palette in force is Lattice's default overlaid, key by key, by the palette of each
/// enabled layer in cascade order; a palette with a <see cref="Base"/> lays that library palette down first, then its
/// own colours.
/// </summary>
/// <param name="Id">The palette's id.</param>
/// <param name="Colours">A colour per token.</param>
public sealed record Palette(PaletteId Id, ValueMap<PaletteKey, Rgba> Colours)
{
    /// <summary>Owner colours, picked by an owner's id (<see cref="Paint.OwnerColour"/>); empty keeps the ramp below.</summary>
    public ValueList<Rgba> OwnerRamp { get; init; } = ValueList<Rgba>.Empty;

    /// <summary>
    /// A library palette (<see cref="LibraryPalettes"/>) laid down under this palette's own colours and owner ramp, so a
    /// theme can take colour-safe or high contrast whole and change a few tokens; none for no base. A layer whose palette
    /// names a base Lattice does not hold is refused at <see cref="IGhostStyles.Add"/> (<c>invalid-value</c>).
    /// </summary>
    public Option<PaletteId> Base { get; init; }
}

/// <summary>
/// A layer of rules. For each property of each drawn thing, every rule of every enabled layer whose match holds is
/// ordered by (band, <see cref="Order"/>, registration, rule position), and the last one that sets the property wins.
/// No specificity: order alone decides, so <see cref="IGhostStyles.Explain"/> names one rule.
/// </summary>
/// <param name="Id">The layer's id.</param>
/// <param name="Band">Its band.</param>
/// <param name="Order">Its place within the band, ascending.</param>
/// <param name="Rules">Its rules, in order.</param>
public sealed record StyleLayerDescriptor(StyleLayerId Id, StyleBand Band, int Order, ValueList<StyleRule> Rules)
{
    /// <summary>A palette the layer adds while it is enabled; none for none.</summary>
    public Option<Palette> Palette { get; init; }

    /// <summary>The motion the layer states while it is enabled; none for no opinion.</summary>
    public Option<Motion> Motion { get; init; }

    /// <summary>Whether the layer starts enabled (default true).</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// An added layer. Enabling and disabling cost one resolve per live signature; disposing removes it (a re-signature of
/// every drawn thing). Main thread. Lattice implements this; consumers call it. A minor may add members: to fake it in
/// a test, inherit <c>Lattice.Testing.Stubs.StubStyleLayer</c>, which gains every added member in the same release.
/// </summary>
public interface IStyleLayer : IDisposable
{
    /// <summary>The layer's id.</summary>
    StyleLayerId Id { get; }

    /// <summary>Whether its rules, palette and motion apply now.</summary>
    bool Enabled { get; }

    /// <summary>Its rules apply. Enabling an enabled layer does nothing.</summary>
    void Enable();

    /// <summary>Its rules stop applying. Disabling a disabled layer does nothing.</summary>
    void Disable();
}

/// <summary>
/// A set of ghost ids a rule matches with <see cref="StyleMatch.InSet"/> (hover, selection, a mod's own). Ids of ghosts
/// that leave the board leave the set. Each change costs the ghosts entering and leaving. Main thread. Lattice
/// implements this; consumers call it. A minor may add members: to fake it in a test, inherit
/// <c>Lattice.Testing.Stubs.StubStyleSet</c>, which gains every added member in the same release.
/// </summary>
public interface IStyleSet
{
    /// <summary>The set's id.</summary>
    StyleSetId Id { get; }

    /// <summary>How many ghosts are in it.</summary>
    int Count { get; }

    /// <summary>Whether a ghost is in it. Constant cost.</summary>
    bool Contains(GhostId ghost);

    /// <summary>The set becomes this one ghost (hover). Constant cost.</summary>
    void Put(GhostId ghost);

    /// <summary>The set becomes these ghosts (selection).</summary>
    void Replace(ValueList<GhostId> ghosts);

    /// <summary>Adds ghosts.</summary>
    void Add(ValueList<GhostId> ghosts);

    /// <summary>Removes ghosts.</summary>
    void Remove(ValueList<GhostId> ghosts);

    /// <summary>Empties the set.</summary>
    void Clear();
}

/// <summary>
/// A set of built structures drawn as overlays while they are members (box select of built things). Overlays read only
/// Lattice's own data: never a structure's renderer state, materials or batch membership. Main thread. Lattice
/// implements this; consumers call it. A minor may add members: to fake it in a test, inherit
/// <c>Lattice.Testing.Stubs.StubStructureSet</c>, which gains every added member in the same release.
/// </summary>
public interface IStructureSet
{
    /// <summary>The set's id.</summary>
    StyleSetId Id { get; }

    /// <summary>How many structures are in it.</summary>
    int Count { get; }

    /// <summary>Whether a structure is in it (by id, else by piece and cell). Constant cost.</summary>
    bool Contains(StructureRef structure);

    /// <summary>The set becomes this one structure (hover). Constant cost.</summary>
    void Put(StructureRef structure);

    /// <summary>The set becomes these structures.</summary>
    void Replace(ValueList<StructureRef> structures);

    /// <summary>Adds structures.</summary>
    void Add(ValueList<StructureRef> structures);

    /// <summary>Removes structures.</summary>
    void Remove(ValueList<StructureRef> structures);

    /// <summary>Empties the set.</summary>
    void Clear();
}

/// <summary>What a highlight draws.</summary>
public abstract record HighlightTargets
{
    private HighlightTargets() { }

    /// <summary>These ghosts.</summary>
    /// <param name="Ids">The ghosts.</param>
    public sealed record Ghosts(ValueList<GhostId> Ids) : HighlightTargets;

    /// <summary>The ghosts a selector picks at the call, on the viewer's board.</summary>
    /// <param name="Which">The selector.</param>
    public sealed record Picked(Selector Which) : HighlightTargets;

    /// <summary>These built structures, as overlays.</summary>
    /// <param name="Refs">The structures.</param>
    public sealed record Structures(ValueList<StructureRef> Refs) : HighlightTargets;
}

/// <summary>A timed highlight: targets drawn with a style in the highlight band for a time.</summary>
/// <param name="Targets">What it draws.</param>
/// <param name="Style">The opinions it adds.</param>
/// <param name="For">How long it lasts, more than zero.</param>
public sealed record HighlightRequest(HighlightTargets Targets, Style Style, Duration For);

/// <summary>
/// One stream of uncommitted ghosts a consumer shows (the piece at the cursor, a run being dragged), drawn with <see
/// cref="StyleMatch.Draft"/> facets. Each <c>Show</c> replaces everything the handle showed. Disposing clears it and
/// frees the id. Drafts end with the world. Main thread. Lattice implements this; consumers call it. A minor may add
/// members: to fake it in a test, inherit <c>Lattice.Testing.Stubs.StubDrafts</c>, which gains every added member in
/// the same release.
/// </summary>
public interface IDrafts : IDisposable
{
    /// <summary>The handle's id.</summary>
    DraftsId Id { get; }

    /// <summary>Draws raw drafts at once, in the call, as <see cref="FindingKind.Unchecked"/>. Cost: the drafts shown.</summary>
    void Show(ValueList<GhostDraft> drafts);

    /// <summary>Draws a preview's ghosts (its change set's added and changed ghosts, and the built half of its removals and swaps) with their findings.</summary>
    void Show(Preview preview);

    /// <summary>Draws nothing.</summary>
    void Clear();

    /// <summary>
    /// Hands the drafts over to a submission, with no frame where neither the drafts nor the applied ghosts are drawn:
    /// <see cref="Submission.Answered"/> and applied, they are dropped in the frame its change set's ghosts are first
    /// drawn; refused, they are dropped now. On <see cref="Submission.Sent"/> they turn <see cref="StyleMatch.Pending"/>
    /// until that ticket is answered, then go as for an answer.
    /// </summary>
    void Hand(Submission submission);
}

/// <summary>The viewer's clip, like a CAD section: things beyond it look as their <see cref="Style.BeyondClip"/> says.</summary>
public abstract record ClipShape
{
    private ClipShape() { }

    /// <summary>A plane; beyond is the side its normal points to.</summary>
    public sealed record Plane : ClipShape
    {
        private Plane(WorldPoint point, WorldVector normal)
        {
            Point = point;
            Normal = normal;
        }

        /// <summary>A point on the plane.</summary>
        public WorldPoint Point { get; }

        /// <summary>The normal, towards beyond; not normalised.</summary>
        public WorldVector Normal { get; }

        /// <summary>Refuses non-finite values and a zero normal.</summary>
        public static Result<Plane, Reason> Create(WorldPoint point, WorldVector normal) =>
            Measure.IsFinite(point) && Measure.IsFinite(normal) && !Measure.IsZero(normal)
                ? Result<Plane, Reason>.Ok(new Plane(point, normal))
                : Result<Plane, Reason>.Error(Reason.InvalidValue(nameof(Plane), "", "finite-with-a-normal"));
    }

    /// <summary>A box; beyond is outside it.</summary>
    public sealed record Box : ClipShape
    {
        private Box(WorldPoint min, WorldPoint max)
        {
            Min = min;
            Max = max;
        }

        /// <summary>The low corner.</summary>
        public WorldPoint Min { get; }

        /// <summary>The high corner.</summary>
        public WorldPoint Max { get; }

        /// <summary>A box between two opposite corners, in any order; refuses non-finite values.</summary>
        public static Result<Box, Reason> Create(WorldPoint corner, WorldPoint opposite) =>
            Measure.IsFinite(corner) && Measure.IsFinite(opposite)
                ? Result<Box, Reason>.Ok(new Box(
                    new WorldPoint(Math.Min(corner.X, opposite.X), Math.Min(corner.Y, opposite.Y), Math.Min(corner.Z, opposite.Z)),
                    new WorldPoint(Math.Max(corner.X, opposite.X), Math.Max(corner.Y, opposite.Y), Math.Max(corner.Z, opposite.Z))))
                : Result<Box, Reason>.Error(Reason.InvalidValue(nameof(Box), "", "finite"));
    }
}

/// <summary>
/// An installed clip. Main thread. Lattice implements this; consumers call it. A minor may add members: to fake it in a
/// test, inherit <c>Lattice.Testing.Stubs.StubClip</c>, which gains every added member in the same release.
/// </summary>
public interface IClip : IDisposable
{
    /// <summary>The clip's shape now.</summary>
    ClipShape Shape { get; }

    /// <summary>Moves the clip (every frame if need be). Constant cost: shader values, never a re-signature.</summary>
    void Move(ClipShape to);
}

/// <summary>A ray to pick along, in world space.</summary>
public readonly record struct PickRay
{
    private PickRay(WorldPoint origin, WorldVector direction, float maxMetres)
    {
        Origin = origin;
        Direction = direction;
        MaxMetres = maxMetres;
    }

    /// <summary>Where it starts.</summary>
    public WorldPoint Origin { get; }

    /// <summary>Which way it goes; not normalised.</summary>
    public WorldVector Direction { get; }

    /// <summary>How far it reaches.</summary>
    public float MaxMetres { get; }

    /// <summary>Refuses non-finite values, a zero direction and a reach of zero or less.</summary>
    public static Result<PickRay, Reason> Create(WorldPoint origin, WorldVector direction, float maxMetres) =>
        Measure.IsFinite(origin) && Measure.IsFinite(direction) && !Measure.IsZero(direction) && Measure.IsFinite(maxMetres) && maxMetres > 0f
            ? Result<PickRay, Reason>.Ok(new PickRay(origin, direction, maxMetres))
            : Result<PickRay, Reason>.Error(Reason.InvalidValue(nameof(PickRay), "", "finite-with-a-direction-and-reach"));
}

/// <summary>Whether a pick hits drafts. Values are fixed for the life of the contract.</summary>
public enum DraftPicks
{
    /// <summary>Ghosts and drafts.</summary>
    Include = 1,

    /// <summary>Ghosts only.</summary>
    Exclude = 2,

    /// <summary>Drafts only.</summary>
    Only = 3,
}

/// <summary>Whether a pick hits things beyond the viewer's clip. Values are fixed for the life of the contract.</summary>
public enum BeyondClipPicks
{
    /// <summary>Things beyond the clip are not hit.</summary>
    Exclude = 1,

    /// <summary>They are hit like any other.</summary>
    Include = 2,
}

/// <summary>Filters for <see cref="IGhostStyles.Pick"/>.</summary>
public sealed record PickOptions
{
    /// <summary>No filter: ghosts and drafts, nothing beyond the clip.</summary>
    public static PickOptions Default { get; } = new();

    /// <summary>Only things this matches (only cables, only placement parts), matched against the candidate's facets; none for anything.</summary>
    public Option<StyleMatch> Only { get; init; }

    /// <summary>Whether drafts are hit (default <see cref="DraftPicks.Include"/>).</summary>
    public DraftPicks Drafts { get; init; } = DraftPicks.Include;

    /// <summary>Whether things beyond the clip are hit (default <see cref="BeyondClipPicks.Exclude"/>).</summary>
    public BeyondClipPicks BeyondClip { get; init; } = BeyondClipPicks.Exclude;
}

/// <summary>What a pick hit: the box hit is one cell of the piece's footprint.</summary>
public abstract record PickHit
{
    private PickHit() { }

    /// <summary>A ghost on the board.</summary>
    /// <param name="Ghost">The ghost.</param>
    /// <param name="Part">Which part was hit.</param>
    /// <param name="Cell">The footprint cell hit.</param>
    /// <param name="Face">The face of that cell the ray entered.</param>
    /// <param name="Point">Where it entered.</param>
    /// <param name="Distance">How far along the ray, in metres.</param>
    public sealed record OnGhost(GhostId Ghost, PartKind Part, Cell Cell, Face Face, WorldPoint Point, float Distance) : PickHit;

    /// <summary>A draft. Drafts are not keyed by ghost id: a preview's ids are trial ids.</summary>
    /// <param name="Drafts">The drafts handle.</param>
    /// <param name="Index">Its position in what the handle last showed: the raw drafts, or the preview's change set's added ghosts.</param>
    /// <param name="Trial">The preview's ghost, with its trial id; none for a raw draft.</param>
    /// <param name="Part">Which part was hit.</param>
    /// <param name="Cell">The footprint cell hit.</param>
    /// <param name="Face">The face of that cell the ray entered.</param>
    /// <param name="Point">Where it entered.</param>
    /// <param name="Distance">How far along the ray, in metres.</param>
    public sealed record OnDraft(DraftsId Drafts, int Index, Option<Ghost> Trial, PartKind Part, Cell Cell, Face Face, WorldPoint Point, float Distance) : PickHit;
}

/// <summary>Which kind of thing <see cref="IGhostStyles.TryPick"/> hit. Values are fixed. Added in contract 0.4.</summary>
public enum PickKind
{
    /// <summary>A ghost on the board (<see cref="PickHit.OnGhost"/>).</summary>
    Ghost = 1,

    /// <summary>A draft (<see cref="PickHit.OnDraft"/>).</summary>
    Draft = 2,
}

/// <summary>
/// What <see cref="IGhostStyles.TryPick"/> hit, as a struct: the fields of <see cref="PickHit.OnGhost"/> or
/// <see cref="PickHit.OnDraft"/> without the draft's trial ghost. <c>default</c> is no hit (<see cref="Kind"/> 0).
/// Added in contract 0.4.
/// </summary>
/// <param name="Kind">A ghost or a draft.</param>
/// <param name="Ghost">The ghost hit; <c>default</c> for a draft.</param>
/// <param name="Drafts">The drafts handle hit; <c>default</c> for a ghost.</param>
/// <param name="Index">The draft's position in what its handle last showed; 0 for a ghost.</param>
/// <param name="Part">Which part was hit.</param>
/// <param name="Cell">The footprint cell hit.</param>
/// <param name="Face">The face of that cell the ray entered.</param>
/// <param name="Point">Where it entered.</param>
/// <param name="Distance">How far along the ray, in metres.</param>
public readonly record struct PickResult(PickKind Kind, GhostId Ghost, DraftsId Drafts, int Index, PartKind Part, Cell Cell, Face Face, WorldPoint Point, float Distance)
{
    /// <summary>The struct form of a union hit.</summary>
    public static PickResult Of(PickHit hit) => hit switch
    {
        PickHit.OnGhost g => new PickResult(PickKind.Ghost, g.Ghost, default, 0, g.Part, g.Cell, g.Face, g.Point, g.Distance),
        PickHit.OnDraft d => new PickResult(PickKind.Draft, default, d.Drafts, d.Index, d.Part, d.Cell, d.Face, d.Point, d.Distance),
        _ => default,
    };
}

/// <summary>Where a property's value came from.</summary>
public abstract record StyleSource
{
    private StyleSource() { }

    /// <summary>No rule set it: Lattice's default.</summary>
    public sealed record Default : StyleSource;

    /// <summary>Lattice's starter library.</summary>
    /// <param name="Rule">The rule's position in the library.</param>
    public sealed record Library(int Rule) : StyleSource;

    /// <summary>A consumer's layer.</summary>
    /// <param name="Band">Its band.</param>
    /// <param name="Id">The layer.</param>
    /// <param name="Rule">The rule's position in the layer.</param>
    public sealed record Layer(StyleBand Band, StyleLayerId Id, int Rule) : StyleSource;

    /// <summary>A timed highlight.</summary>
    public sealed record Highlight : StyleSource;

    /// <summary>Lattice's "why" overlay, above every band.</summary>
    public sealed record Inspect : StyleSource;
}

/// <summary>Why a ghost looks as it does.</summary>
/// <param name="Ghost">The ghost.</param>
/// <param name="Resolved">The look after the cascade, every property set.</param>
/// <param name="Sources">Where each property's value came from.</param>
/// <param name="Facets">The facets that hold for it, as atomic matches (state, finding, family, sets, ...).</param>
public sealed record StyleStory(GhostId Ghost, Style Resolved, ValueMap<StyleProperty, StyleSource> Sources, ValueList<StyleMatch> Facets);

/// <summary>Something Lattice draws.</summary>
public abstract record DrawnThing
{
    private DrawnThing() { }

    /// <summary>A ghost on the board.</summary>
    /// <param name="Id">The ghost.</param>
    public sealed record OfGhost(GhostId Id) : DrawnThing;

    /// <summary>A draft.</summary>
    /// <param name="Drafts">The drafts handle.</param>
    /// <param name="Index">Its position in what the handle last showed.</param>
    public sealed record OfDraft(DraftsId Drafts, int Index) : DrawnThing;

    /// <summary>A built structure in a structure set or a highlight.</summary>
    /// <param name="Structure">The structure.</param>
    public sealed record OfStructure(StructureRef Structure) : DrawnThing;
}
