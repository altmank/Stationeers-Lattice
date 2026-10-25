using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;
using Lattice.TestKit.Allocations;

namespace Lattice.Tests.Slices;

public sealed class FrameSchedulerTests
{
    private static readonly TimeSpan Micro = TimeSpan.FromTicks(10);

    private static readonly WorkKind[] Kinds = [WorkKind.Checking, WorkKind.Sending, WorkKind.Saving, WorkKind.Drawing];

    private readonly FakeClock _clock = new();

    private static SliceBudget Budget(double milliseconds) =>
        SliceBudget.Create(TimeSpan.FromMilliseconds(milliseconds))
            .Match(b => b, r => throw new InvalidOperationException(r.ToString()));

    private static FrameBudgets Uniform(double milliseconds) =>
        new(Budget(milliseconds), Budget(milliseconds), Budget(milliseconds), Budget(milliseconds));

    private FrameScheduler Scheduler(double milliseconds = 2) => new(_clock, Uniform(milliseconds));

    private static SlicedJob Ok(Result<SlicedJob, Reason> scheduled) =>
        scheduled.Match(j => j, r => throw new InvalidOperationException(r.ToString()));

    private TimeSpan TimedFrame(FrameScheduler scheduler)
    {
        var start = _clock.Now;
        scheduler.RunFrame();
        return _clock.Now - start;
    }

    [Fact]
    public void A_million_step_job_never_exceeds_its_budget_per_frame_and_finishes()
    {
        var scheduler = Scheduler(2);
        var job = new CountingJob(_clock, 1_000_000, Micro);
        var handle = Ok(scheduler.Schedule(WorkKind.Checking, job, Priority.Normal));

        var frames = 0;
        while (handle.State == JobState.Running)
        {
            Assert.True(TimedFrame(scheduler) <= TimeSpan.FromMilliseconds(2));
            Assert.True(++frames < 10_000, "the job made no progress");
        }

        Assert.Equal(JobState.Done, handle.State);
        Assert.Equal(1_000_000, job.Done);
        Assert.Equal(500, frames);
    }

    [Fact]
    public void A_step_that_asks_for_the_next_frame_is_not_stepped_again_this_frame_and_the_jobs_after_it_still_run()
    {
        var scheduler = Scheduler(2);
        var yielding = new NextFrameJob(_clock, Micro);
        var after = new CountingJob(_clock, 1_000_000, Micro);
        Ok(scheduler.Schedule(WorkKind.Checking, yielding, Priority.Normal));
        Ok(scheduler.Schedule(WorkKind.Checking, after, Priority.Low));

        scheduler.RunFrame();
        var afterFirst = after.Done;
        scheduler.RunFrame();

        Assert.Equal(2, yielding.Steps);
        Assert.True(afterFirst > 0);
        Assert.True(after.Done > afterFirst);
    }

    [Fact]
    public void A_job_asking_for_the_next_frame_alone_costs_one_step_a_frame()
    {
        var scheduler = Scheduler(2);
        Ok(scheduler.Schedule(WorkKind.Checking, new NextFrameJob(_clock, Micro), Priority.Low));

        Assert.Equal(Micro, TimedFrame(scheduler));
    }

    private sealed class NextFrameJob(FakeClock clock, TimeSpan stepCost) : ISlicedWork
    {
        public int Steps { get; private set; }

        public StepOutcome Step()
        {
            clock.Advance(stepCost);
            Steps++;
            return StepOutcome.NextFrame;
        }
    }

    [Fact]
    public void Each_kind_spends_its_own_budget()
    {
        var scheduler = new FrameScheduler(_clock, new FrameBudgets(Budget(1), Budget(2), Budget(3), Budget(4)));
        var jobs = new List<CountingJob>();
        foreach (var kind in Kinds)
        {
            var job = new CountingJob(_clock, 1_000_000, Micro);
            jobs.Add(job);
            Ok(scheduler.Schedule(kind, job, Priority.Normal));
        }

        scheduler.RunFrame();

        Assert.Equal([1000, 2000, 3000, 4000], jobs.ConvertAll(j => j.Done));
    }

    [Fact]
    public void Higher_priority_runs_first_and_equal_priorities_run_in_scheduling_order()
    {
        var scheduler = Scheduler(1);
        var order = new List<string>();
        CountingJob Job(string name) => new(_clock, 1, Micro, _ => order.Add(name));

        Ok(scheduler.Schedule(WorkKind.Checking, Job("low"), Priority.Low));
        Ok(scheduler.Schedule(WorkKind.Checking, Job("normal-1"), Priority.Normal));
        Ok(scheduler.Schedule(WorkKind.Checking, Job("high"), Priority.High));
        Ok(scheduler.Schedule(WorkKind.Checking, Job("normal-2"), Priority.Normal));
        Ok(scheduler.Schedule(WorkKind.Checking, Job("urgent"), new Priority(5)));

        scheduler.RunFrame();

        Assert.Equal(["urgent", "high", "normal-1", "normal-2", "low"], order);
    }

