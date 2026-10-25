using System;

namespace Lattice.Api;

/// <summary>A ghost's id. Never reused within a world. <c>default</c> is no ghost; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct GhostId(long Value);

/// <summary>A plan's id. Never reused within a world. <c>default</c> is no plan; a command holding it is refused with <see cref="ReasonCodes.DefaultValue"/>.</summary>
public readonly record struct PlanId(long Value);

/// <summary>
/// A board revision. The board's revision rises by one per applied change set; a ghost's or plan's revision is the
/// board revision of its last change, so "unchanged since R" means its revision is at most R.
/// Changed-since checks (<see cref="Command.Revert"/>, <c>UnchangedSince</c>) compare against the last change a command
/// made; Lattice's own marks from the world (started, stale) advance a ghost's revision but never count as changed since.
/// <c>default</c> is the empty board before any change.
/// </summary>
public readonly record struct Revision(long Value);

/// <summary>A command's id, chosen by the sender. The same id sent twice applies once. <c>default</c> is refused.</summary>
public readonly record struct CommandId(Guid Value)
{
    /// <summary>A fresh id.</summary>
    public static CommandId New() => new(Guid.NewGuid());
}

/// <summary>The id of one applied change set: the unit of sync, events and undo. <c>default</c> is none.</summary>
public readonly record struct ChangeSetId(long Value);

/// <summary>Follows a submitted command until its outcome arrives.</summary>
public readonly record struct Ticket(CommandId Command);

/// <summary>A built structure's id in the game (its reference id). <c>default</c> is none.</summary>
public readonly record struct ReferenceId(long Value);

/// <summary>A cable, pipe or chute network in the game. <c>default</c> is none.</summary>
public readonly record struct NetworkRef(long Value);

/// <summary>A room in the game. <c>default</c> is none.</summary>
public readonly record struct RoomRef(long Value);

/// <summary>A connected player's client id.</summary>
public readonly record struct ClientId(ulong Value);

/// <summary>The number of a line in a plan file, from 1. <c>default</c> is before the first line.</summary>
public readonly record struct LineNumber(long Value);
