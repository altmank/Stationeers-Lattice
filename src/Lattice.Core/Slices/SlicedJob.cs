using System;

namespace Lattice.Core.Slices;

/// <summary>A scheduled piece of sliced work: its state, and the handle that cancels it.</summary>
internal sealed class SlicedJob
{
    internal SlicedJob(ISlicedWork work, Priority priority, bool consumer = false)
    {
        Work = work;
        Priority = priority;
        Consumer = consumer;
    }

    public JobState State { get; private set; } = JobState.Running;

    public Priority Priority { get; }

    internal ISlicedWork Work { get; }

    /// <summary>Work a consumer scheduled (<see cref="Lattice.Api.ISlices"/>): run after Lattice's own, never caught.</summary>
    internal bool Consumer { get; }

    /// <summary>
    /// Expected cost of the next step: the median of the last three steps' costs, so steps that are steadily dear
    /// keep a slice from starting one it cannot finish, while one step stretched by something outside the job (a
    /// collection, the thread descheduled) does not hold the job back in the frames after it.
    /// </summary>
    internal TimeSpan StepEstimate { get; private set; }

    private long _last;
    private long _beforeLast;

    /// <summary>Stops the job before its next step; a finished or cancelled job stays as it is.</summary>
    public void Cancel()
    {
        if (State == JobState.Running)
            State = JobState.Cancelled;
    }

    internal void Observe(TimeSpan stepCost)
    {
        var (a, b, c) = (stepCost.Ticks, _last, _beforeLast);
        StepEstimate = TimeSpan.FromTicks(Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c)));
        (_last, _beforeLast) = (a, b);
    }

    internal void Finish()
    {
        if (State == JobState.Running)
            State = JobState.Done;
    }

    internal void Fault()
    {
        if (State == JobState.Running)
            State = JobState.Faulted;
    }
}