    [Fact]
    public void Lower_priority_gets_only_what_higher_priority_leaves()
    {
        var scheduler = Scheduler(1);
        var high = new CountingJob(_clock, 300, Micro);
        var low = new CountingJob(_clock, 1_000_000, Micro);
        Ok(scheduler.Schedule(WorkKind.Saving, low, Priority.Low));
        Ok(scheduler.Schedule(WorkKind.Saving, high, Priority.High));

        scheduler.RunFrame();

        Assert.Equal(300, high.Done);
        Assert.Equal(700, low.Done);
    }

    [Fact]
    public void A_cancelled_job_stops_at_its_next_slice()
    {
        var scheduler = Scheduler(1);
        var job = new CountingJob(_clock, 1_000_000, Micro);
        var handle = Ok(scheduler.Schedule(WorkKind.Sending, job, Priority.Normal));
        scheduler.RunFrame();
        var before = job.Done;

        handle.Cancel();
        scheduler.RunFrame();

        Assert.Equal(JobState.Cancelled, handle.State);
        Assert.Equal(before, job.Done);
    }

    [Fact]
    public void A_job_cancelled_during_its_own_step_takes_no_further_step()
    {
        var scheduler = Scheduler(1);
        SlicedJob? handle = null;
        var job = new CountingJob(_clock, 1_000_000, Micro, j =>
        {
            if (j.Done == 10) handle!.Cancel();
        });
        handle = Ok(scheduler.Schedule(WorkKind.Checking, job, Priority.Normal));

        scheduler.RunFrame();
        scheduler.RunFrame();

        Assert.Equal(10, job.Done);
        Assert.Equal(JobState.Cancelled, handle.State);
    }

    [Fact]
    public void A_cancelled_job_leaves_its_time_to_the_next_job()
    {
        var scheduler = Scheduler(1);
        var first = Ok(scheduler.Schedule(WorkKind.Checking, new CountingJob(_clock, 1_000_000, Micro), Priority.High));
        var second = new CountingJob(_clock, 1_000_000, Micro);
        Ok(scheduler.Schedule(WorkKind.Checking, second, Priority.Normal));

        first.Cancel();
        scheduler.RunFrame();

        Assert.Equal(1000, second.Done);
    }

    [Fact]
    public void Cancelling_a_finished_job_keeps_it_done()
    {
        var scheduler = Scheduler(1);
        var handle = Ok(scheduler.Schedule(WorkKind.Checking, new CountingJob(_clock, 3, Micro), Priority.Normal));
        scheduler.RunFrame();

        handle.Cancel();

        Assert.Equal(JobState.Done, handle.State);
    }

    [Fact]
    public void A_job_scheduled_during_a_frame_starts_next_frame()
    {
        var scheduler = Scheduler(1);
        var late = new CountingJob(_clock, 5, Micro);
        var scheduledLate = false;
        var early = new CountingJob(_clock, 2, Micro, _ =>
        {
            if (scheduledLate) return;
            scheduledLate = true;
            Ok(scheduler.Schedule(WorkKind.Checking, late, Priority.High));
        });
        Ok(scheduler.Schedule(WorkKind.Checking, early, Priority.Normal));

        scheduler.RunFrame();
        Assert.Equal(0, late.Done);

        scheduler.RunFrame();
        Assert.Equal(5, late.Done);
    }

    [Fact]
    public void A_step_longer_than_the_budget_still_runs_once_a_frame()
    {
        var scheduler = Scheduler(1);
        var job = new CountingJob(_clock, 3, TimeSpan.FromMilliseconds(5));
        var handle = Ok(scheduler.Schedule(WorkKind.Drawing, job, Priority.Normal));

        scheduler.RunFrame();
        Assert.Equal(1, job.Done);
        scheduler.RunFrame();
        scheduler.RunFrame();

        Assert.Equal(JobState.Done, handle.State);
    }

    [Fact]
    public void One_slow_step_does_not_hold_a_job_to_one_step_a_frame()
    {
        var scheduler = Scheduler(1);
        var steps = 0;
        var job = new SteppedJob(() =>
        {
            _clock.Advance(++steps == 1 ? TimeSpan.FromMilliseconds(3) : Micro);
            return StepOutcome.More;
        });
        Ok(scheduler.Schedule(WorkKind.Checking, job, Priority.Normal));

        for (var frame = 0; frame < 40; frame++)
            scheduler.RunFrame();
        var before = steps;
        scheduler.RunFrame();

        Assert.True(steps - before > 900, $"only {steps - before} steps in a frame after the slow one");
    }

    [Fact]
    public void A_one_off_slow_step_leaves_the_next_frame_its_whole_budget()
    {
        var scheduler = Scheduler(1);
        var steps = 0;
        var job = new SteppedJob(() =>
        {
            _clock.Advance(++steps == 500 ? TimeSpan.FromMilliseconds(20) : Micro);
            return StepOutcome.More;
        });
        Ok(scheduler.Schedule(WorkKind.Checking, job, Priority.Normal));

        while (steps < 500)
            scheduler.RunFrame();
        var before = steps;
        scheduler.RunFrame();

        Assert.True(steps - before > 900, $"only {steps - before} steps in the frame after the slow one");
    }

