using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.Executors;

/// <summary>What a builder says about itself, as the host holds it and sync carries it: never saved.</summary>
/// <param name="Info">Who it is, fixed at registration.</param>
/// <param name="Area">Where it works.</param>
/// <param name="Word">Its word on itself.</param>
/// <param name="Supply">Its latest supply report, if any.</param>
/// <param name="Answers">Its answers to the plans assigned to it.</param>
internal sealed record BuilderSays(
    ExecutorInfo Info,
    Option<BuilderArea> Area,
    BuilderWord Word,
    Option<Supply> Supply,
    ValueMap<PlanId, AssignmentAnswer> Answers);

/// <summary>
/// The world's builders as every seat reads them (<see cref="IExecutors.All"/> and the reads beside it): what each
/// builder says (from its handle on the host, from the host's word on a client), how its claims stand, and Lattice's
/// counts. A builder's state is worked out from its claims first (a claim reported working, or not reported on, makes
/// it working, beating a stuck word; waiting and paused claims do not), then its word. Built counts are the board's
/// (<see cref="PlanEntry.DoneBy"/>), working and waiting counts the live claims'. Every read is cached and allocates
/// nothing while unchanged; a change drops only what it touches. A builder whose record changed is told once per burst
/// (<see cref="IGhostEvents.BuilderChanged"/>), and to <see cref="Told"/> at once. Main thread.
/// </summary>
internal sealed class BuilderBook
{
    /// <summary>The caps on a supply report.</summary>
    public const int MostItems = 256;

    /// <summary>How many plans a supply report may name.</summary>
    public const int MostPlans = 64;

    private static readonly BuilderState IdleState = new BuilderState.Idle();
    private static readonly BuilderState WorkingState = new BuilderState.Working();
    private static readonly BuilderWord ReadyWord = new BuilderWord.Ready();

    private readonly GhostBoard _board;
    private readonly Actor _viewer;
    private readonly Dictionary<ExecutorId, Entry> _builders = new();
    private readonly Dictionary<GhostId, Tally> _claims = new();
    private readonly Dictionary<(PlanId Plan, ExecutorId By), Counts> _counts = new();
    private readonly Dictionary<PlanId, Counts> _planCounts = new();
    private readonly Dictionary<PlanId, ValueList<Assignment>> _assigned = new();
    private readonly Dictionary<ExecutorId, ValueList<PlanId>> _plansOf = new();
    private readonly Dictionary<PlanId, ValueList<ItemQuantity>> _toward = new();
    private readonly List<ExecutorId> _changedOrder = new();
    private readonly HashSet<ExecutorId> _changed = new();
    private readonly Action<ChangeSet> _onBoard;
    private ValueList<Builder>? _all;
    private bool _queued;

    public BuilderBook(GhostBoard board, Actor viewer)
    {
        _board = board;
        _viewer = viewer;
        _onBoard = OnBoard;
        board.Committed += _onBoard;
    }

    /// <summary>Called with a builder each time what it says changes, or it registers or goes. Sync sets it on the host.</summary>
    internal Action<ExecutorId>? Told { get; set; }

    // ---- what builders say ----

    /// <summary>A builder registered (or, on a client, the host's word on it arrived): what it says starts empty.</summary>
    public void Put(ExecutorInfo info) => Take(new BuilderSays(info, Option<BuilderArea>.None, ReadyWord, Option<Supply>.None, ValueMap<PlanId, AssignmentAnswer>.Empty));

    /// <summary>Takes a builder's whole word: on a client the host's line; equal words change nothing.</summary>
    public void Take(BuilderSays says)
    {
        if (_builders.TryGetValue(says.Info.Id, out var entry))
        {
            if (entry.Says == says)
                return;
            entry.Says = says;
        }
        else
            _builders.Add(says.Info.Id, entry = new Entry(says, WorkingOf(says.Info.Id)));

        Changed(says.Info.Id, answers: true);
    }

