using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.Executors;

/// <summary>Where this process sits in the world: the host applies commands and holds claims; a client only asks.</summary>
internal enum WorldSeat
{
    Host = 1,
    Client = 2,
}

/// <summary>
/// The world's executors: who works on which ghost and what each says about it. One claim per ghost; a claim lapses
/// when game time reaches its end (<see cref="IGameClock"/>, so a paused game holds every lease), when its executor
/// releases it or is disposed, and when its ghost leaves the board. Reads see a lapsed claim as gone at once;
/// <see cref="Lapse"/>, called once per frame before the board's events flush, forgets lapsed claims and tells
/// <see cref="IGhostEvents.StatusChanged"/> for those that had a status. Status lives with the claim and is never
/// "done": matching decides that from the world. A ghost's maker hears <see cref="IGhostEvents.Blocked"/> when the
/// ghost turns stale or its stale reason changes, and when its executor reports <see cref="ExecutorStatus.Waiting"/>
/// with a status it had not reported just before. On a client nothing is held here: reads answer from the host's word
/// (<see cref="Mirror"/>), which sync keeps. Main thread. Dispose at world end.
/// </summary>
internal sealed class WorldExecutors : IExecutors, IDisposable, Lattice.Core.Styles.IStatusReader
{
    private readonly GhostBoard _board;
    private readonly IGameClock _clock;
    private readonly WorldSeat _seat;
    private readonly Dictionary<ExecutorId, Handle> _registered = new();
    private readonly Dictionary<GhostId, Hold> _holds = new();
    private readonly SortedSet<(double Until, long Ghost)> _ends = new();
    private readonly ExecutorNotices _notices;
    private readonly ExecutorMirror? _mirror;
    private readonly Action<ChangeSet> _onBoard;

    public WorldExecutors(GhostBoard board, IGameClock clock, WorldSeat seat)
    {
        _board = board;
        _clock = clock;
        _seat = seat;
        _notices = new ExecutorNotices(board);
        if (seat == WorldSeat.Client)
            _mirror = new ExecutorMirror(board, clock, _notices);
        _onBoard = OnBoard;
        board.Events.Changed += _onBoard;
    }

    /// <summary>Who gives the executors' reasons.</summary>
    public static RuleId Rule => RuleId.Known("lattice.executors");

    /// <inheritdoc />
    public Result<IExecutorHandle, Reason> Register(ExecutorInfo info)
    {
        if (_seat != WorldSeat.Host)
            return Result<IExecutorHandle, Reason>.Error(Reason.Of(ReasonCodes.HostOnly, Rule));
        if (info is null)
            return Result<IExecutorHandle, Reason>.Error(Reason.InvalidValue(nameof(ExecutorInfo), "null", "not-null"));
        if (info.Id == default || info.Name == default)
            return Result<IExecutorHandle, Reason>.Error(Reason.Of(ReasonCodes.DefaultValue, Rule,
                ValueMap.Of("type", info.Id == default ? nameof(ExecutorId) : nameof(DisplayName))));
        if (_registered.ContainsKey(info.Id))
            return Result<IExecutorHandle, Reason>.Error(Reason.Of(ReasonCodes.AlreadyRegistered, Rule, ValueMap.Of("executor", info.Id.Value)));

        var handle = new Handle(this, info);
        _registered.Add(info.Id, handle);
        return Result<IExecutorHandle, Reason>.Ok(handle);
    }

    /// <summary>The host's claims and status as this client last heard them; none on the host.</summary>
    internal ExecutorMirror? Mirror => _mirror;

    /// <summary>
    /// Called with a ghost each time its claim or status may have changed: a claim made or renewed, a report, a claim
    /// released, lapsed, or dropped with its executor or its ghost. Sync sets it on the host.
    /// </summary>
    internal Action<GhostId>? Told { get; set; }

    /// <inheritdoc />
    public Option<Claim> ClaimOn(GhostId ghost) =>
        _mirror is not null ? _mirror.ClaimOn(ghost)
        : Live(ghost, out var hold) ? Option<Claim>.Some(hold.Claim) : Option<Claim>.None;

    /// <inheritdoc />
    public ValueList<ExecutorReport> StatusOf(GhostId ghost) =>
        _mirror is not null ? _mirror.StatusOf(ghost)
        : Live(ghost, out var hold) ? hold.Reports : ValueList<ExecutorReport>.Empty;

    /// <summary>The ghost's claim as it stands now: held, with the lease seconds left, or gone. Constant cost.</summary>
    internal ClaimState StateOf(GhostId ghost) =>
        Live(ghost, out var hold)
            ? new ClaimState.Held(ghost, hold.By.Info, hold.Claim.Until.Seconds - _clock.Now.Seconds, hold.Status)
            : new ClaimState.Gone(ghost);

