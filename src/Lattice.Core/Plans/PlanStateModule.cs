using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Ports;
using Lattice.Core.Safety;
using Lattice.Core.Slices;
using Lattice.Core.Steps;
using Lattice.Core.Validation;

namespace Lattice.Core.Plans;

/// <summary>A plan's six facts and its state, as <see cref="IPlans"/> answers them.</summary>
internal interface IPlanStates
{
    /// <summary>The plan's facts, or pending while they are worked out.</summary>
    Result<Answer<PlanFacts>, Reason> Facts(PlanId plan);

    /// <summary>The plan's state, or pending with its facts.</summary>
    Result<Answer<PlanState>, Reason> State(PlanId plan);
}

/// <summary>The build safety warnings for a plan's ghosts, worked out in parts.</summary>
internal interface IPlanSafety
{
    /// <summary>Starts the check of these ghosts; the board and the world must not change until it is done.</summary>
    ISafetyRun Start(IReadOnlyList<Ghost> plan);
}

/// <summary>Build safety (32) on the board's planned world over the live world view.</summary>
internal sealed class BoardSafety(BuildSafety safety, IGhostBoard board, IWorldView world) : IPlanSafety
{
    public ISafetyRun Start(IReadOnlyList<Ghost> plan) => safety.Start(plan, board, world);
}

/// <summary>What the plan-state module is built from, for one world.</summary>
/// <param name="Board">The board.</param>
/// <param name="Events">The world's events: change sets are read from them, plan state changes raised through them.</param>
/// <param name="Mirror">The world mirror, for its revision.</param>
/// <param name="Validation">Validation (30), for the findings and its runs.</param>
/// <param name="Bills">Bills (31), worked out in parts.</param>
/// <param name="Safety">Build safety (32).</param>
/// <param name="Scheduler">The frame scheduler the facts are worked out in (checking budget).</param>
internal sealed record PlanStateParts(
    GhostBoard Board,
    BoardEvents Events,
    WorldMirror Mirror,
    ValidationService Validation,
    BillMaker Bills,
    IPlanSafety Safety,
    FrameScheduler Scheduler);

/// <summary>
/// Plan state, worked out, never set by hand. Each active plan's six facts from validation, bills and
/// build safety, and the state that follows (<see cref="PlanFactsMaker"/>).
/// <para>
/// Main thread. <see cref="Frame"/>, once per frame, sees whether the board (as its change sets have been read), the
/// world mirror or validation's findings moved since the facts were worked out; if so every active plan is queued again and
/// worked out in the checking budget's slices at low priority, one plan at a time and a few of its ghosts a step
/// (<see cref="PlanJob"/>): after validation's asks, whose answers move the findings the facts follow (and completion
/// waits on them);
/// a plan's work is dropped and the plan queued again when the board, the world or the findings move under it. A plan
/// whose findings are still pending at the board's revision is left until validation's findings move. Until a plan's facts
/// are worked out for the current board, world and findings, <see cref="Facts"/> and <see cref="State"/> answer
/// pending; nothing is worked out in the caller's frame. When a plan's state differs from the last one worked out (or
/// is its first), <see cref="IGhostEvents.PlanStateChanged"/> is raised with the next burst; when queued plans are all
/// worked out after a pending answer was given, <see cref="IGhostEvents.AnswersReady"/>.
/// </para>
/// <para>
/// Progress counts the ghosts done (they leave the board) as the board keeps them per plan; saving restores the count
/// when it rebuilds a plan at load, so a plan built to the end before a save loads as complete. A plan's work is linear
/// in its ghosts, plus what build safety follows (<see cref="BuildSafety"/>); a step does at most the units
/// <see cref="PlanJob"/> names, beyond the plan's selection and two sorts when its work starts.
/// </para>
/// </summary>
internal sealed class PlanStateModule : IPlanStates
{
    private readonly PlanStateParts _parts;
    private readonly Dictionary<PlanId, Known> _known = new();
    private readonly Queue<PlanId> _queue = new();
    private readonly HashSet<PlanId> _queued = new();
    private readonly Action<ChangeSet> _onChanged;
    private readonly Action _onReset;
    private Key _key = new(new Revision(-1), new Revision(-1), -1, -1);
    private Revision _seen;
    private PlanJob? _job;
    private bool _working;
    private bool _askedPending;

    public PlanStateModule(PlanStateParts parts)
    {
        _parts = parts;
        _seen = parts.Board.Revision;
        _onChanged = Read;
        _onReset = Reset;
    }

