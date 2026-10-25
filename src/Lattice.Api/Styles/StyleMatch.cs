namespace Lattice.Api;

/// <summary>
/// What a style rule targets, evaluated per drawn thing from its facets. No predicate delegates: a mod with logic of
/// its own computes a set and pushes it (<see cref="IGhostStyles.Set"/>), so every style input is known to Lattice and
/// no consumer code runs per ghost. Union: new cases come with a contract minor; a case this Lattice does not know
/// never matches.
/// </summary>
public abstract record StyleMatch
{
    private StyleMatch() { }

    /// <summary>Every drawn thing.</summary>
    public sealed record Everything : StyleMatch;

    /// <summary>
    /// Ghosts a selector picks. Local cases (ids, box, group, owner, maker, plan, tag, piece) are decided per ghost;
    /// relational ones (network, room, run through) re-select when the board or world revision moves.
    /// </summary>
    /// <param name="Which">The selector.</param>
    public sealed record Picks(Selector Which) : StyleMatch;

    /// <summary>What the ghost asks for (a draft's intent; a structure overlay matches none).</summary>
    /// <param name="Kind">The intent kind.</param>
    public sealed record Intent(IntentKind Kind) : StyleMatch;

    /// <summary>Which part is drawn: the planned piece, or an overlay of a built one.</summary>
    /// <param name="Kind">The part.</param>
    public sealed record Part(PartKind Kind) : StyleMatch;

    /// <summary>The ghost's state on the board.</summary>
    /// <param name="Kind">The state.</param>
    public sealed record State(StateKind Kind) : StyleMatch;

    /// <summary>
    /// The ghost's finding in the cached validation answer, optionally only with one reason code. A pending answer
    /// reads as <see cref="FindingKind.Unchecked"/>; drawing never starts a check. Plan state's background checks cover
    /// every plan, so a finding's look appears without the viewer asking for a check.
    /// </summary>
    /// <param name="Kind">The finding kind.</param>
    /// <param name="Code">Only findings with this reason code; none for any.</param>
    public sealed record Finding(FindingKind Kind, Option<ReasonCode> Code) : StyleMatch;

    /// <summary>An executor's status on the ghost, optionally only one executor's.</summary>
    /// <param name="Kind">The status.</param>
    /// <param name="By">Only this executor's status; none for any.</param>
    public sealed record Status(StatusKind Kind, Option<ExecutorId> By) : StyleMatch;

    /// <summary>The family of the piece drawn (the target piece of a build, the built piece of an overlay).</summary>
    /// <param name="Of">The family.</param>
    public sealed record Family(PieceFamily Of) : StyleMatch;

    /// <summary>A member of a ghost set or a structure set.</summary>
    /// <param name="Set">The set.</param>
    public sealed record InSet(StyleSetId Set) : StyleMatch;

    /// <summary>Placed by the local viewer.</summary>
    public sealed record Mine : StyleMatch;

    /// <summary>Has a tint.</summary>
    public sealed record Tinted : StyleMatch;

    /// <summary>Its intent carries a finish (paint, name or setting): a finish-only ghost, or a build or swap with one.</summary>
    public sealed record Finishes : StyleMatch;

    /// <summary>Shown by a drafts handle, not on the board.</summary>
    public sealed record Draft : StyleMatch;

    /// <summary>A draft handed over with a submission the host has not answered yet.</summary>
    public sealed record Pending : StyleMatch;

    /// <summary>An overlay of a built structure in a structure set.</summary>
    public sealed record Structure : StyleMatch;

    /// <summary>Every one of these holds (none: holds).</summary>
    /// <param name="Of">The matches.</param>
    public sealed record All(ValueList<StyleMatch> Of) : StyleMatch;

    /// <summary>At least one of these holds (none: does not hold).</summary>
    /// <param name="Of">The matches.</param>
    public sealed record Any(ValueList<StyleMatch> Of) : StyleMatch;

    /// <summary>This does not hold.</summary>
    /// <param name="Match">The match.</param>
    public sealed record Not(StyleMatch Match) : StyleMatch;
}

/// <summary>Which part of a ghost is drawn. Values are fixed for the life of the contract.</summary>
public enum PartKind
{
    /// <summary>The planned piece at its cell and turn: a build, a swap's new piece.</summary>
    Placement = 1,

    /// <summary>An overlay on the built structure: a removal, a swap's old piece, a finish-only ghost, a structure set member.</summary>
    Existing = 2,
}

/// <summary>A ghost's state, as a style matches it (<see cref="GhostState"/>). Values are fixed for the life of the contract.</summary>
public enum StateKind
{
    /// <summary><see cref="GhostState.Planned"/>.</summary>
    Planned = 1,

    /// <summary><see cref="GhostState.Started"/>.</summary>
    Started = 2,

    /// <summary><see cref="GhostState.Stale"/>.</summary>
    Stale = 3,
}

/// <summary>A ghost's finding, as a style matches it (<see cref="Lattice.Api.Finding"/>). Values are fixed for the life of the contract.</summary>
public enum FindingKind
{
    /// <summary>Not checked yet, or the answer is pending.</summary>
    Unchecked = 1,

    /// <summary><see cref="Lattice.Api.Finding.Passes"/>.</summary>
    Passes = 2,

    /// <summary><see cref="Lattice.Api.Finding.WaitsFor"/>.</summary>
    WaitsFor = 3,

    /// <summary><see cref="Lattice.Api.Finding.WillFail"/>.</summary>
    WillFail = 4,

    /// <summary><see cref="Lattice.Api.Finding.Warns"/>.</summary>
    Warns = 5,
}

/// <summary>An executor's status on a ghost, as a style matches it (<see cref="ExecutorStatus"/>). Values are fixed for the life of the contract.</summary>
public enum StatusKind
{
    /// <summary><see cref="ExecutorStatus.Working"/>.</summary>
    Working = 1,

    /// <summary><see cref="ExecutorStatus.Waiting"/>.</summary>
    Waiting = 2,

    /// <summary><see cref="ExecutorStatus.Paused"/>.</summary>
    Paused = 3,

    /// <summary>No executor reports on the ghost.</summary>
    None = 4,
}

/// <summary>A face of a grid cell, by its outward normal. Values are fixed for the life of the contract.</summary>
public enum Face
{
    /// <summary>Towards +X.</summary>
    PlusX = 1,

    /// <summary>Towards -X.</summary>
    MinusX = 2,

    /// <summary>Towards +Y (up).</summary>
    PlusY = 3,

    /// <summary>Towards -Y (down).</summary>
    MinusY = 4,

    /// <summary>Towards +Z.</summary>
    PlusZ = 5,

    /// <summary>Towards -Z.</summary>
    MinusZ = 6,
}
