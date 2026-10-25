using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Styles;

/// <summary>What kind of thing is drawn.</summary>
internal enum ThingKind
{
    /// <summary>A ghost on the viewer's board.</summary>
    Ghost = 1,

    /// <summary>A draft shown by a drafts handle.</summary>
    Draft = 2,

    /// <summary>A built structure in a structure set or a highlight.</summary>
    Structure = 3,
}

/// <summary>
/// One drawn thing: one part of a ghost (a swap draws its new piece and an overlay of its old one), one part of a draft,
/// or one built structure. Holds the facts its facets are read from, its signature, and where it sits in the draw plan
/// and the pick index. Made per change, never per frame.
/// </summary>
internal sealed class Thing
{
    private Thing(ThingKind kind, PartKind part, PrefabKey piece, Cell at, Turn turn)
    {
        Kind = kind;
        Part = part;
        Piece = piece;
        At = at;
        Turn = turn;
    }

    public ThingKind Kind { get; }

    public PartKind Part { get; }

    /// <summary>The piece drawn: the planned piece of a placement, the built piece of an overlay.</summary>
    public PrefabKey Piece { get; }

    /// <summary>The piece's own cell.</summary>
    public Cell At { get; }

    public Turn Turn { get; }

    /// <summary>The board's ghost, or a preview's trial ghost; null for a raw draft and a structure.</summary>
    public Ghost? Record { get; private set; }

    /// <summary>The raw draft; null otherwise.</summary>
    public GhostDraft? Raw { get; private init; }

    /// <summary>The drafts handle showing it; null for a ghost or a structure.</summary>
    public DraftsHandle? Drafts { get; private init; }

    /// <summary>Its position in what its drafts handle last showed.</summary>
    public int Index { get; private init; }

    /// <summary>The built structure an overlay draws; default for a placement.</summary>
    public StructureRef Structure { get; private init; }

    /// <summary>The placed shape of a placement part; null for an overlay or a piece the catalogue does not hold.</summary>
    public PlacedShape? Shape { get; private init; }

    public PieceFamily Family { get; set; }

    public Finding? Finding { get; set; }

    public ValueList<ExecutorReport> Statuses { get; set; } = ValueList<ExecutorReport>.Empty;

    /// <summary>A draft handed over with a submission not yet answered.</summary>
    public bool Pending { get; set; }

    public Signature Signature { get; set; } = null!;

    /// <summary>The thing's batch and its slot there; null while it is not in the draw plan.</summary>
    public Batch? Batch { get; set; }

    public int BatchSlot { get; set; }

    /// <summary>When its look last started to ease or play, as a draw plan frame; 0 for never.</summary>
    public long StartFrame { get; set; }

    /// <summary>Its pick boxes, one per footprint cell; null while it is not in the pick index.</summary>
    public PickBox[]? Boxes { get; set; }

    /// <summary>Its place in the styles' list of every drawn thing.</summary>
    public int ListSlot { get; set; } = -1;

    /// <summary>Whether it was removed from the styles; a removed thing is never drawn or picked again.</summary>
    public bool Gone { get; set; }

    /// <summary>The ghost's id, for a ghost; default otherwise.</summary>
    public GhostId Id => Record is not null && Kind == ThingKind.Ghost ? Record.Id : default;

    /// <summary>Its intent, for a ghost or a draft; null for a structure.</summary>
    public Intent? Intent => Record?.Intent ?? Raw?.Intent;

    public Option<Tint> Tint => Record is not null ? Record.Tint : Raw is not null ? Raw.Tint : Option<Tint>.None;

    public static Thing OfGhost(Ghost ghost, PartKind part, PrefabKey piece, Cell at, Turn turn, PlacedShape? shape, StructureRef structure) =>
        new(ThingKind.Ghost, part, piece, at, turn) { Record = ghost, Shape = shape, Structure = structure };

    public static Thing OfDraft(DraftsHandle drafts, int index, Ghost? trial, GhostDraft? raw, PartKind part, PrefabKey piece, Cell at, Turn turn, PlacedShape? shape, StructureRef structure) =>
        new(ThingKind.Draft, part, piece, at, turn) { Drafts = drafts, Index = index, Record = trial, Raw = raw, Shape = shape, Structure = structure };

    public static Thing OfStructure(StructureRef structure, Turn turn) =>
        new(ThingKind.Structure, PartKind.Existing, structure.Piece, structure.At, turn) { Structure = structure };

    /// <summary>Takes the board's newer record of the same ghost and part.</summary>
    public void Refresh(Ghost ghost) => Record = ghost;
}