    /// <summary>A builder went: its handle was disposed, or the host said so.</summary>
    public void Gone(ExecutorId id)
    {
        if (!_builders.Remove(id))
            return;
        Changed(id, answers: true);
    }

    /// <summary>The builders now known, by id.</summary>
    public IEnumerable<ExecutorId> Ids => _builders.Keys;

    /// <summary>What a builder says, if it is registered.</summary>
    public Option<BuilderSays> Says(ExecutorId id) =>
        _builders.TryGetValue(id, out var entry) ? Option.Some(entry.Says) : Option<BuilderSays>.None;

    public bool Has(ExecutorId id) => _builders.ContainsKey(id);

    public void Locate(ExecutorId id, Option<BuilderArea> area)
    {
        if (_builders.TryGetValue(id, out var entry))
            Take(entry.Says with { Area = area });
    }

    public void Say(ExecutorId id, BuilderWord word)
    {
        if (_builders.TryGetValue(id, out var entry))
            Take(entry.Says with { Word = word });
    }

    /// <summary>
    /// A supply report, checked against the caps (<see cref="ReasonCodes.TooLarge"/> with the list, its size and the cap)
    /// and stamped with <paramref name="at"/>; an equal report keeps its first stamp and changes nothing.
    /// </summary>
    public Result<Unit, Reason> Offer(ExecutorId id, SupplyReport report, GameTime at)
    {
        if (report is null || report.Reaches is null || report.Toward is null)
            return Result<Unit, Reason>.Error(Reason.InvalidValue(nameof(SupplyReport), "null", "not-null"));
        if (report.Reaches.Count > MostItems)
            return TooLarge("reaches", report.Reaches.Count, MostItems);
        if (report.Toward.Count > MostPlans)
            return TooLarge("toward", report.Toward.Count, MostPlans);
        foreach (var pair in report.Toward)
            if (pair.Value is null)
                return Result<Unit, Reason>.Error(Reason.InvalidValue(nameof(SupplyReport), "null", "not-null"));
            else if (pair.Value.Count > MostItems)
                return Result<Unit, Reason>.Error(Reason.Of(ReasonCodes.TooLarge, WorldExecutors.Rule, ValueMap
                    .Of("list", "toward")
                    .With("plan", pair.Key.Value.ToString())
                    .With("count", pair.Value.Count.ToString())
                    .With("most", MostItems.ToString())));
        if (!_builders.TryGetValue(id, out var entry))
            return Result<Unit, Reason>.Ok(Unit.Value);
        if (entry.Says.Supply.TryGet(out var had) && had.Report == report)
            return Result<Unit, Reason>.Ok(Unit.Value);
        Take(entry.Says with { Supply = Option.Some(new Supply(report, at)) });
        return Result<Unit, Reason>.Ok(Unit.Value);
    }

    /// <summary>A builder's answer to a plan; refused not-found when the plan on the board does not assign it.</summary>
    public Result<Unit, Reason> Answer(ExecutorId id, PlanId plan, AssignmentAnswer answer)
    {
        if (answer is null)
            return Result<Unit, Reason>.Error(Reason.InvalidValue(nameof(AssignmentAnswer), "null", "not-null"));
        if (!_board.Index.Plans.TryGetValue(plan, out var entry) || !entry.Plan.BuildBy.Contains(id))
            return Result<Unit, Reason>.Error(Reason.Of(ReasonCodes.NotFound, WorldExecutors.Rule, ValueMap
                .Of("plan", plan.Value.ToString())
                .With("executor", id.Value)
                .With("problem", "assignment")));
        if (_builders.TryGetValue(id, out var builder) && !(builder.Says.Answers.TryGetValue(plan, out var had) && had == answer))
            Take(builder.Says with { Answers = builder.Says.Answers.With(plan, answer) });
        return Result<Unit, Reason>.Ok(Unit.Value);
    }

    // ---- claims ----

