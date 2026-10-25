using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Slices;

/// <summary>
/// <see cref="ISlices"/> over a world's <see cref="FrameScheduler"/>: consumer work runs in the drawing budget after
/// Lattice's own drawing jobs, one queued work per id. A consumer's step is called plainly; what it throws is the
/// consumer's.
/// </summary>
internal sealed class SliceService(FrameScheduler scheduler) : ISlices
{
    private readonly Dictionary<SliceId, ConsumerWork> _queued = new();

    public Result<IDisposable, Reason> Schedule(SlicedWorkDescriptor work)
    {
        if (work.Id == default)
            return Refuse(ReasonCodes.DefaultValue, work);
        if (_queued.ContainsKey(work.Id))
            return Refuse(ReasonCodes.AlreadyRegistered, work);
        if (RankOf(work.Priority) is not { } priority)
            return Refuse(ReasonCodes.UnknownKind, work);

        var queued = new ConsumerWork(this, work);
        if (!scheduler.ScheduleConsumer(WorkKind.Drawing, queued, priority).TryGet(out var job))
            return Refuse(ReasonCodes.UnknownKind, work);

        queued.Job = job;
        _queued.Add(work.Id, queued);
        return Result<IDisposable, Reason>.Ok(queued);
    }

    private static Priority? RankOf(SlicePriority priority) => priority switch
    {
        SlicePriority.Low => Priority.Low,
        SlicePriority.Normal => Priority.Normal,
        SlicePriority.High => Priority.High,
        _ => null,
    };

    private static Result<IDisposable, Reason> Refuse(ReasonCode code, SlicedWorkDescriptor work) =>
        Result<IDisposable, Reason>.Error(Reason.Of(
            code,
            SliceRules.Scheduler,
            ValueMap<string, string>.Empty.With("id", work.Id.Value).With("priority", work.Priority.ToString())));

    private void Forget(ConsumerWork work)
    {
        if (_queued.TryGetValue(work.Id, out var queued) && ReferenceEquals(queued, work))
            _queued.Remove(work.Id);
    }

    /// <summary>A consumer's work as the scheduler steps it, and the handle that cancels it.</summary>
    private sealed class ConsumerWork(SliceService owner, SlicedWorkDescriptor descriptor) : ISlicedWork, IDisposable
    {
        public SlicedJob? Job { get; set; }

        public SliceId Id => descriptor.Id;

        public StepOutcome Step()
        {
            if (descriptor.Step())
                return StepOutcome.More;
            owner.Forget(this);
            return StepOutcome.Done;
        }

        public void Dispose()
        {
            Job?.Cancel();
            owner.Forget(this);
        }
    }
}
