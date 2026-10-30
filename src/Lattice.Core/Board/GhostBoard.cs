using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Plans;
using Lattice.Core.Ports;
using Lattice.Core.Saving;
using Lattice.Core.Slices;

namespace Lattice.Core.Board;

/// <summary>The next id of each kind the board hands out. Counters only grow.</summary>
internal sealed record IdCounters(long NextGhost, long NextPlan, long NextChangeSet);

/// <summary>
/// A board as loaded: its revision, active plans and ghosts, and the id counters. The counters are one above the
/// highest id of each kind written anywhere in the world's plan files, on every branch, not only up to the loaded
/// save's point, so an id used on a later branch is never handed out again.
/// </summary>
internal sealed record BoardSeed(Revision Revision, IdCounters Next, ValueList<Plan> Plans, ValueList<Ghost> Ghosts)
{
    /// <summary>Each plan's count of ghosts done so far; a plan not listed has none.</summary>
    public ValueMap<PlanId, int> Done { get; init; } = ValueMap<PlanId, int>.Empty;

    /// <summary>Of <see cref="Done"/>, each plan's ghosts per executor that held their claim; a plan left out has none.</summary>
    public ValueMap<PlanId, ValueMap<ExecutorId, int>> DoneBy { get; init; } = ValueMap<PlanId, ValueMap<ExecutorId, int>>.Empty;
}

/// <summary>
/// The one holder of a world's ghost and plan state. Applying a primitive command runs in three steps, so no consumer
/// code ever sees a half-changed board: prepare the change from the current state without changing anything, ask the
/// access policy about everything it touches, then commit (one revision, one change set, one queued event). Main
/// thread. Pure: no Unity or game types; pieces are known only through the catalogue.
/// </summary>
internal sealed partial class GhostBoard
{
    /// <summary>The plan loose ghosts go in; it exists on every board.</summary>
    public static readonly PlanId DefaultPlan = new(1);

    private static readonly PlanName DefaultPlanName = PlanName.Create("Loose ghosts")
        .Match(static name => name, static reason => throw new InvalidOperationException(reason.ToString()));

    private static readonly Maker DefaultPlanMaker = Maker.Create(LatticeIdentity.Mod, "board")
        .Match(static maker => maker, static reason => throw new InvalidOperationException(reason.ToString()));

    private readonly Shapes _shapes;
    private readonly ITimeSource _time;
    private readonly PlanSizeLimit _sizeLimit;
    private readonly Dictionary<Actor, BoardView> _views = new();
    private long _nextGhost;
    private long _nextPlan;
    private long _nextChangeSet;

    public GhostBoard(Shapes shapes, ITimeSource time, AccessPolicies policies, PlanSizeLimit sizeLimit, BoardEvents events, Option<BoardSeed> seed)
    {
        _shapes = shapes;
        _time = time;
        Policies = policies;
        _sizeLimit = sizeLimit;
        Events = events;
        if (seed.TryGet(out var loaded))
            Load(loaded);
        else
        {
            (_nextGhost, _nextPlan, _nextChangeSet) = (1, DefaultPlan.Value + 1, 1);
            Index.Plans.Add(DefaultPlan, new PlanEntry(NewDefaultPlan(), default));
        }
    }

    public Revision Revision { get; private set; }

    /// <summary>
    /// Whether an executor is registered, asked when an assign edit is first applied (never on a plan file's replay);
    /// set on the host, none on a client, which only previews.
    /// </summary>
    internal Func<ExecutorId, bool>? Registered { get; set; }

    /// <summary>
    /// The executor holding a ghost's claim, asked as the ghost completes so the completion names it
    /// (<see cref="Completion.By"/>); set on the host.
    /// </summary>
    internal Func<GhostId, Option<ExecutorId>>? ClaimHolder { get; set; }

    /// <summary>
    /// Told each change set as it commits (on the host and on a client's replica), before its event is raised; never for
    /// a preview's staged change. For the counts that must agree with the board at once: a done ghost's claim leaves
    /// the moment its completion lands.
    /// </summary>
    internal Action<ChangeSet>? Committed { get; set; }

    /// <summary>Rises with every commit; read caches built at an older epoch are dropped.</summary>
    public int Epoch { get; private set; }

    public AccessPolicies Policies { get; }

    public BoardEvents Events { get; }

    internal BoardIndex Index { get; } = new();

    /// <summary>The id the next plan created gets; moves only with a commit, so it is fixed at a revision.</summary>
    internal PlanId NextPlan => new(_nextPlan);

    /// <summary>The selectors module, once installed: box, run, network, room and tag selectors.</summary>
    internal IGhostSelectors? Selectors { get; private set; }

    /// <summary>The change sets commands made, for undo.</summary>
    internal ChangeHistory History { get; } = new();

    /// <summary>Saving, once installed: the plans' files behind <see cref="Command.CatchUp"/> and the plans view's saving reads.</summary>
    internal ISavedPlans? Saved { get; private set; }

    /// <summary>The plan-state module's facts and states; none until it is installed.</summary>
    internal IPlanStates? PlanStates { get; private set; }

    /// <summary>The swaps module's fits, once installed: what <see cref="GhostEdit.Swap"/> changes a piece to.</summary>
    internal IPieceSwaps? Swaps { get; private set; }

    /// <summary>The board as an actor may see it; one view per actor, kept for the world.</summary>
    public BoardView ViewFor(Actor viewer)
    {
        if (!_views.TryGetValue(viewer, out var view))
            _views[viewer] = view = new BoardView(this, viewer);
        return view;
    }

    /// <summary>
    /// Applies a primitive command for an actor, or refuses it with nothing changed. A batch is prepared step by step on
    /// the state the earlier steps leave, then asked of the access policy and committed as one change set. Module-owned
    /// commands are refused <see cref="ReasonCodes.ModuleMissing"/> here: they reach the board expanded. CatchUp needs
    /// saving installed (else module-missing) and is refused inside a batch: saving moves the plan's file to its newest
    /// line for that change set instead of writing one. What the access policy throws leaves this call with the board
    /// unchanged. Cost: linear in what the command touches.
    /// </summary>
    public Outcome Apply(CommandId id, Command command, Actor actor)
    {
        if (id == default)
            return Refused(Reason.Of(ReasonCodes.DefaultValue, LatticeIdentity.Board, ValueMap.Of("type", nameof(CommandId))));
        if (DefaultValues.In(command, actor).TryGet(out var defaultValue))
            return Refused(defaultValue);

        var pending = new Pending(new Revision(Revision.Value + 1), _time.Now);
        var prepared = Prepare(command, actor, pending);
        if (!prepared.TryGet(out var ready))
            return new Outcome.Refused(ValueList.Of(prepared.ErrorOf()));

        foreach (var (request, ghosts) in ready.Asks)
            if (Policies.MayChange(request).TryGetError(out var why))
                return new Outcome.Refused(ValueList.Of(new Refusal(why, ghosts)));

        var applied = Commit(ready, id, actor, byCommand: true);
        foreach (var plan in ready.CaughtUp)
            Saved?.CaughtUp(plan, applied.Id);
        return new Outcome.Applied(applied);
    }

    /// <summary>The world shows a part-built structure of the ghost's piece in its spot. Not a command; not policed.</summary>
    public Result<ChangeSet, Reason> MarkStarted(GhostId ghost, StructureRef partBuilt) =>
        Mark(ghost, new GhostState.Started(partBuilt));

    /// <summary>The ghost cannot be done as it is. Not a command; not policed.</summary>
    public Result<ChangeSet, Reason> MarkStale(GhostId ghost, Reason why) => Mark(ghost, new GhostState.Stale(why));

    /// <summary>Nothing of the ghost is built (again). Not a command; not policed.</summary>
    public Result<ChangeSet, Reason> MarkPlanned(GhostId ghost) => Mark(ghost, new GhostState.Planned());

    /// <summary>The world matches the ghost: it leaves the board, and <see cref="IGhostEvents.Completed"/> follows its change set.</summary>
    public Result<ChangeSet, Reason> Complete(GhostId ghost, Completion how)
    {
        if (how.By.IsNone && ClaimHolder is { } holder)
            how = how with { By = holder(ghost) };
        var dropped = Drop(ghost, new RemovalCause.Done(how));
        if (dropped.IsOk)
            Events.Queue(new CompletedEvent(ghost, how));
        return dropped;
    }

    /// <summary>The structure a remove, swap or finish ghost points at is gone: the ghost leaves the board.</summary>
    public Result<ChangeSet, Reason> DropTargetGone(GhostId ghost) => Drop(ghost, new RemovalCause.TargetGone());

    /// <summary>Installs the selectors module for the world; read caches are dropped.</summary>
    internal void InstallSelectors(IGhostSelectors selectors)
    {
        Selectors = selectors;
        Epoch++;
    }