    /// <summary>Subscribes to the world's change sets and installs itself on the board.</summary>
    public void Start()
    {
        _parts.Events.Changed += _onChanged;
        _parts.Events.BoardReset += _onReset;
        _parts.Board.InstallPlanStates(this);
    }

    /// <summary>Unsubscribes; the board stays as it is (it ends with the world).</summary>
    public void End()
    {
        _parts.Events.Changed -= _onChanged;
        _parts.Events.BoardReset -= _onReset;
    }

    /// <summary>Queues every active plan again when the board, the world or the findings moved. Linear in the active plans when they did, constant otherwise.</summary>
    public void Frame()
    {
        var now = Now();
        if (now == _key)
            return;
        _key = now;
        foreach (var plan in _parts.Board.Index.Plans.Keys)
            if (_queued.Add(plan))
                _queue.Enqueue(plan);
        DropGone();
        if (!_working && _queue.Count > 0)
        {
            _working = true;
            _parts.Scheduler.Schedule(WorkKind.Checking, new Work(this), Priority.Low);
        }
    }

    public Result<Answer<PlanFacts>, Reason> Facts(PlanId plan)
    {
        if (!_parts.Board.Index.Plans.ContainsKey(plan))
            return Result<Answer<PlanFacts>, Reason>.Error(NotFound(plan));
        if (_known.TryGetValue(plan, out var known) && known.At == Now())
            return Result<Answer<PlanFacts>, Reason>.Ok(known.Facts);
        _askedPending = true;
        return Result<Answer<PlanFacts>, Reason>.Ok(new Answer<PlanFacts>.Pending(_parts.Board.Revision));
    }

    public Result<Answer<PlanState>, Reason> State(PlanId plan)
    {
        if (!_parts.Board.Index.Plans.ContainsKey(plan))
            return Result<Answer<PlanState>, Reason>.Error(NotFound(plan));
        if (_known.TryGetValue(plan, out var known) && known.At == Now())
            return Result<Answer<PlanState>, Reason>.Ok(known.State);
        _askedPending = true;
        return Result<Answer<PlanState>, Reason>.Ok(new Answer<PlanState>.Pending(_parts.Board.Revision));
    }

    private Key Now() => new(_parts.Board.Revision, _seen, _parts.Mirror.Revision, _parts.Validation.FindingsVersion);

    /// <summary>Starts working out one plan; none while its change sets are not in for the board's revision. Constant.</summary>
    private PlanJob? Begin(PlanId plan) =>
        _parts.Board.Index.Plans.ContainsKey(plan) && _seen == _parts.Board.Revision
            ? new PlanJob(_parts, plan, Now(), _parts.Board.Revision)
            : null;

    private void Advance(PlanJob job)
    {
        if (job.At != Now())
        {
            _job = null;
            if (_queued.Add(job.Plan))
                _queue.Enqueue(job.Plan);
            return;
        }

        switch (job.Step())
        {
            case JobStep.More:
                return;
            case JobStep.Left:
                _job = null;
                return;
        }

        _job = null;
        var facts = job.Facts(_parts.Board.Index.Plans[job.Plan].Done);
        var state = PlanFactsMaker.StateOf(facts);
        var changed = !_known.TryGetValue(job.Plan, out var before) || !Equals(before.State.Value, state);
        _known[job.Plan] = new Known(job.At, new Answer<PlanFacts>.Ready(facts, job.Board), new Answer<PlanState>.Ready(state, job.Board));
        if (changed)
            _parts.Events.Queue(new PlanStateChangedEvent(job.Plan));
    }

    private void Read(ChangeSet changes) => _seen = changes.Revision;

    private void Reset()
    {
        _seen = _parts.Board.Revision;
        _known.Clear();
        _job = null;
    }

    private void DropGone()
    {
        List<PlanId>? gone = null;
        foreach (var plan in _known.Keys)
            if (!_parts.Board.Index.Plans.ContainsKey(plan))
                (gone ??= new List<PlanId>()).Add(plan);
        if (gone is null)
            return;
        foreach (var plan in gone)
        {
            _known.Remove(plan);
        }
    }

    private StepOutcome Step()
    {
        if (_job is { } job)
        {
            Advance(job);
        }
        else if (_queue.Count > 0)
        {
            var plan = _queue.Dequeue();
            _queued.Remove(plan);
            _job = Begin(plan);
        }

        if (_job is not null || _queue.Count > 0)
            return StepOutcome.More;
        _working = false;
        if (_askedPending)
        {
            _askedPending = false;
            _parts.Events.Queue(new AnswersReadyEvent(_parts.Board.Revision));
        }

        return StepOutcome.Done;
    }

