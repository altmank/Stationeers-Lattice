using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;

namespace Lattice.Tests.Slices;

public sealed class SliceServiceTests
{
    private static readonly TimeSpan Micro = TimeSpan.FromTicks(10);

    private readonly FakeClock _clock = new();
    private readonly FrameScheduler _scheduler;
    private readonly SliceService _slices;

    public SliceServiceTests()
    {
        _scheduler = new FrameScheduler(_clock, new FrameBudgets(Budget(1), Budget(1), Budget(1), Budget(2)));
        _slices = new SliceService(_scheduler);
    }

    private static SliceBudget Budget(double milliseconds) =>
        SliceBudget.Create(TimeSpan.FromMilliseconds(milliseconds))
            .Match(b => b, r => throw new InvalidOperationException(r.ToString()));

    private static SliceId Id(string name) =>
        SliceId.Create(ModId.Create("planner").Match(m => m, r => throw new InvalidOperationException(r.ToString())), name)
            .Match(i => i, r => throw new InvalidOperationException(r.ToString()));

    private Func<bool> Steps(int count, List<string>? log = null, string name = "")
    {
        var done = 0;
        return () =>
        {
            _clock.Advance(Micro);
            log?.Add(name);
            return ++done < count;
        };
    }

    private IDisposable Schedule(SlicedWorkDescriptor work) =>
        _slices.Schedule(work).Match(h => h, r => throw new InvalidOperationException(r.ToString()));

    private TimeSpan TimedFrame()
    {
        var start = _clock.Now;
        _scheduler.RunFrame();
        return _clock.Now - start;
    }

    [Fact]
    public void A_consumer_step_runs_inside_the_drawing_budget_and_stops_on_dispose()
    {
        var steps = 0;
        var handle = Schedule(new SlicedWorkDescriptor(Id("overlay"), () =>
        {
            _clock.Advance(Micro);
            steps++;
            return true;
        }));

        Assert.Equal(TimeSpan.FromMilliseconds(2), TimedFrame());
        Assert.Equal(2000, steps);

        handle.Dispose();
        _scheduler.RunFrame();

        Assert.Equal(2000, steps);
    }

    [Fact]
    public void Finished_work_is_not_stepped_again_and_frees_its_id()
    {
        var log = new List<string>();
        Schedule(new SlicedWorkDescriptor(Id("overlay"), Steps(3, log, "a")));
        _scheduler.RunFrame();
        _scheduler.RunFrame();

        Assert.Equal(3, log.Count);
        Assert.True(_slices.Schedule(new SlicedWorkDescriptor(Id("overlay"), Steps(1))).IsOk);
    }

    [Fact]
    public void Priorities_order_consumer_work()
    {
        var log = new List<string>();
        Schedule(new SlicedWorkDescriptor(Id("low"), Steps(1, log, "low")) { Priority = SlicePriority.Low });
        Schedule(new SlicedWorkDescriptor(Id("normal"), Steps(1, log, "normal")));
        Schedule(new SlicedWorkDescriptor(Id("high"), Steps(1, log, "high")) { Priority = SlicePriority.High });

        _scheduler.RunFrame();

        Assert.Equal(["high", "normal", "low"], log);
    }

    [Fact]
    public void A_second_work_with_a_queued_id_is_refused()
    {
        Schedule(new SlicedWorkDescriptor(Id("overlay"), Steps(10)));

        Assert.True(_slices.Schedule(new SlicedWorkDescriptor(Id("overlay"), Steps(10))).TryGetError(out var reason));
        Assert.Equal(ReasonCodes.AlreadyRegistered, reason.Code);
    }

    [Fact]
    public void A_stale_handle_leaves_newer_work_with_the_same_id_alone()
    {
        var first = Schedule(new SlicedWorkDescriptor(Id("overlay"), Steps(1)));
        _scheduler.RunFrame();
        var steps = 0;
        Schedule(new SlicedWorkDescriptor(Id("overlay"), () =>
        {
            _clock.Advance(Micro);
            return ++steps < 1_000_000;
        }));

        first.Dispose();
        _scheduler.RunFrame();

        Assert.True(steps > 0);
        Assert.True(_slices.Schedule(new SlicedWorkDescriptor(Id("overlay"), Steps(1))).IsError);
    }

    [Fact]
    public void A_default_id_or_an_unknown_priority_is_refused()
    {
        Assert.True(_slices.Schedule(new SlicedWorkDescriptor(default, Steps(1))).TryGetError(out var noId));
        Assert.Equal(ReasonCodes.DefaultValue, noId.Code);

        var odd = new SlicedWorkDescriptor(Id("odd"), Steps(1)) { Priority = (SlicePriority)9 };
        Assert.True(_slices.Schedule(odd).TryGetError(out var unknown));
        Assert.Equal(ReasonCodes.UnknownKind, unknown.Code);
    }

    [Fact]
    public void Consumer_work_spends_only_the_drawing_budget()
    {
        var checking = 0;
        Assert.True(_scheduler.Schedule(WorkKind.Checking, new CountingJob(_clock, 1_000_000, Micro, _ => checking++), Priority.Normal).IsOk);
        var drawing = 0;
        Schedule(new SlicedWorkDescriptor(Id("overlay"), () =>
        {
            _clock.Advance(Micro);
            return ++drawing < 1_000_000;
        }));

        _scheduler.RunFrame();

        Assert.Equal((1000, 2000), (checking, drawing));
    }
}
