using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Sync;

/// <summary>A network connection as the game numbers it (<c>Client.connectionId</c>); the host's own is never one.</summary>
internal readonly record struct ConnectionId(long Value);

/// <summary>The five messages sync sends; each travels as one message type of its own.</summary>
internal enum SyncKind
{
    /// <summary>A client's command, client to host (<see cref="CommandForm"/>).</summary>
    Command = 1,

    /// <summary>The change lines of one change set, host to every joined client (the live message form).</summary>
    Lines = 2,

    /// <summary>One part of the current ghosts, host to a joining client (the join form).</summary>
    Join = 3,

    /// <summary>The outcome of a client's command, host to that client (<see cref="AnswerForm"/>).</summary>
    Answer = 4,

    /// <summary>Executors' claims and status on some ghosts, host to every joined client (<see cref="StatusForm"/>).</summary>
    Status = 5,
}

/// <summary>
/// Sends sync text over the game's network. Main thread. Sending is fire and forget; the network keeps the order of
/// messages to one connection.
/// </summary>
internal interface ISyncLink
{
    void ToHost(SyncKind kind, string text);

    void ToClient(ConnectionId to, SyncKind kind, string text);
}

/// <summary>
/// A client's board as sync drives it: it takes the host's change sets as they are and the host's ghosts at join.
/// Main thread.
/// </summary>
internal interface IReplicaBoard
{
    Revision Revision { get; }

    /// <summary>The board's events: changes, answers and resets are raised through them.</summary>
    BoardEvents Events { get; }

    /// <summary>The ghost as the board holds it now; none when it holds no ghost with that id.</summary>
    Option<Ghost> Ghost(GhostId id);

    /// <summary>The plan as the board holds it now; none when it holds no active plan with that id.</summary>
    Option<Plan> Plan(PlanId id);

    /// <summary>
    /// Commits a change set the host made, as it is: its ids, revision, ghosts and plans. One revision, one queued
    /// <see cref="IGhostEvents.Changed"/>. Refused, changing nothing, unless its revision is the board's next.
    /// </summary>
    Result<ChangeSet, Reason> Mirror(ChangeSet set);

    /// <summary>Replaces everything the board holds with the host's ghosts and plans; queues <see cref="IGhostEvents.BoardReset"/>.</summary>
    void Reset(BoardSeed seed);
}