    /// <summary>Installs the plan-state module for the world; read caches are dropped.</summary>
    internal void InstallPlanStates(IPlanStates states)
    {
        PlanStates = states;
        Epoch++;
    }

    /// <summary>Installs the swaps module's fits for the world; read caches are dropped.</summary>
    internal void InstallSwaps(IPieceSwaps swaps)
    {
        Swaps = swaps;
        Epoch++;
    }

    /// <summary>Installs saving for the world; read caches are dropped.</summary>
    internal void InstallSaving(ISavedPlans saved)
    {
        Saved = saved;
        Epoch++;
    }

    /// <summary>The ghosts a selector picks, as a viewer may see them, in id order; refused for kinds the board cannot evaluate.</summary>
    internal Result<List<GhostEntry>, Reason> Select(Selector selector, Actor viewer) =>
        BoardSelect.Ids(Index, selector, Selectors).Map(ids =>
        {
            var picked = new List<GhostEntry>(ids.Count);
            foreach (var id in ids)
                if (Index.Ghosts.TryGetValue(id, out var entry) && Policies.MaySee(viewer, entry.Ghost))
                    picked.Add(entry);
            picked.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
            return picked;
        });

    private Result<Pending, Refusal> Prepare(Command command, Actor actor, Pending pending) => command switch
    {
        Command.Place place => PreparePlace(place, actor, pending),
        Command.Remove remove => PrepareRemove(remove, actor, pending),
        Command.Change change => PrepareChange(change, actor, pending),
        Command.Move move => PrepareMove(move, actor, pending),
        Command.CreatePlan create => PrepareCreatePlan(create, actor, pending),
        Command.EditPlan edit => PrepareEditPlan(edit, actor, pending),
        Command.MoveToPlan move => PrepareMoveToPlan(move, actor, pending),
        Command.Batch batch => PrepareBatch(batch, actor, pending),
        Command.Revert revert => PrepareRevert(revert, actor, pending),
        Command.FinishPlan finish => PrepareFinish(finish, actor, pending),
        Command.CatchUp catchUp => PrepareCatchUp(catchUp, actor, pending),
        Command.Reopen reopen => PrepareReopen(reopen, actor, pending),
        Command.MarkRemoval or Command.PlanSwap or Command.PlanFinish
            or Command.PlanRun or Command.PlanRoute or Command.Transform or Command.PlaceTemplate or Command.Repair
            or Command.TakeDown or Command.PlanSplit or Command.PlaceTemplateIn => Fail(ModuleMissing(command)),
        _ => Fail(Unknown.Kind(command)),
    };

    /// <summary>The refusal for a command the board does not apply itself and no module expanded.</summary>
    public static Reason ModuleMissing(Command command) =>
        Reason.Of(ReasonCodes.ModuleMissing, LatticeIdentity.Board, ValueMap.Of("command", command.GetType().Name));

    /// <summary>
    /// Prepares each step on the state the earlier steps leave: a step is staged into the index once prepared and every
    /// staged step is taken out again before this returns, whatever happens, so the board is unchanged. The result is
    /// the batch's net effect (a ghost added then changed is added; added then removed is not there) with every step's
    /// policy questions. A refused step refuses the batch, its index in the fact <c>step</c> (nested: <c>1.0</c>).
    /// A later step's selector asks the policy's visibility question with the earlier steps staged; whatever that call
    /// throws, the staged steps are taken out first.
    /// </summary>
    private Result<Pending, Refusal> PrepareBatch(Command.Batch batch, Actor actor, Pending pending)
    {
        for (var i = 0; i < batch.Steps.Count; i++)
            if (batch.Steps[i] is Command.CatchUp)
                return Fail(AtStep(new Refusal(Reason.Of(ReasonCodes.InvalidValue, LatticeIdentity.Board,
                    ValueMap.Of("command", nameof(Command.CatchUp)).With("problem", "not-in-a-batch")), ValueList<GhostId>.Empty), i));
        return PrepareInOrder(batch.Steps, actor, pending, numbered: true);
    }