    private static Reason NotFound(PlanId plan) =>
        Reason.Of(ReasonCodes.NotFound, PlanCodes.Plans, ValueMap.Of("plan", plan.Value.ToString()));

    /// <summary>
    /// What the facts were worked out against: the board, the change sets read, the world, and validation's findings
    /// (<see cref="ValidationService.FindingsVersion"/>), so a run that changes no finding leaves the facts as they are.
    /// </summary>
    private readonly record struct Key(Revision Board, Revision Seen, long World, int Findings);

    private sealed record Known(Key At, Answer<PlanFacts>.Ready Facts, Answer<PlanState>.Ready State);

    /// <summary>What one step of a plan's work came to.</summary>
    private enum JobStep
    {
        /// <summary>More steps to go.</summary>
        More,

        /// <summary>The facts are worked out.</summary>
        Done,

        /// <summary>Left until validation's findings move: no findings at the board's revision, or the selection refused.</summary>
        Left,
    }

    /// <summary>
    /// One plan's facts worked out across steps, one part a step: the findings taken (the viewer's selection), the plan's
    /// ghosts taken (the actor's selection), then a bounded amount of findings, bill, build safety (its start, the
    /// plan's sort, a step of its own) and facts. Measured per unit on a 36,306-structure base under a 5,000-ghost plan:
    /// findings and facts about 1 us, bill about 2 us, safety up to 0.1 ms with a few pieces near 0.5 ms (the keep-outs
    /// around openings); each selection about 1 ms and the safety start about 1.7 ms for the whole plan. So a step
    /// stays inside the checking budget up to about 5,000 ghosts; past that the selections and the sort grow with the
    /// plan.
    /// </summary>
    private sealed class PlanJob(PlanStateParts parts, PlanId plan, Key at, Revision board)
    {
        /// <summary>Ghosts placed or copied in one findings step.</summary>
        public const int FindingsUnits = 64;

        /// <summary>Ghosts counted in one bill step.</summary>
        public const int BillUnits = 64;

        /// <summary>Ghosts through one check in one safety step.</summary>
        public const int SafetyUnits = SafetyRun.StepUnits;

        /// <summary>Ghosts or findings sorted in one facts step.</summary>
        public const int FactsUnits = 256;

        private FindingsRun? _findings;
        private ValueList<Ghost>? _ghosts;
        private BillMaker.BillRun? _bill;
        private ISafetyRun? _safety;
        private bool _safe;
        private PlanFactsRun? _facts;

        public PlanId Plan => plan;

        /// <summary>What the facts are worked out against.</summary>
        public Key At => at;

        public Revision Board => board;

        /// <summary>Works one part a bounded amount.</summary>
        public JobStep Step()
        {
            var which = new Selector.InPlan(plan);
            if (_findings is null)
            {
                if (!parts.Validation.FindingsFor(which).TryGet(out var findings))
                    return JobStep.Left;
                _findings = findings;
                return JobStep.More;
            }

            if (_ghosts is null || _bill is null)
            {
                if (!parts.Board.ViewFor(LatticeIdentity.Actor).Select(which).TryGet(out var ghosts))
                    return JobStep.Left;
                _ghosts = ghosts;
                _bill = parts.Bills.Start(which, ghosts);
                return JobStep.More;
            }

            if (!_findings.Done)
            {
                _findings.Step(FindingsUnits);
                return JobStep.More;
            }

            if (!_bill.Done)
            {
                _bill.Step(BillUnits);
                return JobStep.More;
            }

            if (_safety is null)
            {
                _safety = parts.Safety.Start(_ghosts);
                return JobStep.More;
            }

            if (!_safe)
            {
                _safe = _safety.Step(SafetyUnits);
                return JobStep.More;
            }

            return (_facts ??= new PlanFactsRun(_ghosts, _findings.Report())).Step(FactsUnits) ? JobStep.Done : JobStep.More;
        }

        /// <summary>The facts, once <see cref="Step"/> has answered done.</summary>
        public PlanFacts Facts(int done) => _facts!.Facts(plan, board, done, _bill!.Bill(), _safety!.Warnings());
    }

    private sealed class Work(PlanStateModule owner) : ISlicedWork
    {
        public StepOutcome Step() => owner.Step();
    }
}

/// <summary>Who gives the plan-state module's reasons.</summary>
internal static class PlanCodes
{
    public static RuleId Plans => RuleId.Known("lattice.plans");
}