    /// <summary>
    /// A ghost's claim as it stands: held by <paramref name="by"/> with its last status (none: not reported on), or
    /// gone (<see cref="Unclaimed"/>). Constant cost.
    /// </summary>
    public void Claimed(GhostId ghost, ExecutorId by, Option<ExecutorStatus> status)
    {
        var plan = _board.Index.Ghosts.TryGetValue(ghost, out var entry) ? entry.Ghost.Plan : default;
        var working = !status.TryGet(out var said) || said is ExecutorStatus.Working;
        if (_claims.TryGetValue(ghost, out var was))
        {
            if (was.Plan == plan && was.By == by && was.Working == working)
                return;
            Count(was, -1);
        }

        var now = new Tally(plan, by, working);
        _claims[ghost] = now;
        Count(now, 1);
    }

    public void Unclaimed(GhostId ghost)
    {
        if (!_claims.TryGetValue(ghost, out var was))
            return;
        _claims.Remove(ghost);
        Count(was, -1);
    }

    /// <summary>Every claim went (a client's board was reset).</summary>
    public void ClearClaims()
    {
        var ghosts = new List<GhostId>(_claims.Keys);
        foreach (var ghost in ghosts)
            Unclaimed(ghost);
    }

    // ---- reads ----

    public ValueList<Builder> All
    {
        get
        {
            if (_all is { } all)
                return all;
            var ids = new List<ExecutorId>(_builders.Keys);
            ids.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
            var builders = new Builder[ids.Count];
            for (var i = 0; i < ids.Count; i++)
                builders[i] = Read(_builders[ids[i]]);
            var built = Lists.Own(builders);
            _all = built;
            return built;
        }
    }

    public Option<Builder> Find(ExecutorId id) =>
        _builders.TryGetValue(id, out var entry) ? Option.Some(Read(entry)) : Option<Builder>.None;

    public ValueList<Assignment> AssignedTo(PlanId plan)
    {
        if (_assigned.TryGetValue(plan, out var cached))
            return cached;
        if (!_board.Index.Plans.TryGetValue(plan, out var entry) || entry.Plan.BuildBy.Count == 0)
            return ValueList<Assignment>.Empty;
        var ids = Sorted(entry.Plan.BuildBy);
        var assigned = new Assignment[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            assigned[i] = !_builders.TryGetValue(id, out var builder) ? new Assignment.Absent(id)
                : !builder.Says.Answers.TryGetValue(plan, out var answer) ? new Assignment.Asked(id)
                : answer is AssignmentAnswer.Refused refused ? new Assignment.Refused(id, refused.Why)
                : new Assignment.Accepted(id);
        }

        var list = Lists.Own(assigned);
        _assigned[plan] = list;
        return list;
    }

    public ValueList<PlanId> PlansOf(ExecutorId id)
    {
        if (_plansOf.TryGetValue(id, out var cached))
            return cached;
        var plans = new List<PlanId>();
        foreach (var entry in _board.Index.Plans.Values)
            if (entry.Plan.BuildBy.Contains(id))
                plans.Add(entry.Plan.Id);
        plans.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        var list = Lists.Own(plans.ToArray());
        _plansOf[id] = list;
        return list;
    }

    public BuilderProgress ProgressOf(PlanId plan, ExecutorId id)
    {
        var built = _board.Index.Plans.TryGetValue(plan, out var entry) ? entry.DoneBy.Of(id) : 0;
        var counts = _counts.TryGetValue((plan, id), out var live) ? live : default;
        return new BuilderProgress(built, counts.Working, counts.Waiting);
    }

    public BuilderProgress ProgressOf(PlanId plan)
    {
        var built = 0;
        if (_board.Index.Plans.TryGetValue(plan, out var entry))
            foreach (var pair in entry.DoneBy)
                built += pair.Value;
        var counts = _planCounts.TryGetValue(plan, out var live) ? live : default;
        return new BuilderProgress(built, counts.Working, counts.Waiting);
    }

