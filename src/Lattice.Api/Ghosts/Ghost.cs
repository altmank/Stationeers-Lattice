using System;

namespace Lattice.Api;

/// <summary>
/// A ghost: an intent about one target, in one plan, with who placed it, what made it and how it looks.
/// Done ghosts leave the board; their record stays in the plan file. A ghost's owner is the actor who placed it
/// (<see cref="PlacedBy"/>); ownership, <see cref="Visibility.OwnerOnly"/> and <see cref="Selector.Owner"/> all mean that.
/// </summary>
/// <param name="Id">Its id, never reused.</param>
/// <param name="Plan">The plan it belongs to.</param>
/// <param name="Revision">The board revision of its last change.</param>
/// <param name="Intent">What it asks for.</param>
/// <param name="State">Planned, started or stale.</param>
/// <param name="PlacedBy">The actor whose command placed it: its owner.</param>
/// <param name="MadeBy">What made it; told when it is blocked.</param>
/// <param name="PlacedAt">When it was placed, real time.</param>
/// <param name="Visibility">Who may see it, when a policy enforces it.</param>
/// <param name="Label">A player's label, if any.</param>
/// <param name="Tint">The colour a player sorts it by, if any; styles draw it (<see cref="Paint.GhostTint"/>).</param>
/// <param name="Groups">The groups it is in.</param>
/// <param name="Attached">Mods' data under their own keys; never read by the core.</param>
public sealed record Ghost(
    GhostId Id,
    PlanId Plan,
    Revision Revision,
    Intent Intent,
    GhostState State,
    Actor PlacedBy,
    Maker MadeBy,
    DateTimeOffset PlacedAt,
    Visibility Visibility,
    Option<Label> Label,
    Option<Tint> Tint,
    ValueSet<GroupName> Groups,
    ValueMap<AttachmentKey, Attachment> Attached);

/// <summary>Where a ghost is in its life while on the board. Union: new states come with a new contract minor.</summary>
public abstract record GhostState
{
    private GhostState() { }

    /// <summary>Nothing of it is built yet.</summary>
    public sealed record Planned : GhostState;

    /// <summary>A part-built structure of its piece stands in its spot.</summary>
    /// <param name="PartBuilt">The part-built structure.</param>
    public sealed record Started(StructureRef PartBuilt) : GhostState;

    /// <summary>It cannot be done as it is (something different is in its spot, ...).</summary>
    /// <param name="Why">The reason.</param>
    public sealed record Stale(Reason Why) : GhostState;
}

/// <summary>A built structure: its id, plus its piece and cell so it survives the id changing.</summary>
/// <param name="Id">The game's reference id.</param>
/// <param name="Piece">Its piece.</param>
/// <param name="At">Its cell.</param>
public readonly record struct StructureRef(ReferenceId Id, PrefabKey Piece, Cell At);

/// <summary>A piece in a cell in a turn. Every build means fully built.</summary>
/// <param name="Piece">The piece.</param>
/// <param name="At">The cell.</param>
/// <param name="Turn">The turn.</param>
public sealed record Placement(PrefabKey Piece, Cell At, Turn Turn);
