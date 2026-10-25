namespace Lattice.Api;

/// <summary>
/// The ghosts of one world, as one viewer may see them (the installed access policy filters every read).
/// Main thread. Results are cached per revision: a repeated read on an unchanged board allocates nothing.
/// Lattice implements this; consumers call it. A minor may add members, so consumer fakes of it are not covered by
/// compatibility.
/// </summary>
public interface IGhostBoard
{
    /// <summary>The board's revision.</summary>
    Revision Revision { get; }

    /// <summary>A ghost by id. Constant cost.</summary>
    Option<Ghost> Find(GhostId id);

    /// <summary>The ghosts a selector picks; refused for a selector kind this version does not know. Cost: linear in the result, through indices.</summary>
    Result<ValueList<Ghost>, Reason> Select(Selector selector);

    /// <summary>The ghosts in a cell. Constant cost.</summary>
    ValueList<Ghost> At(Cell cell);

    /// <summary>
    /// The ghosts that point at a built structure (remove, swap, finish, or started on it): matched by the structure's
    /// reference id, else by its piece and cell, so a structure the game gave a new id is still found. Constant cost.
    /// </summary>
    ValueList<Ghost> On(StructureRef structure);

    /// <summary>The same board as another actor may see it.</summary>
    IGhostBoard As(Actor viewer);

    /// <summary>
    /// Every group that holds a ghost this viewer may see, with how many such ghosts it holds, ordered by name
    /// (ordinal). Read from the board's group index; cached per revision, so a repeated read on an unchanged board
    /// allocates nothing. A change shows as a new <see cref="Revision"/> and an <see cref="IGhostEvents.Changed"/> event.
    /// Cost after a change: linear in the ghosts that are in groups. Added in contract 0.5: read
    /// <see cref="IGhostSystem.Version"/> first.
    /// </summary>
    ValueList<GroupCount> Groups { get; }
}

/// <summary>A group and how many ghosts in it the viewer may see. Added in contract 0.5.</summary>
/// <param name="Name">The group.</param>
/// <param name="Count">How many of its ghosts the viewer may see, 1 or more.</param>
public sealed record GroupCount(GroupName Name, int Count);

/// <summary>
/// The built world as consumers read it: structures, rooms and networks, plus which structures are only assumed
/// (ghosts treated as built, during validation). Lattice implements this; consumers call it. A minor may add members.
/// </summary>
public interface IWorldQuery
{
    /// <summary>A structure by id. Constant cost.</summary>
    Option<StructureInfo> Structure(ReferenceId id);

    /// <summary>The structures in a cell. Constant cost.</summary>
    ValueList<StructureInfo> At(Cell cell);

    /// <summary>The structures a selector picks; refused for a selector kind this version does not know.</summary>
    Result<ValueList<StructureRef>, Reason> Select(StructureSelector selector);

    /// <summary>The network a structure is on, if any.</summary>
    Option<NetworkRef> NetworkOf(ReferenceId id);

    /// <summary>The room a cell is in, if any.</summary>
    Option<RoomRef> RoomAt(Cell cell);

    /// <summary>The ghost a structure stands for when it is only assumed built; none for real structures.</summary>
    Option<GhostId> AssumedFrom(StructureRef structure);
}

/// <summary>A built (or assumed) structure as the world shows it.</summary>
/// <param name="Ref">The structure.</param>
/// <param name="Turn">Its turn.</param>
/// <param name="State">Being built at a step, fully built, or broken.</param>
/// <param name="Colour">Its paint, if painted.</param>
/// <param name="Name">Its name, if named.</param>
/// <param name="Setting">Its labeller setting, if it has one.</param>
public sealed record StructureInfo(
    StructureRef Ref,
    Turn Turn,
    BuildState State,
    Option<PaintColour> Colour,
    Option<ThingName> Name,
    Option<Setting> Setting);

/// <summary>How far a structure is built. Union: new states come with a new contract minor.</summary>
public abstract record BuildState
{
    private BuildState() { }

    /// <summary>Not yet at its last build step.</summary>
    /// <param name="At">The step it is at.</param>
    public sealed record Building(StepIndex At) : BuildState;

    /// <summary>At its last build step.</summary>
    public sealed record Built : BuildState;

    /// <summary>
    /// Broken by damage: it stands but cannot be repaired, and taking it apart gives nothing back.
    /// </summary>
    public sealed record Broken : BuildState;
}

/// <summary>A structure the game moved (rocket parts detached and re-attached, for example).</summary>
/// <param name="From">The structure where it was.</param>
/// <param name="To">Its new cell.</param>
/// <param name="Turn">Its new turn.</param>
public sealed record StructureMove(StructureRef From, Cell To, Turn Turn);