    [Fact]
    public void Steps_that_each_cost_most_of_the_budget_run_one_a_frame()
    {
        var scheduler = Scheduler(1);
        var job = new CountingJob(_clock, 10, TimeSpan.FromMilliseconds(0.8));
        Ok(scheduler.Schedule(WorkKind.Checking, job, Priority.Normal));

        for (var frame = 0; frame < 3; frame++)
            scheduler.RunFrame();
        var frames = new List<TimeSpan>();
        for (var frame = 0; frame < 5; frame++)
            frames.Add(TimedFrame(scheduler));

        Assert.All(frames, spent => Assert.True(spent <= TimeSpan.FromMilliseconds(1), $"a frame spent {spent.TotalMilliseconds} ms"));
    }

    [Fact]
    public void An_unknown_kind_is_refused()
    {
        var scheduled = Scheduler().Schedule((WorkKind)99, new CountingJob(_clock, 1, Micro), Priority.Normal);

        Assert.True(scheduled.TryGetError(out var reason));
        Assert.Equal(ReasonCodes.UnknownKind, reason.Code);
    }

    [Fact]
    public void An_idle_frame_spends_no_time()
    {
        Assert.Equal(TimeSpan.Zero, TimedFrame(Scheduler()));
    }

    [Fact]
    public void A_frame_over_queued_work_allocates_nothing()
    {
        var scheduler = Scheduler(1);
        Ok(scheduler.Schedule(WorkKind.Checking, new CountingJob(_clock, int.MaxValue, Micro), Priority.Normal));
        Ok(scheduler.Schedule(WorkKind.Drawing, new CountingJob(_clock, int.MaxValue, Micro), Priority.Low));

        Allocations.AssertNone(scheduler.RunFrame, "RunFrame");
    }

    [Fact]
    public void The_default_budgets_are_positive_for_every_kind()
    {
        foreach (var kind in Kinds)
            Assert.True(FrameBudgets.Default.For(kind).Length > TimeSpan.Zero);
    }

    [Fact]
    public void A_throwing_lattice_job_ends_and_is_reported_and_the_other_jobs_run_that_frame()
    {
        var faults = new List<(ISlicedWork Work, Exception Error)>();
        var scheduler = new FrameScheduler(_clock, Uniform(2), (work, error) => faults.Add((work, error)));
        var faulty = new SteppedJob(() => throw new InvalidOperationException("lattice bug"));
        var bad = Ok(scheduler.Schedule(WorkKind.Saving, faulty, Priority.High));
        var after = new CountingJob(_clock, 3, Micro);
        var saving = Ok(scheduler.Schedule(WorkKind.Saving, after, Priority.Normal));
        var drawing = new CountingJob(_clock, 3, Micro);
        Ok(scheduler.Schedule(WorkKind.Drawing, drawing, Priority.Normal));

        scheduler.RunFrame();
        scheduler.RunFrame();

        Assert.Equal(JobState.Faulted, bad.State);
        var fault = Assert.Single(faults);
        Assert.Same(faulty, fault.Work);
        Assert.Equal("lattice bug", fault.Error.Message);
        Assert.Equal(JobState.Done, saving.State);
        Assert.Equal(3, drawing.Done);
    }

    [Fact]
    public void Consumer_work_runs_after_lattices_own_whatever_its_priority()
    {
        var scheduler = Scheduler();
        var order = new List<string>();
        Ok(scheduler.ScheduleConsumer(WorkKind.Drawing, new SteppedJob(() => { order.Add("consumer"); return StepOutcome.Done; }), Priority.High));
        Ok(scheduler.Schedule(WorkKind.Drawing, new SteppedJob(() => { order.Add("lattice-low"); return StepOutcome.Done; }), Priority.Low));
        Ok(scheduler.Schedule(WorkKind.Drawing, new SteppedJob(() => { order.Add("lattice-normal"); return StepOutcome.Done; }), Priority.Normal));

        scheduler.RunFrame();

        Assert.Equal(new[] { "lattice-normal", "lattice-low", "consumer" }, order);
    }

    [Fact]
    public void A_throwing_consumer_job_is_not_caught_and_lattices_jobs_of_its_kind_still_ran()
    {
        var faults = 0;
        var scheduler = new FrameScheduler(_clock, Uniform(2), (_, _) => faults++);
        var consumer = Ok(scheduler.ScheduleConsumer(WorkKind.Drawing, new SteppedJob(() => throw new InvalidOperationException("mod bug")), Priority.High));
        var lattice = new CountingJob(_clock, 1, Micro);
        Ok(scheduler.Schedule(WorkKind.Drawing, lattice, Priority.Low));

        var error = Assert.Throws<InvalidOperationException>(scheduler.RunFrame);

        Assert.Equal("mod bug", error.Message);
        Assert.Equal(1, lattice.Done);
        Assert.Equal(0, faults);
        Assert.Equal(JobState.Running, consumer.State);
    }

    private sealed class SteppedJob(Func<StepOutcome> step) : ISlicedWork
    {
        public StepOutcome Step() => step();
    }
}
