using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Slices;

/// <summary>
/// Runs long main-thread work in slices: each frame, every kind of work gets its own budget, spent on its jobs from
/// the highest priority down. A lower priority job gets only the time higher ones leave. Main thread only.
/// A frame over queued work allocates nothing; a job scheduled during a frame starts in the next one.
/// In each budget Lattice's own jobs run before consumers' (<see cref="ScheduleConsumer"/>), so a consumer's work never
/// holds back saving, sending or checking. A step of Lattice's own work that throws ends its job and goes to
/// <c>faulted</c>, and the frame goes on; with no handler it leaves <see cref="RunFrame"/>. A consumer's step is not
/// caught: its exception leaves <see cref="RunFrame"/>, its job stays queued, and only consumer work after it
/// waits.
/// </summary>
internal sealed class FrameScheduler(IClock clock, FrameBudgets budgets, Action<ISlicedWork, Exception>? faulted = null)
{
    private readonly SliceQueue _checking = new(budgets.Checking, faulted);
    private readonly SliceQueue _sending = new(budgets.Sending, faulted);
    private readonly SliceQueue _saving = new(budgets.Saving, faulted);
    private readonly SliceQueue _drawing = new(budgets.Drawing, faulted);

    /// <summary>Queues work of a kind; refused for a kind this version does not know. Linear in the kind's queue.</summary>
    public Result<SlicedJob, Reason> Schedule(WorkKind kind, ISlicedWork work, Priority priority) =>
        Add(kind, new SlicedJob(work, priority));

    /// <summary>Queues a consumer's work: it runs after every Lattice job of its kind, and what it throws is not caught.</summary>
    public Result<SlicedJob, Reason> ScheduleConsumer(WorkKind kind, ISlicedWork work, Priority priority) =>
        Add(kind, new SlicedJob(work, priority, consumer: true));

    private Result<SlicedJob, Reason> Add(WorkKind kind, SlicedJob job)
    {
        if (QueueOf(kind) is not { } queue)
            return Result<SlicedJob, Reason>.Error(Reason.Of(
                ReasonCodes.UnknownKind, SliceRules.Scheduler, ValueMap.Of("kind", kind.ToString())));

        queue.Add(job);
        return Result<SlicedJob, Reason>.Ok(job);
    }

    /// <summary>The time the last <see cref="RunFrame"/> spent on work of a kind, by the scheduler's clock; zero for a kind this version does not know.</summary>
    internal TimeSpan Spent(WorkKind kind) => QueueOf(kind)?.Spent ?? TimeSpan.Zero;

    /// <summary>Whether no job of any kind is queued or running; a paused job does not count.</summary>
    internal bool Idle => _checking.Idle && _sending.Idle && _saving.Idle && _drawing.Idle;

    /// <summary>Spends one frame's budgets: checking, sending, saving, then drawing.</summary>
    public void RunFrame()
    {
        _checking.Run(clock);
        _sending.Run(clock);
        _saving.Run(clock);
        _drawing.Run(clock);
    }

    private SliceQueue? QueueOf(WorkKind kind) => kind switch
    {
        WorkKind.Checking => _checking,
        WorkKind.Sending => _sending,
        WorkKind.Saving => _saving,
        WorkKind.Drawing => _drawing,
        _ => null,
    };

    /// <summary>The jobs of one kind in run order, and the budget they share.</summary>
    private sealed class SliceQueue(SliceBudget budget, Action<ISlicedWork, Exception>? faulted)
    {
        private readonly List<SlicedJob> _jobs = new(16);
        private readonly List<SlicedJob> _incoming = new(16);

        public void Add(SlicedJob job) => _incoming.Add(job);

        public bool Idle
        {
            get
            {
                if (_incoming.Count > 0)
                    return false;
                foreach (var job in _jobs)
                    if (job.State == JobState.Running)
                        return false;
                return true;
            }
        }

        /// <summary>The time the last <see cref="Run"/> spent on steps.</summary>
        public TimeSpan Spent { get; private set; }

        public void Run(IClock clock)
        {
            Spent = TimeSpan.Zero;
            TakeIncoming();
            if (_jobs.Count == 0)
                return;

            var now = clock.Now;
            var start = now;
            var deadline = now + budget.Length;
            var stepped = false;
            for (var i = 0; i < _jobs.Count && now < deadline; i++)
            {
                var job = _jobs[i];
                var nextFrame = false;
                while (job.State == JobState.Running && !nextFrame && (!stepped || now + job.StepEstimate <= deadline))
                {
                    var outcome = job.Consumer || faulted is null ? job.Work.Step() : Guarded(job, faulted);
                    var after = clock.Now;
                    job.Observe(after - now);
                    now = after;
                    stepped = true;
                    if (outcome == StepOutcome.Done)
                        job.Finish();
                    nextFrame = outcome == StepOutcome.NextFrame;
                }

                if (job.State == JobState.Running && !nextFrame)
                    break;
            }

            Spent = now - start;
            RemoveEnded();
        }

        private static StepOutcome Guarded(SlicedJob job, Action<ISlicedWork, Exception> faulted)
        {
            try
            {
                return job.Work.Step();
            }
            catch (Exception error)
            {
                job.Fault();
                faulted(job.Work, error);
                return StepOutcome.Done;
            }
        }

        private void TakeIncoming()
        {
            for (var i = 0; i < _incoming.Count; i++)
                InsertInRunOrder(_incoming[i]);
            _incoming.Clear();
        }

        private void InsertInRunOrder(SlicedJob job)
        {
            var at = _jobs.Count;
            while (at > 0 && RunsBefore(job, _jobs[at - 1]))
                at--;
            _jobs.Insert(at, job);
        }

        private static bool RunsBefore(SlicedJob job, SlicedJob other) =>
            job.Consumer != other.Consumer ? other.Consumer : other.Priority.Rank < job.Priority.Rank;

        private void RemoveEnded()
        {
            var kept = 0;
            for (var i = 0; i < _jobs.Count; i++)
                if (_jobs[i].State == JobState.Running)
                    _jobs[kept++] = _jobs[i];
            _jobs.RemoveRange(kept, _jobs.Count - kept);
        }
    }
}