    public ValueList<ItemQuantity> SupplyToward(PlanId plan)
    {
        if (_toward.TryGetValue(plan, out var cached))
            return cached;
        if (!_board.Index.Plans.TryGetValue(plan, out var entry) || !_board.Policies.MaySee(_viewer, entry.Plan))
            return ValueList<ItemQuantity>.Empty;
        var sums = new Dictionary<ItemKey, int>();
        var order = new List<ItemKey>();
        foreach (var id in Sorted(entry.Plan.BuildBy))
            if (_builders.TryGetValue(id, out var builder) && builder.Says.Supply.TryGet(out var supply) && supply.Report.Toward.TryGetValue(plan, out var items))
                foreach (var item in items)
                {
                    if (!sums.TryGetValue(item.Item, out var had))
                        order.Add(item.Item);
                    sums[item.Item] = had + item.Count;
                }

        order.Sort(static (a, b) => a.Hash.CompareTo(b.Hash));
        var summed = new List<ItemQuantity>(order.Count);
        foreach (var item in order)
            if (ItemQuantity.Create(item, sums[item]).TryGet(out var quantity))
                summed.Add(quantity);
        var list = Lists.Own(summed.ToArray());
        _toward[plan] = list;
        return list;
    }

    public int UnclaimedIn(PlanId plan)
    {
        if (!_board.Index.Plans.TryGetValue(plan, out var entry))
            return 0;
        var counts = _planCounts.TryGetValue(plan, out var live) ? live : default;
        return Math.Max(0, entry.Ghosts.Count - counts.Working - counts.Waiting);
    }

    /// <summary>Ends with the world: the board is no longer watched.</summary>
    public void End() => _board.Committed -= _onBoard;

    // ---- inside ----

    private Builder Read(Entry entry)
    {
        if (entry.Read is { } read)
            return read;
        var says = entry.Says;
        var state = entry.Working > 0 ? WorkingState : says.Word is BuilderWord.Stuck stuck ? new BuilderState.Stuck(stuck.Why) : IdleState;
        var supply = says.Supply;
        if (supply.TryGet(out var had) && Hidden(had.Report.Toward) is { } shown)
            supply = Option.Some(had with { Report = had.Report with { Toward = shown } });
        return entry.Read = new Builder(says.Info, says.Area, state, supply);
    }

    // The Toward entries the viewer may see, or null when it may see them all.
    private ValueMap<PlanId, ValueList<ItemQuantity>>? Hidden(ValueMap<PlanId, ValueList<ItemQuantity>> toward)
    {
        Dictionary<PlanId, ValueList<ItemQuantity>>? kept = null;
        foreach (var pair in toward)
        {
            var seen = _board.Index.Plans.TryGetValue(pair.Key, out var entry) && _board.Policies.MaySee(_viewer, entry.Plan);
            if (seen && kept is null)
                continue;
            if (kept is null)
            {
                kept = new Dictionary<PlanId, ValueList<ItemQuantity>>(toward.Count);
                foreach (var earlier in toward)
                {
                    if (earlier.Key == pair.Key)
                        break;
                    kept[earlier.Key] = earlier.Value;
                }
            }

            if (seen)
                kept[pair.Key] = pair.Value;
        }

        return kept is null ? null : ValueMap<PlanId, ValueList<ItemQuantity>>.From(kept);
    }

    private int WorkingOf(ExecutorId id)
    {
        var working = 0;
        foreach (var tally in _claims.Values)
            if (tally.By == id && tally.Working)
                working++;
        return working;
    }

    private void Count(Tally tally, int step)
    {
        var key = (tally.Plan, tally.By);
        var counts = _counts.TryGetValue(key, out var had) ? had : default;
        counts = tally.Working ? counts with { Working = counts.Working + step } : counts with { Waiting = counts.Waiting + step };
        if (counts == default)
            _counts.Remove(key);
        else
            _counts[key] = counts;
        var plan = _planCounts.TryGetValue(tally.Plan, out var planHad) ? planHad : default;
        plan = tally.Working ? plan with { Working = plan.Working + step } : plan with { Waiting = plan.Waiting + step };
        if (plan == default)
            _planCounts.Remove(tally.Plan);
        else
            _planCounts[tally.Plan] = plan;

        if (!tally.Working || !_builders.TryGetValue(tally.By, out var builder))
            return;
        var wasWorking = builder.Working > 0;
        builder.Working += step;
        if (wasWorking != builder.Working > 0)
            Changed(tally.By, answers: false);
    }