    /// <summary>Adds every ghost with a live claim to <paramref name="into"/>, in id order. Linear in the claims.</summary>
    internal void Claimed(List<GhostId> into)
    {
        var start = into.Count;
        foreach (var ghost in _holds.Keys)
            if (Live(ghost, out _))
                into.Add(ghost);
        into.Sort(start, into.Count - start, ById.Instance);
    }

    /// <summary>Forgets every claim whose lease has run out. Linear in the claims that lapsed; allocates nothing when none did.</summary>
    public void Lapse()
    {
        var now = _clock.Now.Seconds;
        while (_ends.Count > 0 && _ends.Min.Until <= now)
            Drop(_holds[new GhostId(_ends.Min.Ghost)]);
    }

    /// <summary>Ends the world's executors: every handle is disposed and the board is no longer watched.</summary>
    public void Dispose()
    {
        _board.Events.Changed -= _onBoard;
        var handles = new List<Handle>(_registered.Values);
        foreach (var handle in handles)
            handle.Dispose();
    }

    private bool Live(GhostId ghost, out Hold hold) =>
        _holds.TryGetValue(ghost, out hold!) && !Lapsed(hold) && _board.Index.Ghosts.ContainsKey(ghost);

    private bool Lapsed(Hold hold) => _clock.Now.Seconds >= hold.Claim.Until.Seconds;

    private Result<Claim, ClaimRefused> Claim(Handle by, GhostId ghost, Lease lease)
    {
        if (by.Disposed)
            return Refused(NotRegistered(by));
        if (ghost == default)
            return Refused(Reason.Of(ReasonCodes.DefaultValue, Rule, ValueMap.Of("type", nameof(GhostId))));
        if (lease == default)
            return Refused(Reason.Of(ReasonCodes.DefaultValue, Rule, ValueMap.Of("type", nameof(Lease))));
        if (!_board.Index.Ghosts.ContainsKey(ghost))
            return Refused(Reason.Of(ReasonCodes.NotFound, Rule, ValueMap.Of("ghost", ghost.Value.ToString()).With("problem", "ghost")));

        if (_holds.TryGetValue(ghost, out var hold))
        {
            if (!Lapsed(hold) && hold.By != by)
                return Refused(HeldBy(hold));
            if (hold.By == by && !Lapsed(hold))
                return Result<Claim, ClaimRefused>.Ok(Extend(hold, lease));
            Drop(hold);
        }

        hold = new Hold(by, lease, new Claim(ghost, by.Info.Id, Until(lease)));
        _holds.Add(ghost, hold);
        _ends.Add(End(hold));
        by.Held.Add(ghost);
        Told?.Invoke(ghost);
        return Result<Claim, ClaimRefused>.Ok(hold.Claim);
    }

    private Result<Claim, ClaimRefused> Renew(Handle by, Claim claim)
    {
        if (by.Disposed)
            return Refused(NotRegistered(by));
        if (claim is null)
            return Refused(Reason.InvalidValue(nameof(Claim), "null", "not-null"));
        if (claim.By != by.Info.Id)
            return Refused(Reason.Of(ReasonCodes.NotAllowed, Rule, ValueMap.Of("executor", by.Info.Id.Value).With("claim-by", claim.By.Value)));
        if (Live(claim.Ghost, out var hold))
            return hold.By == by ? Result<Claim, ClaimRefused>.Ok(Extend(hold, hold.Lease)) : Refused(HeldBy(hold));
        return Refused(Reason.Of(ReasonCodes.LeaseExpired, Rule, ValueMap.Of("ghost", claim.Ghost.Value.ToString())));
    }

    private void Release(Handle by, Claim claim)
    {
        if (claim is not null && claim.By == by.Info.Id && _holds.TryGetValue(claim.Ghost, out var hold) && hold.By == by)
            Drop(hold);
    }

    private Result<Unit, Reason> Report(Handle by, GhostId ghost, ExecutorStatus status)
    {
        if (by.Disposed)
            return Result<Unit, Reason>.Error(NotRegistered(by));
        if (status is null)
            return Result<Unit, Reason>.Error(Reason.InvalidValue(nameof(ExecutorStatus), "null", "not-null"));
        if (!Live(ghost, out var hold))
            return Result<Unit, Reason>.Error(Reason.Of(ReasonCodes.NotFound, Rule, ValueMap.Of("ghost", ghost.Value.ToString()).With("problem", "claim")));
        if (hold.By != by)
            return Result<Unit, Reason>.Error(HeldBy(hold).Why);
        if (hold.Status.TryGet(out var before) && before == status)
            return Result<Unit, Reason>.Ok(Unit.Value);

        hold.Status = Option<ExecutorStatus>.Some(status);
        hold.Reports = ValueList.Of(new ExecutorReport(by.Info, status));
        Told?.Invoke(ghost);
        _notices.StatusChanged(ghost);
        if (status is ExecutorStatus.Waiting waiting)
            _notices.Blocked(ghost, waiting.Why);
        return Result<Unit, Reason>.Ok(Unit.Value);
    }

