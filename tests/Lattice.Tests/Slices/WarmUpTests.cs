using System;
using Lattice.Api;
using Lattice.Core.Slices;

namespace Lattice.Tests.Slices;

public sealed class WarmUpTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(2);
    private static readonly string[] Slices = ["Lattice.Core.Slices"];

    private readonly WarmedSets sets = new();
    private readonly TickingClock _clock = new(TimeSpan.FromMilliseconds(0.1));

    private FrameScheduler Scheduler() =>
        new(_clock, new FrameBudgets(Create(), Create(), Create(), Create()));

    private static SliceBudget Create() =>
        SliceBudget.Create(Budget).Match(b => b, r => throw new InvalidOperationException(r.ToString()));

    private static WarmUp Work(SlicedJob job) => (WarmUp)job.Work;

    private static SlicedJob Job(Option<SlicedJob> scheduled) =>
        scheduled.TryGet(out var job) ? job : throw new InvalidOperationException("the warm-up was not scheduled");

    [Fact]
    public void Scheduling_never_throws_whatever_the_assembly_does()
    {
        Assert.False(WarmUp.Schedule(sets, Scheduler(), "no-assembly", null!, Slices).TryGet(out _));
        Assert.False(sets.IsDone("no-assembly"));
    }

    [Fact]
    public void A_set_is_compiled_in_slices_within_the_budget_once_and_then_it_is_done()
    {
        var scheduler = Scheduler();
        var job = Job(WarmUp.Schedule(sets, scheduler, "slices-once", typeof(WarmUp).Assembly, Slices));
        var frames = 0;
        while (job.State == JobState.Running)
        {
            var start = _clock.Now;
            scheduler.RunFrame();
            Assert.True(_clock.Now - start <= Budget + TimeSpan.FromMilliseconds(0.2));
            Assert.True(++frames < 10_000, "the warm-up made no progress");
        }

        Assert.Equal(JobState.Done, job.State);
        Assert.True(Work(job).Compiled > 50, $"compiled {Work(job).Compiled}");
        Assert.True(frames > 1, "the set was compiled in one slice");
        Assert.True(sets.IsDone("slices-once"));
        Assert.False(WarmUp.Schedule(sets, scheduler, "slices-once", typeof(WarmUp).Assembly, Slices).TryGet(out _));
    }

    [Fact]
    public void A_frame_in_which_other_work_took_time_compiles_nothing()
    {
        var scheduler = Scheduler();
        var worker = new ForeverJob(_clock);
        scheduler.Schedule(WorkKind.Checking, worker, Priority.Low);
        var job = Job(WarmUp.Schedule(sets, scheduler, "slices-waits", typeof(WarmUp).Assembly, Slices));

        for (var i = 0; i < 5; i++)
            scheduler.RunFrame();

        Assert.Equal(0, Work(job).Compiled);
        Assert.Equal(JobState.Running, job.State);
        Assert.True(worker.Steps > 0);

        worker.Stop = true;
        for (var i = 0; i < 2000 && job.State == JobState.Running; i++)
            scheduler.RunFrame();

        Assert.True(Work(job).Compiled > 50);
        Assert.Equal(JobState.Done, job.State);
    }

    [Fact]
    public void The_warm_up_waits_out_a_frame_whose_other_work_took_time_in_its_own_kind()
    {
        var scheduler = Scheduler();
        var order = new System.Collections.Generic.List<string>();
        var warm = Job(WarmUp.Schedule(sets, scheduler, "slices-order", typeof(WarmUp).Assembly, Slices));
        var low = new RecordingJob("low", order, _clock);
        scheduler.Schedule(WorkKind.Drawing, low, Priority.Low);

        scheduler.RunFrame();

        Assert.Equal(["low"], order);
        Assert.Equal(0, Work(warm).Compiled);
    }

    private sealed class RecordingJob(string name, System.Collections.Generic.List<string> order, TickingClock clock) : ISlicedWork
    {
        public StepOutcome Step()
        {
            order.Add(name);
            clock.Advance(TimeSpan.FromMilliseconds(0.5));
            return StepOutcome.Done;
        }
    }

    private sealed class ForeverJob(TickingClock clock) : ISlicedWork
    {
        public int Steps { get; private set; }

        public bool Stop { get; set; }

        public StepOutcome Step()
        {
            Steps++;
            clock.Advance(TimeSpan.FromMilliseconds(0.1));
            return Stop ? StepOutcome.Done : StepOutcome.More;
        }
    }

    /// <summary>A clock that moves by a fixed time at each read, so a step costs that time.</summary>
    private sealed class TickingClock(TimeSpan perRead) : IClock
    {
        private TimeSpan _now;

        public TimeSpan Now => _now += perRead;

        public void Advance(TimeSpan by) => _now += by;
    }
}
