using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.Executors;

/// <summary>One ghost's claim as the host holds it: held (by whom, the lease seconds left, what it last said), or gone.</summary>
internal abstract record ClaimState
{
    private ClaimState(GhostId ghost) => Ghost = ghost;

    public GhostId Ghost { get; }

    /// <summary>Claimed by an executor, with the lease seconds left on the host and its status, if it reported one.</summary>
    public sealed record Held(GhostId Ghost, ExecutorInfo By, double LeftSeconds, Option<ExecutorStatus> Status) : ClaimState(Ghost);

    /// <summary>No claim and no status.</summary>
    public sealed record Gone(GhostId Ghost) : ClaimState(Ghost);
}

/// <summary>
/// A client's copy of the host's claims and status, read through the client seat's <see cref="IExecutors"/>. It holds
/// what the host last said about each ghost on this board and never lapses a claim by this seat's clock: a claim's
/// <see cref="Claim.Until"/> is this seat's game time when the host's word arrived plus the lease seconds left then,
/// and the claim stays until the host says it is gone. Taking the host's word raises what the host raised:
/// <see cref="IGhostEvents.StatusChanged"/> when a ghost's claim is taken or goes or its reports change, and <see cref="IGhostEvents.Blocked"/>
/// when they change to <see cref="ExecutorStatus.Waiting"/>. A ghost that leaves this board, and a board reset, drop
/// its entry with no event. Reads are constant cost and allocate nothing. Main thread.
/// </summary>
internal sealed class ExecutorMirror(GhostBoard board, IGameClock clock, ExecutorNotices notices, BuilderBook book)
{
    private readonly Dictionary<GhostId, Mirrored> _held = new();

    /// <summary>How many ghosts hold a mirrored claim.</summary>
    public int Count => _held.Count;

    /// <summary>This seat's game time, which the host's supply ages are taken against.</summary>
    public GameTime Now => clock.Now;

    public Option<Claim> ClaimOn(GhostId ghost) =>
        _held.TryGetValue(ghost, out var held) ? Option<Claim>.Some(held.Claim) : Option<Claim>.None;

    public ValueList<ExecutorReport> StatusOf(GhostId ghost) =>
        _held.TryGetValue(ghost, out var held) ? held.Reports : ValueList<ExecutorReport>.Empty;

    /// <summary>
    /// The host's word on some ghosts, in order. A join's word replaces everything held; a ghost this board does not
    /// hold is passed over. Linear in the entries.
    /// </summary>
    public void Take(bool join, ValueList<ClaimState> entries)
    {
        if (join)
            _held.Clear();
        foreach (var entry in entries)
        {
            var ghost = entry.Ghost;
            if (!board.Index.Ghosts.ContainsKey(ghost))
                continue;
            var had = _held.TryGetValue(ghost, out var was);
            var before = had ? was.Reports : ValueList<ExecutorReport>.Empty;
            switch (entry)
            {
                case ClaimState.Held held:
                    var reports = held.Status.TryGet(out var status)
                        ? ValueList.Of(new ExecutorReport(held.By, status))
                        : ValueList<ExecutorReport>.Empty;
                    var until = new GameTime(clock.Now.Seconds + held.LeftSeconds);
                    _held[ghost] = new Mirrored(new Claim(ghost, held.By.Id, until), reports);
                    book.Claimed(ghost, held.By.Id, held.Status);
                    Told(ghost, before, reports, claimMoved: !had || was.Claim.By != held.By.Id);
                    break;
                case ClaimState.Gone:
                    _held.Remove(ghost);
                    book.Unclaimed(ghost);
                    Told(ghost, before, ValueList<ExecutorReport>.Empty, claimMoved: had);
                    break;
            }
        }
    }

    /// <summary>The ghost left this board: its entry goes with no event.</summary>
    public void Forget(GhostId ghost)
    {
        _held.Remove(ghost);
        book.Unclaimed(ghost);
    }

    /// <summary>The board was reset: every entry goes with no event, and every builder with it.</summary>
    public void Clear()
    {
        _held.Clear();
        book.ClearClaims();
        foreach (var id in new List<ExecutorId>(book.Ids))
            book.Gone(id);
    }

    /// <summary>The host's word on builders: a join's replaces every builder held; each line is a builder's whole word.</summary>
    public void TakeBuilders(bool join, IReadOnlyList<BuilderSays> says, IReadOnlyList<ExecutorId> gone)
    {
        if (join)
        {
            var named = new HashSet<ExecutorId>();
            foreach (var each in says)
                named.Add(each.Info.Id);
            foreach (var id in new List<ExecutorId>(book.Ids))
                if (!named.Contains(id))
                    book.Gone(id);
        }

        foreach (var each in says)
            book.Take(each);
        foreach (var id in gone)
            book.Gone(id);
    }

    private void Told(GhostId ghost, ValueList<ExecutorReport> before, ValueList<ExecutorReport> now, bool claimMoved)
    {
        if (before.Equals(now) && !claimMoved)
            return;
        notices.StatusChanged(ghost);
        if (now.Count > 0 && now[0].Status is ExecutorStatus.Waiting waiting)
            notices.Blocked(ghost, waiting.Why);
    }

    private sealed record Mirrored(Claim Claim, ValueList<ExecutorReport> Reports);
}