    private void Unregister(Handle handle)
    {
        var held = new GhostId[handle.Held.Count];
        handle.Held.CopyTo(held);
        foreach (var ghost in held)
            Drop(_holds[ghost]);
        _registered.Remove(handle.Info.Id);
    }

    private void OnBoard(ChangeSet changes)
    {
        foreach (var removal in changes.Removed)
        {
            if (_holds.TryGetValue(removal.Ghost.Id, out var hold))
                Drop(hold);
            _mirror?.Forget(removal.Ghost.Id);
        }

        foreach (var added in changes.Added)
            if (added.State is GhostState.Stale stale)
                _notices.Blocked(added.Id, stale.Why);
        foreach (var change in changes.Changed)
            if (change.After.State is GhostState.Stale stale && change.Before.State != change.After.State)
                _notices.Blocked(change.After.Id, stale.Why);
    }

    private Claim Extend(Hold hold, Lease lease)
    {
        _ends.Remove(End(hold));
        hold.Lease = lease;
        hold.Claim = hold.Claim with { Until = Until(lease) };
        _ends.Add(End(hold));
        Told?.Invoke(hold.Claim.Ghost);
        return hold.Claim;
    }

    private void Drop(Hold hold)
    {
        var ghost = hold.Claim.Ghost;
        _holds.Remove(ghost);
        _ends.Remove(End(hold));
        hold.By.Held.Remove(ghost);
        Told?.Invoke(ghost);
        if (hold.Status.IsSome)
            _notices.StatusChanged(ghost);
    }

    private GameTime Until(Lease lease) => new(_clock.Now.Seconds + lease.Seconds);

    private static (double, long) End(Hold hold) => (hold.Claim.Until.Seconds, hold.Claim.Ghost.Value);

    private static ClaimRefused HeldBy(Hold hold) => new(
        Reason.Of(ReasonCodes.Claimed, Rule, ValueMap.Of("ghost", hold.Claim.Ghost.Value.ToString()).With("executor", hold.Claim.By.Value)),
        Option<ExecutorId>.Some(hold.Claim.By));

    private static Reason NotRegistered(Handle by) =>
        Reason.Of(ReasonCodes.NotFound, Rule, ValueMap.Of("executor", by.Info.Id.Value).With("problem", "executor"));

    private static Result<Claim, ClaimRefused> Refused(Reason why) =>
        Result<Claim, ClaimRefused>.Error(new ClaimRefused(why, Option<ExecutorId>.None));

    private static Result<Claim, ClaimRefused> Refused(ClaimRefused refused) => Result<Claim, ClaimRefused>.Error(refused);

    private sealed class ById : IComparer<GhostId>
    {
        public static readonly ById Instance = new();

        public int Compare(GhostId a, GhostId b) => a.Value.CompareTo(b.Value);
    }

    /// <summary>One executor's claim on one ghost, the lease it renews by, and what it last said.</summary>
    private sealed class Hold(Handle by, Lease lease, Claim claim)
    {
        public Handle By { get; } = by;

        public Lease Lease { get; set; } = lease;

        public Claim Claim { get; set; } = claim;

        public Option<ExecutorStatus> Status { get; set; }

        public ValueList<ExecutorReport> Reports { get; set; } = ValueList<ExecutorReport>.Empty;
    }

    private sealed class Handle(WorldExecutors executors, ExecutorInfo info) : IExecutorHandle
    {
        public ExecutorInfo Info { get; } = info;

        public HashSet<GhostId> Held { get; } = new();

        public bool Disposed { get; private set; }

        public Result<Claim, ClaimRefused> Claim(GhostId ghost, Lease lease) => executors.Claim(this, ghost, lease);

        public Result<Claim, ClaimRefused> Renew(Claim claim) => executors.Renew(this, claim);

        public void Release(Claim claim) => executors.Release(this, claim);

        public Result<Unit, Reason> Report(GhostId ghost, ExecutorStatus status) => executors.Report(this, ghost, status);

        public void Dispose()
        {
            if (Disposed)
                return;
            Disposed = true;
            executors.Unregister(this);
        }
    }
}