    // A builder's record changed: its reads and the lists it is in are dropped, and it is told once this burst.
    private void Changed(ExecutorId id, bool answers)
    {
        if (_builders.TryGetValue(id, out var entry))
            entry.Read = null;
        _all = null;
        if (answers)
        {
            _assigned.Clear();
            _toward.Clear();
        }

        Told?.Invoke(id);
        if (_changed.Add(id))
            _changedOrder.Add(id);
        if (_queued)
            return;
        _queued = true;
        _board.Events.Queue(new Burst(this));
    }

    private void Raise(BoardEvents events)
    {
        _queued = false;
        var changed = _changedOrder.ToArray();
        _changedOrder.Clear();
        _changed.Clear();
        foreach (var id in changed)
            events.RaiseBuilderChanged(id);
    }

    // Plans opened, changed or finished: what reads plans is dropped; an answer to a plan that no longer assigns its builder goes.
    private void OnBoard(ChangeSet changes)
    {
        foreach (var change in changes.Changed)
            if (change.Before.Plan != change.After.Plan && _claims.TryGetValue(change.After.Id, out var tally))
            {
                Count(tally, -1);
                var moved = tally with { Plan = change.After.Plan };
                _claims[change.After.Id] = moved;
                Count(moved, 1);
            }

        if (changes.PlansOpened.Count == 0 && changes.PlansChanged.Count == 0 && changes.PlansFinished.Count == 0)
            return;
        _assigned.Clear();
        _plansOf.Clear();
        _toward.Clear();
        _all = null;
        foreach (var entry in _builders.Values)
            entry.Read = null;

        List<(ExecutorId, BuilderSays)>? dropped = null;
        foreach (var entry in _builders.Values)
            foreach (var pair in entry.Says.Answers)
                if (!_board.Index.Plans.TryGetValue(pair.Key, out var plan) || !plan.Plan.BuildBy.Contains(entry.Says.Info.Id))
                {
                    (dropped ??= new()).Add((entry.Says.Info.Id, entry.Says with { Answers = entry.Says.Answers.Without(pair.Key) }));
                    break;
                }

        if (dropped is null)
            return;
        foreach (var (id, says) in dropped)
        {
            var kept = says;
            foreach (var pair in says.Answers)
                if (!_board.Index.Plans.TryGetValue(pair.Key, out var plan) || !plan.Plan.BuildBy.Contains(id))
                    kept = kept with { Answers = kept.Answers.Without(pair.Key) };
            Take(kept);
        }
    }

    private static List<ExecutorId> Sorted(ValueSet<ExecutorId> ids)
    {
        var sorted = new List<ExecutorId>(ids.Count);
        foreach (var id in ids)
            sorted.Add(id);
        sorted.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
        return sorted;
    }

    private static Result<Unit, Reason> TooLarge(string list, int count, int most) =>
        Result<Unit, Reason>.Error(Reason.Of(ReasonCodes.TooLarge, WorldExecutors.Rule, ValueMap
            .Of("list", list)
            .With("count", count.ToString())
            .With("most", most.ToString())));

    private sealed class Entry(BuilderSays says, int working)
    {
        public BuilderSays Says { get; set; } = says;

        /// <summary>Its live claims reported working or not reported on.</summary>
        public int Working { get; set; } = working;

        /// <summary>Its record as read, until something it shows changes.</summary>
        public Builder? Read { get; set; }
    }

    private readonly record struct Tally(PlanId Plan, ExecutorId By, bool Working);

    private readonly record struct Counts(int Working, int Waiting);

    private sealed class Burst(BuilderBook book) : QueuedEvent
    {
        public override void Raise(BoardEvents events) => book.Raise(events);
    }
}
