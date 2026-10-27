namespace Lattice.Api;

/// <summary>
/// A description of a set of ghosts: the same value reads the board and drives bulk commands.
/// A later minor may add cases; Lattice refuses a case it does not know with <see cref="ReasonCodes.UnknownKind"/>.
/// </summary>
public abstract record Selector
{
    private Selector() { }

    /// <summary>Every ghost: the whole board as the reading actor sees it, every plan included.</summary>
    public sealed record Everything : Selector;

    /// <summary>These ghosts.</summary>
    /// <param name="Values">The ids.</param>
    public sealed record Ids(ValueList<GhostId> Values) : Selector;

    /// <summary>Ghosts whose cell is in the box between two corners (inclusive, any order).</summary>
    /// <param name="Corner">One corner.</param>
    /// <param name="Opposite">The opposite corner.</param>
    public sealed record Box(Cell Corner, Cell Opposite) : Selector;

    /// <summary>The run of cable, pipe or chute ghosts through a cell, between junctions.</summary>
    /// <param name="Cell">A cell on the run.</param>
    public sealed record RunThrough(Cell Cell) : Selector;

    /// <summary>
    /// The planned network joined to a run piece at a cell: the build and swap ghosts of run pieces (cable, pipe or
    /// chute: no end with a port role) joined end to end to the run piece at the cell, and through each other, by the
    /// network kinds of that piece's ends, through junctions. It includes the piece at the cell; when the cell holds
    /// several run ghosts (a cable and a pipe), each starts a walk by its own kinds. It stops at device pieces and at
    /// built pieces: it reads ghosts only, through the board's index, and never scans the world. A cell with no run
    /// ghost picks nothing, as <see cref="RunThrough"/>. Removal and finish ghosts have no shape and are never picked;
    /// to select a built run use the <see cref="StructureSelector"/> cases. Added in contract 0.15.
    /// </summary>
    /// <param name="Cell">A cell holding a planned run piece.</param>
    public sealed record Joined(Cell Cell) : Selector;

    /// <summary>Ghosts on a network.</summary>
    /// <param name="Of">The network.</param>
    public sealed record Network(NetworkRef Of) : Selector;

    /// <summary>Ghosts in or bounding a room.</summary>
    /// <param name="Of">The room.</param>
    public sealed record Room(RoomRef Of) : Selector;

    /// <summary>Ghosts in a group.</summary>
    /// <param name="Name">The group.</param>
    public sealed record Group(GroupName Name) : Selector;

    /// <summary>Ghosts an actor owns: the ghosts it placed (<see cref="Ghost.PlacedBy"/>).</summary>
    /// <param name="Actor">The actor.</param>
    public sealed record Owner(Actor Actor) : Selector;

    /// <summary>Ghosts made by a maker.</summary>
    /// <param name="Maker">The maker.</param>
    public sealed record MadeBy(Maker Maker) : Selector;

    /// <summary>Ghosts in a plan.</summary>
    /// <param name="Plan">The plan.</param>
    public sealed record InPlan(PlanId Plan) : Selector;

    /// <summary>Ghosts carrying attached data under a key, with the given value if one is given.</summary>
    /// <param name="Key">The key.</param>
    /// <param name="Value">The value to match, or none for any value.</param>
    public sealed record Tagged(AttachmentKey Key, Option<Attachment> Value) : Selector;

    /// <summary>Ghosts of a piece (their build or swap target).</summary>
    /// <param name="Piece">The piece.</param>
    public sealed record OfPiece(PrefabKey Piece) : Selector;

    /// <summary>Ghosts every selector picks (and).</summary>
    /// <param name="Of">The selectors.</param>
    public sealed record All(ValueList<Selector> Of) : Selector;

    /// <summary>Ghosts any selector picks (or).</summary>
    /// <param name="Of">The selectors.</param>
    public sealed record Any(ValueList<Selector> Of) : Selector;
}

/// <summary>
/// A description of a set of built structures: the shapes of <see cref="Selector"/> that make sense in the world.
/// A later minor may add cases; Lattice refuses a case it does not know with <see cref="ReasonCodes.UnknownKind"/>.
/// </summary>
public abstract record StructureSelector
{
    private StructureSelector() { }

    /// <summary>These structures.</summary>
    /// <param name="Values">The structures.</param>
    public sealed record Refs(ValueList<StructureRef> Values) : StructureSelector;

    /// <summary>Structures whose cell is in the box between two corners (inclusive, any order).</summary>
    /// <param name="Corner">One corner.</param>
    /// <param name="Opposite">The opposite corner.</param>
    public sealed record Box(Cell Corner, Cell Opposite) : StructureSelector;

    /// <summary>The run of cable, pipe or chute pieces through a cell, between junctions.</summary>
    /// <param name="Cell">A cell on the run.</param>
    public sealed record RunThrough(Cell Cell) : StructureSelector;

    /// <summary>The pieces of a network.</summary>
    /// <param name="Of">The network.</param>
    public sealed record Network(NetworkRef Of) : StructureSelector;

    /// <summary>The structures in or bounding a room.</summary>
    /// <param name="Of">The room.</param>
    public sealed record Room(RoomRef Of) : StructureSelector;

    /// <summary>Structures of a piece.</summary>
    /// <param name="Piece">The piece.</param>
    public sealed record OfPiece(PrefabKey Piece) : StructureSelector;

    /// <summary>Structures every selector picks (and).</summary>
    /// <param name="Of">The selectors.</param>
    public sealed record All(ValueList<StructureSelector> Of) : StructureSelector;

    /// <summary>Structures any selector picks (or).</summary>
    /// <param name="Of">The selectors.</param>
    public sealed record Any(ValueList<StructureSelector> Of) : StructureSelector;
}