    /// <summary>Prepares commands in order, each on the state the earlier ones leave, and nets them into <paramref name="pending"/>.</summary>
    private Result<Pending, Refusal> PrepareInOrder(IReadOnlyList<Command> steps, Actor actor, Pending pending, bool numbered)
    {
        var staged = new List<Pending>(steps.Count);
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                var prepared = Prepare(steps[i], actor, new Pending(pending.At, pending.Now));
                if (!prepared.TryGet(out var step))
                    return Fail(numbered ? AtStep(prepared.ErrorOf(), i) : prepared.ErrorOf());
                Stage(step, byCommand: true);
                staged.Add(step);
            }
        }
        finally
        {
            for (var i = staged.Count - 1; i >= 0; i--)
                Unstage(staged[i]);
        }

        Net(staged, pending);
        return Ok(pending);
    }

    private static Refusal AtStep(Refusal refusal, int step)
    {
        var at = refusal.Why.Facts.TryGetValue("step", out var inner) ? step + "." + inner : step.ToString();
        return refusal with { Why = refusal.Why with { Facts = refusal.Why.Facts.With("step", at) } };
    }

    private static void Net(List<Pending> steps, Pending net)
    {
        var first = new Dictionary<GhostId, GhostEntry?>();
        var last = new Dictionary<GhostId, (GhostEntry? Entry, RemovalCause? Cause)>();
        var order = new List<GhostId>();
        var firstPlan = new Dictionary<PlanId, Plan?>();
        var lastPlan = new Dictionary<PlanId, Plan>();
        var planOrder = new List<PlanId>();

        void Seen(GhostId id, GhostEntry? before)
        {
            if (first.ContainsKey(id))
                return;
            first.Add(id, before);
            order.Add(id);
        }

        void SeenPlan(PlanId id, Plan? before, Plan after)
        {
            if (!firstPlan.ContainsKey(id))
            {
                firstPlan.Add(id, before);
                planOrder.Add(id);
            }

            lastPlan[id] = after;
        }

        foreach (var step in steps)
        {
            foreach (var (entry, cause) in step.Removed)
            {
                Seen(entry.Id, entry);
                last[entry.Id] = (null, cause);
            }

            foreach (var (before, after) in step.Changed)
            {
                Seen(before.Id, before);
                last[before.Id] = (after, null);
            }

            foreach (var entry in step.Added)
            {
                Seen(entry.Id, null);
                last[entry.Id] = (entry, null);
            }

            foreach (var plan in step.PlansOpened)
                SeenPlan(plan.Id, null, plan);
            foreach (var (before, after) in step.PlansChanged)
                SeenPlan(before.Id, before, after);
            net.PlansFinished.AddRange(step.PlansFinished);
            net.CaughtUp.AddRange(step.CaughtUp);
            net.DoneCounts.AddRange(step.DoneCounts);
            net.Asks.AddRange(step.Asks);
            net.GhostsTaken += step.GhostsTaken;
            net.PlansTaken += step.PlansTaken;
        }

        foreach (var id in order)
        {
            var before = first[id];
            var (after, cause) = last[id];
            if (before is null && after is not null)
                net.Added.Add(after);
            else if (before is not null && after is null)
                net.Removed.Add((before, cause!));
            else if (before is not null && after is not null && !ReferenceEquals(before, after))
                net.Changed.Add((before, after));
        }

        foreach (var id in planOrder)
        {
            var after = lastPlan[id];
            if (firstPlan[id] is { } before)
                net.PlansChanged.Add((before, after));
            else
                net.PlansOpened.Add(after);
        }
    }

    /// <summary>
    /// Undoes one of the actor's own change sets, as a new change: ghosts it added leave, ghosts it removed come back
    /// with their ids, ghosts and plans it changed are set back. Plan changes only: a ghost keeps its lifecycle state, and
    /// a ghost the world has since completed (or whose target is gone) is skipped, never rebuilt or torn down. Refused
    /// when another actor changed one of those ghosts or plans since, or has a ghost in a spot a removed ghost would
    /// come back to or a changed ghost's piece or placement would go back to; one refusal naming every such ghost and, in
    /// <c>actor</c>, who changed them. The actor's own ghost in such a spot is replaced, as a placement replaces it. A
    /// piece going back that cannot share a small cell with a ghost there refuses, naming both. A change set that
    /// opened or finished plans is refused: plans are kept, and finishing is the finished-plans module's.
    /// </summary>
    private Result<Pending, Refusal> PrepareRevert(Command.Revert revert, Actor actor, Pending pending)
    {
        var setFact = revert.ChangeSet.Value.ToString();
        if (!History.Find(revert.ChangeSet).TryGet(out var changes))
            return Fail(NotFound("change-set", revert.ChangeSet.Value));
        if (changes.By != actor)
            return Fail(Reason.Of(ReasonCodes.NotAllowed, LatticeIdentity.Board, ValueMap.Of("change-set", setFact).With("by", ActorText.Of(changes.By))));
        if (changes.PlansOpened.Count > 0)
            return Fail(Reason.Of(ReasonCodes.NotAllowed, LatticeIdentity.Board, ValueMap.Of("change-set", setFact).With("plans-opened", changes.PlansOpened.Count.ToString())));
        if (changes.PlansFinished.Count > 0)
            return Fail(Reason.Of(ReasonCodes.NotAllowed, LatticeIdentity.Board, ValueMap.Of("change-set", setFact).With("plans-finished", changes.PlansFinished.Count.ToString())));

        foreach (var change in changes.PlansChanged)
            if (History.Last(change.After.Id).TryGet(out var last) && last.At.Value > changes.Revision.Value && last.By != actor)
                return Fail(Reason.Of(ReasonCodes.ChangedSince, LatticeIdentity.Board, ValueMap.Of("plan", change.After.Id.Value.ToString()).With("actor", ActorText.Of(last.By))));

        var since = new ChangedByOthers(History, actor, changes.Revision);
        foreach (var ghost in changes.Added)
            since.Check(ghost.Id);
        foreach (var change in changes.Changed)
            since.Check(change.After.Id);
        foreach (var removal in changes.Removed)
            since.Check(removal.Ghost.Id);

        var leaving = new HashSet<GhostId>();
        foreach (var ghost in changes.Added)
            if (Index.Ghosts.TryGetValue(ghost.Id, out var entry))
            {
                leaving.Add(ghost.Id);
                pending.Removed.Add((entry, new RemovalCause.Removed()));
            }

        var reverting = new List<(GhostEntry Current, GhostEntry Back)>(changes.Changed.Count);
        var reshaped = new Dictionary<GhostId, GhostEntry>();
        foreach (var change in changes.Changed)
            if (!leaving.Contains(change.After.Id) && Index.Ghosts.TryGetValue(change.After.Id, out var current))
            {
                var back = change.Before with { Revision = pending.At, State = current.Ghost.State };
                if (back.Intent.Equals(current.Ghost.Intent))
                {
                    reverting.Add((current, current.With(back, Option.Some(pending.At))));
                    continue;
                }

                var resolved = Resolve(back.Intent);
                if (!resolved.TryGet(out var parts))
                    return Fail(new Refusal(resolved.ErrorOf(), ValueList.Of(back.Id)));
                var entry = new GhostEntry(back, parts.Shape, parts.Target, pending.At);
                reshaped.Add(entry.Id, entry);
                reverting.Add((current, entry));
            }

        foreach (var removal in changes.Removed)
        {
            if (Index.Ghosts.ContainsKey(removal.Ghost.Id))
                continue;
            var restored = removal.Ghost with { Revision = pending.At };
            if (!Index.Plans.ContainsKey(restored.Plan))
                return Fail(new Refusal(NotFound("plan", restored.Plan.Value), ValueList.Of(restored.Id)));
            var resolved = Resolve(restored.Intent);
            if (!resolved.TryGet(out var parts))
                return Fail(new Refusal(resolved.ErrorOf(), ValueList.Of(restored.Id)));
            var entry = new GhostEntry(restored, parts.Shape, parts.Target, pending.At);
            foreach (var holder in Holders(entry))
                if (!reshaped.ContainsKey(holder) && leaving.Add(holder) && since.CheckHolder(holder, Index.Ghosts[holder].Ghost.PlacedBy))
                    pending.Removed.Add((Index.Ghosts[holder], new RemovalCause.Replaced(restored.Id)));

            pending.Added.Add(entry);
        }

        foreach (var entry in reshaped.Values)
            foreach (var holder in Holders(entry))
                if (holder != entry.Id && !reshaped.ContainsKey(holder) && leaving.Add(holder) &&
                    since.CheckHolder(holder, Index.Ghosts[holder].Ghost.PlacedBy))
                    pending.Removed.Add((Index.Ghosts[holder], new RemovalCause.Replaced(entry.Id)));

        if (since.Refusal() is { } changedSince)
            return Fail(changedSince);

        foreach (var (current, back) in reverting)
            if (!leaving.Contains(current.Id))
                pending.Changed.Add((current, back));
        foreach (var entry in reshaped.Values)
            if (!leaving.Contains(entry.Id) && SmallClash(entry, reshaped, leaving, pending.Added) is { } clash)
                return Fail(clash);

        foreach (var change in changes.PlansChanged)
            pending.PlansChanged.Add((Index.Plans[change.After.Id].Plan, change.Before with { Revision = pending.At }));

        var plans = new SortedSet<long>();
        foreach (var entry in pending.Added)
            plans.Add(entry.Ghost.Plan.Value);
        foreach (var (before, after) in pending.Changed)
            if (before.Ghost.Plan != after.Ghost.Plan)
                plans.Add(after.Ghost.Plan.Value);
        foreach (var plan in plans)
            if (Admit(new PlanId(plan), pending) is { } tooLarge)
                return Fail(tooLarge);

        foreach (var (entry, _) in pending.Removed)
            Ask(pending, new AccessRequest(actor, revert, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(entry.Id));
        foreach (var entry in pending.Added)
            Ask(pending, new AccessRequest(actor, revert, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(entry.Id));
        foreach (var (before, _) in pending.Changed)
            Ask(pending, new AccessRequest(actor, revert, before.Ghost.Plan, Option.Some(before.Ghost)), ValueList.Of(before.Id));
        foreach (var (before, _) in pending.PlansChanged)
            Ask(pending, new AccessRequest(actor, revert, before.Id, Option<Ghost>.None), ValueList<GhostId>.Empty);
        return Ok(pending);
    }

    /// <summary>The ghosts now holding a spot or the target of the entry.</summary>
    private List<GhostId> Holders(GhostEntry entry)
    {
        var holders = new List<GhostId>();
        if (entry.Shape.TryGet(out var shape))
            foreach (var spot in shape.Spots)
                if (Index.TryOccupant(spot, out var holder) && !holders.Contains(holder))
                    holders.Add(holder);
        if (entry.Target.TryGet(out var target) && Index.TryTargeting(target.Id, out var targeting) && !holders.Contains(targeting))
            holders.Add(targeting);
        return holders;
    }

    private Result<Pending, Refusal> PreparePlace(Command.Place place, Actor actor, Pending pending)
    {
        var drafts = place.Ghosts;
        var kept = new GhostEntry?[drafts.Count];
        var draftSpots = new Dictionary<SpotKey, int>();
        var draftTargets = new Dictionary<ReferenceId, int>();
        var draftCells = new Dictionary<Cell, List<int>>();
        var replaced = new Dictionary<GhostId, int>();
        var plans = new SortedSet<long>();

        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            var plan = draft.Plan.Reduce(DefaultPlan);
            if (!Index.Plans.ContainsKey(plan))
                return Fail(NotFound("plan", plan.Value));
            var resolved = Resolve(draft.Intent);
            if (!resolved.TryGet(out var parts))
                return Fail(resolved.ErrorOf());
            var (shape, target) = parts;
            var id = new GhostId(_nextGhost + i);

            void Take(int earlier)
            {
                if (kept[earlier] is not { } dropped)
                    return;
                kept[earlier] = null;
                Forget(dropped, earlier, draftSpots, draftTargets, draftCells);
                var moved = new List<GhostId>();
                foreach (var (old, by) in replaced)
                    if (by == earlier)
                        moved.Add(old);
                foreach (var old in moved)
                    replaced[old] = i;
            }

            if (shape.TryGet(out var placed))
                foreach (var spot in placed.Spots)
                {
                    if (Index.TryOccupant(spot, out var old))
                        replaced.TryAdd(old, i);
                    if (draftSpots.TryGetValue(spot, out var earlier))
                        Take(earlier);
                }

            if (target.TryGet(out var structure))
            {
                if (Index.TryTargeting(structure.Id, out var old))
                    replaced.TryAdd(old, i);
                if (draftTargets.TryGetValue(structure.Id, out var earlier))
                    Take(earlier);
            }

            if (shape.TryGet(out var small) && small.IsSmall && Conflict(small, replaced, kept, draftCells) is { } refusal)
                return Fail(refusal);

            var ghost = new Ghost(
                id, plan, pending.At, draft.Intent, new GhostState.Planned(), actor, draft.MadeBy, pending.Now,
                draft.Visibility, draft.Label, draft.Tint, draft.Groups, draft.Attached);
            var entry = new GhostEntry(ghost, shape, target, pending.At);
            kept[i] = entry;
            Remember(entry, i, draftSpots, draftTargets, draftCells);
            plans.Add(plan.Value);
        }

        pending.GhostsTaken = drafts.Count;
        foreach (var entry in kept)
            if (entry is not null)
                pending.Added.Add(entry);

        var removedIds = new List<GhostId>(replaced.Keys);
        removedIds.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        foreach (var old in removedIds)
            pending.Removed.Add((Index.Ghosts[old], new RemovalCause.Replaced(new GhostId(_nextGhost + replaced[old]))));

        foreach (var planValue in plans)
        {
            var plan = new PlanId(planValue);
            if (Admit(plan, pending) is { } tooLarge)
                return Fail(tooLarge);
            Ask(pending, new AccessRequest(actor, place, plan, Option<Ghost>.None), ValueList<GhostId>.Empty);
        }

        foreach (var (entry, _) in pending.Removed)
            Ask(pending, new AccessRequest(actor, place, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(entry.Id));
        return Ok(pending);
    }

    private Result<Pending, Refusal> PrepareRemove(Command.Remove remove, Actor actor, Pending pending)
    {
        var selected = Selected(remove.Which, actor);
        if (!selected.TryGet(out var entries))
            return Fail(selected.ErrorOf());
        foreach (var entry in entries)
        {
            pending.Removed.Add((entry, new RemovalCause.Removed()));
            Ask(pending, new AccessRequest(actor, remove, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(entry.Id));
        }

        return Ok(pending);
    }

    private Result<Pending, Refusal> PrepareChange(Command.Change change, Actor actor, Pending pending)
    {
        if (change.Edit is GhostEdit.Swap swap)
            return PrepareSwap(change, swap.To, actor, pending);
        var selected = Selected(change.Which, actor);
        if (!selected.TryGet(out var entries))
            return Fail(selected.ErrorOf());
        if (change.UnchangedSince.TryGet(out var since) && ChangedSince(entries, since, pending.At) is { } changedSince)
            return Fail(changedSince);

        foreach (var entry in entries)
        {
            var edited = Edits.Apply(entry.Ghost, change.Edit, pending.At);
            if (!edited.TryGet(out var after))
                return Fail(new Refusal(edited.ErrorOf(), ValueList.Of(entry.Id)));
            pending.Changed.Add((entry, entry.With(after, Option.Some(pending.At))));
            Ask(pending, new AccessRequest(actor, change, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(entry.Id));
        }

        return Ok(pending);
    }

    /// <summary>
    /// The swap edit: each picked ghost the swaps module can change takes its new piece and shape, ids and fields kept;
    /// the rest are left as they are. Nothing picked changes nothing. Refused with the first ghost's reason (fact
    /// <c>picked</c>) when ghosts were picked and none could swap, and as a placement for a new piece that clashes in a
    /// small cell with a ghost there (naming both). Linear in the picked ghosts and the ghosts in their small cells.
    /// </summary>
    private Result<Pending, Refusal> PrepareSwap(Command.Change change, SwapTo to, Actor actor, Pending pending)
    {
        if (Swaps is not { } swaps)
            return Fail(Reason.Of(ReasonCodes.ModuleMissing, LatticeIdentity.Board, ValueMap.Of("module", "swaps")));
        if (swaps.Unknown(to).TryGet(out var unknown))
            return Fail(unknown);
        var selected = Select(change.Which, actor);
        if (!selected.TryGet(out var entries))
            return Fail(selected.ErrorOf());
        if (change.UnchangedSince.TryGet(out var since) && ChangedSince(entries, since, pending.At) is { } changedSince)
            return Fail(changedSince);

        var swappable = 0;
        (Reason Why, GhostId Ghost)? first = null;
        var changed = new Dictionary<GhostId, GhostEntry>();
        foreach (var entry in entries)
        {
            var swapped = Edits.Swap(entry.Ghost, to, swaps, pending.At);
            if (swapped.Why is { } leftOut)
            {
                first ??= (leftOut, entry.Id);
                continue;
            }

            swappable++;
            if (swapped.After is not { } edited)
                continue;
            var resolved = Resolve(edited.Intent);
            if (!resolved.TryGet(out var parts))
                return Fail(new Refusal(resolved.ErrorOf(), ValueList.Of(entry.Id)));
            var after = new GhostEntry(edited, parts.Shape, parts.Target, pending.At);
            changed.Add(entry.Id, after);
            pending.Changed.Add((entry, after));
            Ask(pending, new AccessRequest(actor, change, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(entry.Id));
        }

        if (swappable == 0 && first is { } refused)
            return Fail(new Refusal(
                refused.Why with { Facts = refused.Why.Facts.With("picked", entries.Count.ToString()) }, ValueList.Of(refused.Ghost)));
        foreach (var (_, after) in pending.Changed)
            if (SmallClash(after, changed, null, null) is { } clash)
                return Fail(clash);
        return Ok(pending);
    }

    /// <summary>
    /// A ghost's new small piece against every other ghost in its cells as they stand after the change: those in
    /// <paramref name="changed"/> with their new shapes, those in <paramref name="leaving"/> gone, those in
    /// <paramref name="added"/> there too. Linear in the ghosts in its cells and the added ones.
    /// </summary>
    private Refusal? SmallClash(GhostEntry after, Dictionary<GhostId, GhostEntry> changed, HashSet<GhostId>? leaving, List<GhostEntry>? added)
    {
        if (!after.Shape.TryGet(out var shape) || !shape.IsSmall)
            return null;
        foreach (var spot in shape.Spots)
            foreach (var other in Index.InCell(spot.Cell))
            {
                if (other == after.Id || (leaving is not null && leaving.Contains(other)))
                    continue;
                var theirs = changed.TryGetValue(other, out var moved) ? moved.Shape : Index.Ghosts[other].Shape;
                if (theirs.TryGet(out var their) && Shapes.Conflict(shape, their).TryGet(out var code))
                    return new Refusal(Clash(code, shape, their), Lists.Own(new[] { after.Id, other }));
            }

        if (added is not null)
            foreach (var entry in added)
                if (entry.Shape.TryGet(out var their) && Shapes.Conflict(shape, their).TryGet(out var code))
                    return new Refusal(Clash(code, shape, their), Lists.Own(new[] { after.Id, entry.Id }));
        return null;
    }

    private Result<Pending, Refusal> PrepareMoveToPlan(Command.MoveToPlan move, Actor actor, Pending pending)
    {
        if (!Index.Plans.ContainsKey(move.To))
            return Fail(NotFound("plan", move.To.Value));
        var selected = Selected(move.Which, actor);
        if (!selected.TryGet(out var entries))
            return Fail(selected.ErrorOf());

        foreach (var entry in entries)
        {
            var after = entry.Ghost with { Plan = move.To, Revision = pending.At };
            pending.Changed.Add((entry, entry.With(after, Option.Some(pending.At))));
            Ask(pending, new AccessRequest(actor, move, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(entry.Id));
        }

        if (Admit(move.To, pending) is { } tooLarge)
            return Fail(tooLarge);
        Ask(pending, new AccessRequest(actor, move, move.To, Option<Ghost>.None), ValueList<GhostId>.Empty);
        return Ok(pending);
    }

    private Result<Pending, Refusal> PrepareCreatePlan(Command.CreatePlan create, Actor actor, Pending pending)
    {
        if (create.Ghosts.Count > 0)
            return PrepareCreateWithGhosts(create, actor, pending);
        var draft = create.Draft;
        var plan = new Plan(new PlanId(_nextPlan), pending.At, draft.Name, actor, draft.MadeBy, draft.Visibility, draft.HigherLayerData) { Colour = draft.Colour };
        pending.PlansTaken = 1;
        pending.PlansOpened.Add(plan);
        Ask(pending, new AccessRequest(actor, create, plan.Id, Option<Ghost>.None), ValueList<GhostId>.Empty);
        return Ok(pending);
    }

    private Result<Pending, Refusal> PrepareEditPlan(Command.EditPlan edit, Actor actor, Pending pending)
    {
        if (!Index.Plans.TryGetValue(edit.Plan, out var entry) || !Policies.MaySee(actor, entry.Plan))
            return Fail(NotFound("plan", edit.Plan.Value));
        if (edit.UnchangedSince.TryGet(out var since) && entry.CommandRevision.Value > since.Value && entry.CommandRevision != pending.At)
            return Fail(Reason.Of(ReasonCodes.ChangedSince, LatticeIdentity.Board, ValueMap.Of("plan", edit.Plan.Value.ToString())));
        if (edit.Edit is PlanEdit.Assign { Builder: var builder } && !entry.Plan.BuildBy.Contains(builder) && Registered is { } registered && !registered(builder))
            return Fail(Reason.Of(ReasonCodes.NotFound, LatticeIdentity.Board, ValueMap.Of("executor", builder.Value).With("problem", "executor")));
        var edited = Edits.Apply(entry.Plan, edit.Edit, pending.At);
        if (!edited.TryGet(out var after))
            return Fail(edited.ErrorOf());
        if (!ReferenceEquals(after, entry.Plan))
            pending.PlansChanged.Add((entry.Plan, after));
        Ask(pending, new AccessRequest(actor, edit, edit.Plan, Option<Ghost>.None), ValueList<GhostId>.Empty);
        return Ok(pending);
    }

    /// <summary>
    /// Creates the plan and places the ghosts in it in one step: the drafts' plan fields name the new plan, so it is
    /// right inside any batch. A refusal of the placement is the command's own; every policy question names this command.
    /// </summary>
    private Result<Pending, Refusal> PrepareCreateWithGhosts(Command.CreatePlan create, Actor actor, Pending pending)
    {
        var plan = Option.Some(new PlanId(_nextPlan));
        var drafts = new GhostDraft[create.Ghosts.Count];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = create.Ghosts[i] with { Plan = plan };
        var steps = new Command[] { new Command.CreatePlan(create.Draft), new Command.Place(Lists.Own(drafts)) };
        var prepared = PrepareInOrder(steps, actor, pending, numbered: false);
        if (!prepared.IsOk)
            return prepared;
        for (var i = 0; i < pending.Asks.Count; i++)
            pending.Asks[i] = (pending.Asks[i].Request with { Command = create }, pending.Asks[i].Ghosts);
        return prepared;
    }

    /// <summary>
    /// Moves build ghosts, ids kept: all of them leave their spots before any lands, so a run shifted onto itself
    /// replaces nothing of its own. Ghosts outside the moved set in a landing spot or target are replaced (the replace
    /// rule of Place); small-cell clashes with what stays, or between moved ghosts, refuse as Place does.
    /// </summary>
    private Result<Pending, Refusal> PrepareMove(Command.Move move, Actor actor, Pending pending)
    {
        if (move.To.Count == 0)
            return Fail(Reason.InvalidValue(nameof(Command.Move), "0", "no-ghosts"));
        var entries = new List<GhostEntry>(move.To.Count);
        var missing = new List<GhostId>();
        foreach (var pair in move.To)
        {
            if (Index.Ghosts.TryGetValue(pair.Key, out var found) && Policies.MaySee(actor, found.Ghost))
                entries.Add(found);
            else
                missing.Add(pair.Key);
        }

        if (missing.Count > 0)
        {
            missing.Sort(static (a, b) => a.Value.CompareTo(b.Value));
            return Fail(new Refusal(Reason.Of(ReasonCodes.NotFound, LatticeIdentity.Board), ValueList.From(missing)));
        }

        entries.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        if (move.UnchangedSince.TryGet(out var since) && ChangedSince(entries, since, pending.At) is { } changedSince)
            return Fail(changedSince);

        var moving = new HashSet<GhostId>();
        foreach (var entry in entries)
            moving.Add(entry.Id);

        var landing = new Dictionary<SpotKey, GhostId>();
        var smallCells = new Dictionary<Cell, List<PlacedShape>>();
        var replaced = new Dictionary<GhostId, GhostId>();
        var replacedOrder = new List<GhostId>();
        foreach (var entry in entries)
        {
            if (entry.Ghost.Intent is not Intent.Build build)
                return Fail(new Refusal(Reason.InvalidValue(nameof(Command.Move), entry.Ghost.Intent.GetType().Name, "not-a-build-ghost"), ValueList.Of(entry.Id)));
            var to = move.To[entry.Id];
            var resolved = _shapes.Of(to);
            if (!resolved.TryGet(out var shape))
                return Fail(new Refusal(resolved.ErrorOf(), ValueList.Of(entry.Id)));

            foreach (var spot in shape.Spots)
            {
                if (landing.TryGetValue(spot, out var other))
                    return Fail(new Refusal(Reason.InvalidValue(nameof(Command.Move), other.Value + "," + entry.Id.Value, "same-spot"), Lists.Own(new[] { other, entry.Id })));
                landing.Add(spot, entry.Id);
                if (Index.TryOccupant(spot, out var holder) && !moving.Contains(holder) && !replaced.ContainsKey(holder))
                {
                    replaced.Add(holder, entry.Id);
                    replacedOrder.Add(holder);
                }
            }

            if (shape.IsSmall)
            {
                foreach (var spot in shape.Spots)
                {
                    foreach (var other in Index.InCell(spot.Cell))
                        if (!moving.Contains(other) && !replaced.ContainsKey(other) && Index.Ghosts[other].Shape.TryGet(out var theirs) &&
                            Shapes.Conflict(shape, theirs).TryGet(out var code))
                            return Fail(new Refusal(Clash(code, shape, theirs), Lists.Own(new[] { entry.Id, other })));
                    if (smallCells.TryGetValue(spot.Cell, out var moved))
                        foreach (var theirs in moved)
                            if (Shapes.Conflict(shape, theirs).TryGet(out var code))
                                return Fail(new Refusal(Clash(code, shape, theirs), ValueList.Of(entry.Id)));
                }

                foreach (var spot in shape.Spots)
                {
                    if (!smallCells.TryGetValue(spot.Cell, out var list))
                        smallCells[spot.Cell] = list = new List<PlacedShape>(2);
                    if (!list.Contains(shape))
                        list.Add(shape);
                }
            }

            var after = entry.Ghost with { Intent = build with { Target = to }, Revision = pending.At };
            pending.Changed.Add((entry, new GhostEntry(after, Option.Some(shape), Option<StructureRef>.None, pending.At)));
            Ask(pending, new AccessRequest(actor, move, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(entry.Id));
        }

        replacedOrder.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        foreach (var old in replacedOrder)
        {
            var entry = Index.Ghosts[old];
            pending.Removed.Add((entry, new RemovalCause.Replaced(replaced[old])));
            Ask(pending, new AccessRequest(actor, move, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(old));
        }

        return Ok(pending);
    }

    /// <summary>
    /// Finishes a plan: it leaves the board, with every ghost still in it (cause removed), and the change set lists it
    /// as finished; saving keeps it as a record. The default plan is never finished (not-allowed, fact <c>plan</c>).
    /// </summary>
    private Result<Pending, Refusal> PrepareFinish(Command.FinishPlan finish, Actor actor, Pending pending)
    {
        if (finish.Plan == DefaultPlan)
            return Fail(Reason.Of(ReasonCodes.NotAllowed, LatticeIdentity.Board, ValueMap.Of("plan", DefaultPlan.Value.ToString()).With("problem", "default-plan")));
        if (!Index.Plans.TryGetValue(finish.Plan, out var plan) || !Policies.MaySee(actor, plan.Plan))
            return Fail(NotFound("plan", finish.Plan.Value));

        foreach (var id in Sorted(plan.Ghosts))
        {
            var entry = Index.Ghosts[id];
            pending.Removed.Add((entry, new RemovalCause.Removed()));
            Ask(pending, new AccessRequest(actor, finish, finish.Plan, Option.Some(entry.Ghost)), ValueList.Of(id));
        }

        pending.PlansFinished.Add(plan);
        Ask(pending, new AccessRequest(actor, finish, finish.Plan, Option<Ghost>.None), ValueList<GhostId>.Empty);
        return Ok(pending);
    }

    /// <summary>
    /// Moves a plan to the newest state its file holds beyond the loaded save (crash recovery): ghosts keep their ids;
    /// ghosts the board has and the file does not leave, the file's ghosts come back or are set as written, a plan the
    /// save was older than opens, and a plan finished in the file finishes. A ghost of another plan in a spot or target
    /// a restored ghost holds is replaced. Refused not-found when the file holds nothing beyond the save.
    /// </summary>
    private Result<Pending, Refusal> PrepareCatchUp(Command.CatchUp catchUp, Actor actor, Pending pending)
    {
        if (Saved is not { } saved)
            return Fail(Reason.Of(ReasonCodes.ModuleMissing, LatticeIdentity.Board, ValueMap.Of("module", "saving")));
        var onBoard = Index.Plans.TryGetValue(catchUp.Plan, out var plan);
        if (onBoard && !Policies.MaySee(actor, plan!.Plan))
            return Fail(NotFound("plan", catchUp.Plan.Value));
        var found = saved.Newest(catchUp.Plan);
        if (!found.TryGet(out var newest))
            return Fail(found.ErrorOf());

        if (newest.Record.TryGet(out var record))
        {
            var written = record with { Revision = pending.At };
            if (!onBoard)
                pending.PlansOpened.Add(written);
            else if (plan!.Plan with { Revision = default } != record with { Revision = default })
                pending.PlansChanged.Add((plan.Plan, written));
        }
        else if (!onBoard)
        {
            return Fail(NotFound("plan", catchUp.Plan.Value));
        }

        var target = new Dictionary<GhostId, Ghost>();
        if (!newest.Finished)
            foreach (var ghost in newest.Ghosts)
                target[ghost.Id] = ghost;

        if (onBoard)
            foreach (var id in Sorted(plan!.Ghosts))
                if (!target.ContainsKey(id))
                    pending.Removed.Add((Index.Ghosts[id], new RemovalCause.Removed()));

        var leaving = new HashSet<GhostId>();
        foreach (var (entry, _) in pending.Removed)
            leaving.Add(entry.Id);
        var ids = new List<GhostId>(target.Keys);
        ids.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        foreach (var id in ids)
        {
            var ghost = target[id];
            var exists = Index.Ghosts.TryGetValue(id, out var current);
            if (exists && current!.Ghost with { Revision = default } == ghost with { Revision = default })
                continue;
            var resolved = Resolve(ghost.Intent);
            if (!resolved.TryGet(out var parts))
                return Fail(new Refusal(resolved.ErrorOf(), ValueList.Of(id)));
            var entry = new GhostEntry(ghost with { Revision = pending.At }, parts.Shape, parts.Target, pending.At);
            foreach (var holder in Holders(entry))
                if (!target.ContainsKey(holder) && leaving.Add(holder))
                    pending.Removed.Add((Index.Ghosts[holder], new RemovalCause.Replaced(id)));
            if (exists)
                pending.Changed.Add((current!, entry));
            else
                pending.Added.Add(entry);
        }

        if (newest.Finished)
            pending.PlansFinished.Add(onBoard ? plan! : new PlanEntry(record!, pending.At));
        else
            pending.DoneCounts.Add((catchUp.Plan, newest.Done, DoneBy.From(newest.DoneBy)));
        pending.CaughtUp.Add(catchUp.Plan);

        foreach (var (entry, _) in pending.Removed)
            Ask(pending, new AccessRequest(actor, catchUp, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(entry.Id));
        foreach (var (before, _) in pending.Changed)
            Ask(pending, new AccessRequest(actor, catchUp, before.Ghost.Plan, Option.Some(before.Ghost)), ValueList.Of(before.Id));
        foreach (var entry in pending.Added)
            Ask(pending, new AccessRequest(actor, catchUp, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(entry.Id));
        Ask(pending, new AccessRequest(actor, catchUp, catchUp.Plan, Option<Ghost>.None), ValueList<GhostId>.Empty);
        return Ok(pending);
    }

    /// <summary>
    /// Makes a finished plan active again with its own id and record, and gives back, ids kept, the ghosts it still held
    /// when it finished; a ghost of another plan in a spot or target one of them holds is replaced. Saving writes the
    /// opening line after the finishing line, so the plan's history runs on unbroken. Needs saving (else module-missing);
    /// refused not-allowed (<c>problem</c> = <c>not-finished</c>) for a plan on the board, not-found for a plan the actor
    /// may not see or the world has no file for. Linear in the plan's lines.
    /// </summary>
    private Result<Pending, Refusal> PrepareReopen(Command.Reopen reopen, Actor actor, Pending pending)
    {
        if (Saved is not { } saved)
            return Fail(Reason.Of(ReasonCodes.ModuleMissing, LatticeIdentity.Board, ValueMap.Of("module", "saving")));
        if (Index.Plans.ContainsKey(reopen.Plan))
            return Fail(Reason.Of(ReasonCodes.NotAllowed, LatticeIdentity.Board, ValueMap.Of("plan", reopen.Plan.Value.ToString()).With("problem", "not-finished")));
        var read = saved.FinishedContents(reopen.Plan);
        if (!read.TryGet(out var contents))
            return Fail(read.ErrorOf());
        if (!Policies.MaySee(actor, contents.Record))
            return Fail(NotFound("plan", reopen.Plan.Value));

        pending.PlansOpened.Add(contents.Record with { Revision = pending.At });
        pending.DoneCounts.Add((reopen.Plan, contents.Done, DoneBy.From(contents.DoneBy)));
        var back = new HashSet<GhostId>();
        foreach (var ghost in contents.Left)
            back.Add(ghost.Id);
        var leaving = new HashSet<GhostId>();
        foreach (var ghost in contents.Left)
        {
            var resolved = Resolve(ghost.Intent);
            if (!resolved.TryGet(out var parts))
                return Fail(new Refusal(resolved.ErrorOf(), ValueList.Of(ghost.Id)));
            var entry = new GhostEntry(ghost with { Revision = pending.At }, parts.Shape, parts.Target, pending.At);
            foreach (var holder in Holders(entry))
                if (!back.Contains(holder) && leaving.Add(holder))
                    pending.Removed.Add((Index.Ghosts[holder], new RemovalCause.Replaced(ghost.Id)));
            pending.Added.Add(entry);
        }

        foreach (var (entry, _) in pending.Removed)
            Ask(pending, new AccessRequest(actor, reopen, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(entry.Id));
        foreach (var entry in pending.Added)
            Ask(pending, new AccessRequest(actor, reopen, entry.Ghost.Plan, Option.Some(entry.Ghost)), ValueList.Of(entry.Id));
        Ask(pending, new AccessRequest(actor, reopen, reopen.Plan, Option<Ghost>.None), ValueList<GhostId>.Empty);
        return Ok(pending);
    }

    private static long[] SortedValues(HashSet<GhostId> ids)
    {
        var values = new long[ids.Count];
        var at = 0;
        foreach (var id in ids)
            values[at++] = id.Value;
        Array.Sort(values);
        return values;
    }

    private static List<GhostId> Sorted(HashSet<GhostId> ids)
    {
        var values = SortedValues(ids);
        var sorted = new List<GhostId>(values.Length);
        foreach (var value in values)
            sorted.Add(new GhostId(value));
        return sorted;
    }

    private Result<ChangeSet, Reason> Mark(GhostId ghost, GhostState state)
    {
        if (!Index.Ghosts.TryGetValue(ghost, out var entry))
            return Result<ChangeSet, Reason>.Error(NotFound("ghost", ghost.Value));
        var pending = new Pending(new Revision(Revision.Value + 1), _time.Now);
        pending.Changed.Add((entry, entry.With(entry.Ghost with { State = state, Revision = pending.At }, Option<Revision>.None)));
        return Result<ChangeSet, Reason>.Ok(Commit(pending, new CommandId(Guid.NewGuid()), LatticeIdentity.Actor, byCommand: false));
    }

    private Result<ChangeSet, Reason> Drop(GhostId ghost, RemovalCause cause)
    {
        if (!Index.Ghosts.TryGetValue(ghost, out var entry))
            return Result<ChangeSet, Reason>.Error(NotFound("ghost", ghost.Value));
        var pending = new Pending(new Revision(Revision.Value + 1), _time.Now);
        pending.Removed.Add((entry, cause));
        return Result<ChangeSet, Reason>.Ok(Commit(pending, new CommandId(Guid.NewGuid()), LatticeIdentity.Actor, byCommand: false));
    }

    private ChangeSet Commit(Pending pending, CommandId command, Actor actor, bool byCommand)
    {
        Stage(pending, byCommand);
        Revision = pending.At;
        Epoch++;

        var changes = ChangesOf(pending, new ChangeSetId(_nextChangeSet++), command, actor);
        if (byCommand)
            History.Record(changes);
        Events.Queue(new ChangedEvent(changes));
        Committed?.Invoke(changes);
        return changes;
    }

    private static ChangeSet ChangesOf(Pending pending, ChangeSetId id, CommandId command, Actor actor) => new(
        id,
        command,
        actor,
        pending.At,
        pending.Now,
        Lists.Own(Map(pending.Added, static e => e.Ghost)),
        Lists.Own(Map(pending.Changed, static c => new GhostChange(c.Before.Ghost, c.After.Ghost))),
        Lists.Own(Map(pending.Removed, static r => new GhostRemoval(r.Entry.Ghost, r.Cause))),
        Lists.Own(pending.PlansOpened.ToArray()),
        Lists.Own(Map(pending.PlansChanged, static c => new PlanChange(c.Before, c.After))),
        Lists.Own(Map(pending.PlansFinished, static e => e.Plan.Id)))
    {
        DoneSet = DoneSetOf(pending.DoneCounts),
        DoneBySet = DoneBySetOf(pending.DoneCounts),
    };

    /// <summary>The per-executor counts a change sets, the last for each plan, for the plans that have any.</summary>
    private static ValueMap<PlanId, ValueMap<ExecutorId, int>> DoneBySetOf(List<(PlanId Plan, int Done, DoneBy DoneBy)> counts)
    {
        if (counts.Count == 0)
            return ValueMap<PlanId, ValueMap<ExecutorId, int>>.Empty;
        var last = new Dictionary<PlanId, DoneBy>(counts.Count);
        foreach (var (plan, _, doneBy) in counts)
            last[plan] = doneBy;
        var set = new Dictionary<PlanId, ValueMap<ExecutorId, int>>(last.Count);
        foreach (var pair in last)
            if (pair.Value.Count > 0)
                set[pair.Key] = pair.Value.ToMap();
        return set.Count == 0 ? ValueMap<PlanId, ValueMap<ExecutorId, int>>.Empty : ValueMap<PlanId, ValueMap<ExecutorId, int>>.From(set);
    }

    /// <summary>The done counts a change sets, the last for each plan.</summary>
    private static ValueMap<PlanId, int> DoneSetOf(List<(PlanId Plan, int Done, DoneBy DoneBy)> counts)
    {
        if (counts.Count == 0)
            return ValueMap<PlanId, int>.Empty;
        var last = new Dictionary<PlanId, int>(counts.Count);
        foreach (var (plan, done, _) in counts)
            last[plan] = done;
        return ValueMap<PlanId, int>.From(last);
    }

    /// <summary>Puts a prepared change into the index and takes its ids; <see cref="Unstage"/> undoes exactly this.</summary>
    private void Stage(Pending pending, bool byCommand)
    {
        foreach (var (entry, cause) in pending.Removed)
        {
            Index.Remove(entry);
            if (cause is RemovalCause.Done done && Index.Plans.TryGetValue(entry.Ghost.Plan, out var donePlan))
            {
                donePlan.Done++;
                if (done.How.By.TryGet(out var by))
                    donePlan.DoneBy = donePlan.DoneBy.Plus(by, 1);
            }
        }
        foreach (var (before, _) in pending.Changed)
            Index.Remove(before);
        foreach (var plan in pending.PlansOpened)
            Index.Plans.Add(plan.Id, new PlanEntry(plan, pending.At));
        pending.PlanRevisionsBefore.Clear();
        foreach (var (_, after) in pending.PlansChanged)
        {
            var entry = Index.Plans[after.Id];
            pending.PlanRevisionsBefore.Add(entry.CommandRevision);
            entry.Plan = after;
            if (byCommand)
                entry.CommandRevision = pending.At;
        }

        foreach (var entry in pending.PlansFinished)
            Index.Plans.Remove(entry.Plan.Id);

        pending.DoneBefore.Clear();
        foreach (var (plan, done, doneBy) in pending.DoneCounts)
        {
            var entry = Index.Plans[plan];
            pending.DoneBefore.Add((entry.Done, entry.DoneBy));
            entry.Done = done;
            entry.DoneBy = doneBy;
        }

        foreach (var (_, after) in pending.Changed)
            Index.Add(after);
        foreach (var entry in pending.Added)
            Index.Add(entry);

        _nextGhost += pending.GhostsTaken;
        _nextPlan += pending.PlansTaken;
    }

    private void Unstage(Pending pending)
    {
        _nextGhost -= pending.GhostsTaken;
        _nextPlan -= pending.PlansTaken;
        for (var i = pending.Added.Count - 1; i >= 0; i--)
            Index.Remove(pending.Added[i]);
        for (var i = pending.Changed.Count - 1; i >= 0; i--)
            Index.Remove(pending.Changed[i].After);
        for (var i = pending.DoneCounts.Count - 1; i >= 0; i--)
        {
            var entry = Index.Plans[pending.DoneCounts[i].Plan];
            (entry.Done, entry.DoneBy) = pending.DoneBefore[i];
        }
        foreach (var entry in pending.PlansFinished)
            Index.Plans.Add(entry.Plan.Id, entry);
        for (var i = pending.PlansChanged.Count - 1; i >= 0; i--)
        {
            var entry = Index.Plans[pending.PlansChanged[i].Before.Id];
            entry.Plan = pending.PlansChanged[i].Before;
            entry.CommandRevision = pending.PlanRevisionsBefore[i];
        }

        foreach (var plan in pending.PlansOpened)
            Index.Plans.Remove(plan.Id);
        for (var i = pending.Changed.Count - 1; i >= 0; i--)
            Index.Add(pending.Changed[i].Before);
        for (var i = pending.Removed.Count - 1; i >= 0; i--)
        {
            var (entry, cause) = pending.Removed[i];
            if (cause is RemovalCause.Done done && Index.Plans.TryGetValue(entry.Ghost.Plan, out var donePlan))
            {
                donePlan.Done--;
                if (done.How.By.TryGet(out var by))
                    donePlan.DoneBy = donePlan.DoneBy.Plus(by, -1);
            }
            Index.Add(entry);
        }
    }

    private Result<(Option<PlacedShape> Shape, Option<StructureRef> Target), Reason> Resolve(Intent intent)
    {
        Result<(Option<PlacedShape>, Option<StructureRef>), Reason> Placed(Placement placement, Option<StructureRef> target) =>
            _shapes.Of(placement).Map(shape => (Option.Some(shape), target));

        return intent switch
        {
            Intent.Build build => Placed(build.Target, Option<StructureRef>.None),
            Intent.Swap swap => Placed(swap.New, Option.Some(swap.Old)),
            Intent.Remove remove => Result<(Option<PlacedShape>, Option<StructureRef>), Reason>.Ok((Option<PlacedShape>.None, Option.Some(remove.Target))),
            Intent.FinishOnly finish => Result<(Option<PlacedShape>, Option<StructureRef>), Reason>.Ok((Option<PlacedShape>.None, Option.Some(finish.Target))),
            _ => Result<(Option<PlacedShape>, Option<StructureRef>), Reason>.Error(Unknown.Kind(intent)),
        };
    }

    private Refusal? Conflict(PlacedShape shape, Dictionary<GhostId, int> replaced, GhostEntry?[] kept, Dictionary<Cell, List<int>> draftCells)
    {
        foreach (var spot in shape.Spots)
        {
            foreach (var other in Index.InCell(spot.Cell))
                if (!replaced.ContainsKey(other) && Index.Ghosts[other].Shape.TryGet(out var theirs) &&
                    Shapes.Conflict(shape, theirs).TryGet(out var code))
                    return new Refusal(Clash(code, shape, theirs), ValueList.Of(other));

            if (draftCells.TryGetValue(spot.Cell, out var drafts))
                foreach (var j in drafts)
                    if (kept[j] is { } earlier && earlier.Shape.TryGet(out var theirs) && Shapes.Conflict(shape, theirs).TryGet(out var code))
                        return new Refusal(Clash(code, shape, theirs), ValueList<GhostId>.Empty);
        }

        return null;
    }

    private static Reason Clash(ReasonCode code, PlacedShape mine, PlacedShape theirs) => Reason.Of(
        code,
        LatticeIdentity.Board,
        ValueMap<string, string>.Empty.With("piece", mine.Placement.Piece.Hash.ToString()).With("with", theirs.Placement.Piece.Hash.ToString()));

    private static void Remember(GhostEntry entry, int draft, Dictionary<SpotKey, int> spots, Dictionary<ReferenceId, int> targets, Dictionary<Cell, List<int>> cells)
    {
        if (entry.Shape.TryGet(out var shape))
            foreach (var spot in shape.Spots)
            {
                spots[spot] = draft;
                if (!cells.TryGetValue(spot.Cell, out var list))
                    cells[spot.Cell] = list = new List<int>(2);
                if (!list.Contains(draft))
                    list.Add(draft);
            }

        if (entry.Target.TryGet(out var target))
            targets[target.Id] = draft;
    }

    private static void Forget(GhostEntry entry, int draft, Dictionary<SpotKey, int> spots, Dictionary<ReferenceId, int> targets, Dictionary<Cell, List<int>> cells)
    {
        if (entry.Shape.TryGet(out var shape))
            foreach (var spot in shape.Spots)
            {
                if (spots.TryGetValue(spot, out var holder) && holder == draft)
                    spots.Remove(spot);
                if (cells.TryGetValue(spot.Cell, out var list))
                    list.Remove(draft);
            }

        if (entry.Target.TryGet(out var target) && targets.TryGetValue(target.Id, out var held) && held == draft)
            targets.Remove(target.Id);
    }

    private Result<List<GhostEntry>, Refusal> Selected(Selector selector, Actor actor)
    {
        var selected = Select(selector, actor);
        if (!selected.TryGet(out var entries))
            return Result<List<GhostEntry>, Refusal>.Error(new Refusal(selected.ErrorOf(), ValueList<GhostId>.Empty));
        return entries.Count == 0
            ? Result<List<GhostEntry>, Refusal>.Error(new Refusal(Reason.Of(ReasonCodes.NotFound, LatticeIdentity.Board), ValueList<GhostId>.Empty))
            : Result<List<GhostEntry>, Refusal>.Ok(entries);
    }

    /// <summary>The ghosts a command changed after the revision; what earlier steps of this batch did (at <paramref name="now"/>) does not count.</summary>
    private static Refusal? ChangedSince(List<GhostEntry> entries, Revision since, Revision now)
    {
        var changed = new List<GhostId>();
        foreach (var entry in entries)
            if (entry.CommandRevision.Value > since.Value && entry.CommandRevision != now)
                changed.Add(entry.Id);
        return changed.Count == 0
            ? null
            : new Refusal(Reason.Of(ReasonCodes.ChangedSince, LatticeIdentity.Board), ValueList.From(changed));
    }

    private Refusal? Admit(PlanId plan, Pending pending)
    {
        var count = Index.Plans[plan].Ghosts.Count;
        foreach (var entry in pending.Added)
            if (entry.Ghost.Plan == plan)
                count++;
        foreach (var (before, after) in pending.Changed)
            if (after.Ghost.Plan == plan && before.Ghost.Plan != plan)
                count++;
        foreach (var (entry, _) in pending.Removed)
            if (entry.Ghost.Plan == plan)
                count--;
        return _sizeLimit.Admit(count).TryGetError(out var tooLarge) ? new Refusal(tooLarge, ValueList<GhostId>.Empty) : null;
    }

    private void Load(BoardSeed seed)
    {
        Revision = seed.Revision;
        (_nextGhost, _nextPlan, _nextChangeSet) = (seed.Next.NextGhost, seed.Next.NextPlan, seed.Next.NextChangeSet);
        foreach (var plan in seed.Plans)
            Index.Plans[plan.Id] = new PlanEntry(plan, plan.Revision) { Done = seed.Done.TryGetValue(plan.Id, out var done) ? done : 0, DoneBy = SeedDoneBy(seed, plan.Id) };
        if (!Index.Plans.ContainsKey(DefaultPlan))
            Index.Plans.Add(DefaultPlan, new PlanEntry(NewDefaultPlan(), default) { Done = seed.Done.TryGetValue(DefaultPlan, out var loose) ? loose : 0, DoneBy = SeedDoneBy(seed, DefaultPlan) });
        foreach (var ghost in seed.Ghosts)
            Index.Add(EntryFor(ghost, ghost.Revision));
    }

    private static DoneBy SeedDoneBy(BoardSeed seed, PlanId plan) =>
        seed.DoneBy.TryGetValue(plan, out var by) ? DoneBy.From(by) : DoneBy.None;

    /// <summary>A ghost the board takes as it is (loaded or mirrored): its shape and target, or none when its intent does not resolve.</summary>
    private GhostEntry EntryFor(Ghost ghost, Revision commandRevision)
    {
        var parts = Resolve(ghost.Intent).Match(
            static p => p,
            static _ => (Option<PlacedShape>.None, Option<StructureRef>.None));
        return new GhostEntry(ghost, parts.Item1, parts.Item2, commandRevision);
    }

    private static Plan NewDefaultPlan() => new(
        DefaultPlan, default, DefaultPlanName, LatticeIdentity.Actor, DefaultPlanMaker, new Visibility.Everyone(),
        ValueMap<AttachmentKey, Attachment>.Empty);

    private static TOut[] Map<TIn, TOut>(List<TIn> items, Func<TIn, TOut> map)
    {
        var mapped = new TOut[items.Count];
        for (var i = 0; i < items.Count; i++)
            mapped[i] = map(items[i]);
        return mapped;
    }

    private static Reason NotFound(string what, long id) =>
        Reason.Of(ReasonCodes.NotFound, LatticeIdentity.Board, ValueMap.Of(what, id.ToString()));

    private static Outcome Refused(Reason why) => new Outcome.Refused(ValueList.Of(new Refusal(why, ValueList<GhostId>.Empty)));

    private static Result<Pending, Refusal> Ok(Pending pending) => Result<Pending, Refusal>.Ok(pending);

    /// <summary>
    /// Adds an ask with the plan's record as it stands before this change (<see cref="AccessRequest.PlanRecord"/>): a
    /// plan this change opens as it opens; else as the board holds it, a batch's earlier steps staged; else as this
    /// change found it leaving. Constant cost.
    /// </summary>
    private void Ask(Pending pending, AccessRequest request, ValueList<GhostId> ghosts) =>
        pending.Asks.Add((request with { PlanRecord = RecordOf(pending, request.Plan) }, ghosts));

    private Option<Plan> RecordOf(Pending pending, PlanId id)
    {
        foreach (var opened in pending.PlansOpened)
            if (opened.Id == id)
                return Option.Some(opened);
        if (Index.Plans.TryGetValue(id, out var entry))
            return Option.Some(entry.Plan);
        foreach (var finished in pending.PlansFinished)
            if (finished.Plan.Id == id)
                return Option.Some(finished.Plan);
        return Option<Plan>.None;
    }

    private static Result<Pending, Refusal> Fail(Refusal refusal) => Result<Pending, Refusal>.Error(refusal);

    private static Result<Pending, Refusal> Fail(Reason why) => Fail(new Refusal(why, ValueList<GhostId>.Empty));

    /// <summary>A change worked out against the current state, not yet applied.</summary>
    private sealed class Pending(Revision at, DateTimeOffset now)
    {
        public Revision At { get; } = at;

        public DateTimeOffset Now { get; } = now;

        public List<GhostEntry> Added { get; } = new();

        public List<(GhostEntry Before, GhostEntry After)> Changed { get; } = new();

        public List<(GhostEntry Entry, RemovalCause Cause)> Removed { get; } = new();

        public List<Plan> PlansOpened { get; } = new();

        public List<(Plan Before, Plan After)> PlansChanged { get; } = new();

        /// <summary>The plans that leave the board, as their entries stood (so unstaging puts them back).</summary>
        public List<PlanEntry> PlansFinished { get; } = new();

        /// <summary>The plans moved to their files' newest lines; saving is told after the commit.</summary>
        public List<PlanId> CaughtUp { get; } = new();

        /// <summary>What the access policy is asked before the commit, with the ghosts a refusal names.</summary>
        public List<(AccessRequest Request, ValueList<GhostId> Ghosts)> Asks { get; } = new();

        /// <summary>Plans whose done count is set from a file (catch-up, reopen), applied after the removals.</summary>
        public List<(PlanId Plan, int Done, DoneBy DoneBy)> DoneCounts { get; } = new();

        /// <summary>The done counts those plans had before staging, in <see cref="DoneCounts"/> order.</summary>
        public List<(int Done, DoneBy DoneBy)> DoneBefore { get; } = new();

        /// <summary>The command revisions the changed plans had before staging, in <see cref="PlansChanged"/> order.</summary>
        public List<Revision> PlanRevisionsBefore { get; } = new();

        public long GhostsTaken { get; set; }

        public long PlansTaken { get; set; }
    }
}

/// <summary>
/// Collects, for a revert, the ghosts another actor changed after the change set being undone, or that another actor
/// holds where a ghost would come back; one refusal names them all and who changed them.
/// </summary>
internal sealed class ChangedByOthers(ChangeHistory history, Actor actor, Revision after)
{
    private readonly List<GhostId> _ghosts = new();
    private readonly List<string> _actors = new();

    /// <summary>False, and noted, when another actor changed the ghost after the change set.</summary>
    public bool Check(GhostId ghost) =>
        !history.Last(ghost).TryGet(out var last) || last.At.Value <= after.Value || last.By == actor || Note(ghost, last.By);

    /// <summary>False, and noted, when a ghost holding a spot to restore was last changed (or placed) by another actor.</summary>
    public bool CheckHolder(GhostId holder, Actor placedBy)
    {
        var by = history.Last(holder).TryGet(out var last) ? last.By : placedBy;
        return by == actor || Note(holder, by);
    }

    public Refusal? Refusal()
    {
        if (_ghosts.Count == 0)
            return null;
        var facts = ValueMap.Of("actor", string.Join(", ", _actors));
        return new Refusal(Reason.Of(ReasonCodes.ChangedSince, LatticeIdentity.Board, facts), ValueList.From(_ghosts));
    }

    private bool Note(GhostId ghost, Actor by)
    {
        if (!_ghosts.Contains(ghost))
            _ghosts.Add(ghost);
        var text = ActorText.Of(by);
        if (!_actors.Contains(text))
            _actors.Add(text);
        return false;
    }
}

/// <summary>The refusal every switch over a contract union ends in for a case this version does not know.</summary>
internal static class Unknown
{
    public static Reason Kind(object value) =>
        Reason.Of(ReasonCodes.UnknownKind, LatticeIdentity.Board, ValueMap.Of("kind", value.GetType().Name));
}
