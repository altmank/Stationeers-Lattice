using System;
using System.Linq;
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
            Assert.True(_clock.Now - start <= Budget + TimeSpan.FromMilliseconds(0.8));
            Assert.True(++frames < 10_000, "the warm-up made no progress");
        }

        Assert.Equal(JobState.Done, job.State);
        Assert.True(Work(job).Compiled > 50, $"compiled {Work(job).Compiled}");
        Assert.True(frames > 1, "the set was compiled in one slice");
        Assert.True(sets.IsDone("slices-once"));
        Assert.False(WarmUp.Schedule(sets, scheduler, "slices-once", typeof(WarmUp).Assembly, Slices).TryGet(out _));
    }

    [Fact]
    public void A_runtime_whose_compile_costs_over_a_millisecond_a_method_never_stretches_a_frame_past_the_budget_and_one_method()
    {
        var clock = new TickingClock(TimeSpan.Zero);
        var cost = TimeSpan.FromMilliseconds(1.1);
        var scheduler = new FrameScheduler(clock, new FrameBudgets(Create(), Create(), Create(), Create()));
        var job = Job(WarmUp.Schedule(sets, scheduler, "slices-slow", typeof(WarmUp).Assembly, Slices, (_, _) => clock.Advance(cost)));
        var longest = TimeSpan.Zero;
        var frames = 0;
        while (job.State == JobState.Running)
        {
            var start = clock.Now;
            scheduler.RunFrame();
            longest = TimeSpan.FromTicks(Math.Max(longest.Ticks, (clock.Now - start).Ticks));
            Assert.True(++frames < 100_000, "the warm-up made no progress");
        }

        Assert.True(longest <= Budget + cost, $"the longest frame took {longest.TotalMilliseconds} ms");
        Assert.Equal(JobState.Done, job.State);
        Assert.True(Work(job).Skipped > 0);
    }

    [Fact]
    public void Generic_types_closed_over_value_types_and_over_reference_types_are_compiled_when_compiling_is_cheap()
    {
        var compiled = new System.Collections.Generic.List<System.Reflection.MethodBase>();
        var logged = new System.Collections.Generic.List<string>();
        sets.Log = logged.Add;
        var scheduler = Scheduler();
        var job = Job(WarmUp.Schedule(sets, scheduler, "slices-generics", typeof(Warm.Holder).Assembly, ["Lattice.Tests.Slices.Warm"], (method, _) => compiled.Add(method)));
        for (var i = 0; i < 10_000 && job.State == JobState.Running; i++)
            scheduler.RunFrame();

        Assert.Equal(JobState.Done, job.State);
        Assert.Contains(compiled, m => m.DeclaringType == typeof(Warm.Box<string>));
        Assert.Contains(compiled, m => m.DeclaringType == typeof(Warm.Holder));
        Assert.Contains(compiled, m => m.DeclaringType == typeof(Warm.Box<int>));
        Assert.Contains(compiled, m => m.DeclaringType == typeof(Warm.Box<Warm.Colour>));
        Assert.Contains(compiled, m => m.DeclaringType == typeof(int?));
        Assert.Contains(compiled, m => m.DeclaringType == typeof(System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, string>>));
        Assert.Equal(0, Work(job).Skipped);
        Assert.Single(logged);
        Assert.Contains("slices-generics", logged[0]);
    }

    [Fact]
    public void Compiles_that_cost_nothing_do_not_teach_warm_up_that_compiling_is_free()
    {
        var clock = new TickingClock(TimeSpan.Zero);
        var perByte = TimeSpan.FromTicks(200);
        var scheduler = new FrameScheduler(clock, new FrameBudgets(Create(), Create(), Create(), Create()));
        var dear = 0;
        var largestDear = 0;
        var job = Job(WarmUp.Schedule(sets, scheduler, "slices-mixed", typeof(WarmUp).Assembly, Slices, (_, size) =>
        {
            if (size <= 60)
                return;
            dear++;
            largestDear = Math.Max(largestDear, size);
            clock.Advance(perByte * size);
        }));
        var longest = TimeSpan.Zero;
        while (job.State == JobState.Running)
        {
            var start = clock.Now;
            scheduler.RunFrame();
            longest = TimeSpan.FromTicks(Math.Max(longest.Ticks, (clock.Now - start).Ticks));
        }

        Assert.True(longest <= Budget + TimeSpan.FromMilliseconds(3), $"the longest frame took {longest.TotalMilliseconds} ms");
        Assert.True(Work(job).Skipped >= 30, $"{Work(job).Skipped} dear methods were held back");
        Assert.True(largestDear <= 350, $"a dear method of {largestDear} IL bytes was compiled; dear {dear}, held back {Work(job).Skipped}");
    }

    private (WarmUp Job, System.Collections.Generic.List<System.Reflection.MethodBase> Compiled, TimeSpan Longest) Run(string set, TimeSpan valuedCost, TimeSpan plainCost)
    {
        var clock = new TickingClock(TimeSpan.Zero);
        var compiled = new System.Collections.Generic.List<System.Reflection.MethodBase>();
        var scheduler = new FrameScheduler(clock, new FrameBudgets(Create(), Create(), Create(), Create()));
        var job = Job(WarmUp.Schedule(sets, scheduler, set, typeof(Warm.Holder).Assembly, ["Lattice.Tests.Slices.Warm"], (method, _) =>
        {
            compiled.Add(method);
            var valued = method.DeclaringType!.IsGenericType && method.DeclaringType.GetGenericArguments().Any(a => a.IsValueType);
            clock.Advance(valued ? valuedCost : plainCost);
        }));
        var longest = TimeSpan.Zero;
        for (var i = 0; i < 100_000 && job.State == JobState.Running; i++)
        {
            var start = clock.Now;
            scheduler.RunFrame();
            longest = TimeSpan.FromTicks(Math.Max(longest.Ticks, (clock.Now - start).Ticks));
        }

        Assert.Equal(JobState.Done, job.State);
        return (Work(job), compiled, longest);
    }

    [Fact]
    public void Methods_of_generics_over_value_types_that_compile_slowly_are_deferred_once_that_is_learned_and_the_others_are_still_compiled()
    {
        var (work, compiled, _) = Run("slices-slow-valued", TimeSpan.FromMilliseconds(8), TimeSpan.FromMilliseconds(0.1));

        var plain = compiled.Count(m => m.DeclaringType == typeof(Warm.Plain) && m.Name.StartsWith('P'));
        var wide = compiled.Count(m => m.DeclaringType == typeof(Warm.Wide<int>) || m.DeclaringType == typeof(Warm.Wide<long>) || m.DeclaringType == typeof(Warm.Wide<Warm.Colour>));
        Assert.True(plain == 40 && wide <= 40 && work.SkippedValued >= 80, $"plain {plain}, wide {wide}, deferred {work.SkippedValued}, skipped {work.Skipped}");
        Assert.Equal(work.SkippedValued, work.Skipped);
    }

    [Fact]
    public void When_generics_over_value_types_compile_as_fast_as_the_rest_nothing_is_deferred()
    {
        var (work, compiled, _) = Run("slices-fast-valued", TimeSpan.FromMilliseconds(0.1), TimeSpan.FromMilliseconds(0.1));

        Assert.Equal(0, work.Skipped);
        Assert.True(compiled.Count(m => m.DeclaringType == typeof(Warm.Wide<int>)) >= 24);
        Assert.Equal(40, compiled.Count(m => m.DeclaringType == typeof(Warm.Plain) && m.Name.StartsWith('P')));
    }

    [Fact]
    public void A_model_made_pessimistic_by_one_slow_compile_recovers_through_probes_of_smaller_methods()
    {
        var limit = TimeSpan.FromMilliseconds(3);
        var model = new WarmUp.CostModel();
        model.Learn(100, TimeSpan.FromMilliseconds(30));
        Assert.True(model.Expected(500) > limit, "the slow sample did not make a big method look dear");
        Assert.True(model.Expected(150) <= limit, "a small method looks dear: a probe of it would be a hitch");

        var deferred = 0;
        while (model.Expected(500) > limit && deferred < 1000)
        {
            if (model.ShouldProbe())
            {
                model.Probed();
                model.Learn(150, TimeSpan.FromMilliseconds(0.3));
            }
            else
            {
                model.Deferred();
                deferred++;
            }
        }

        Assert.True(deferred < 1000, "the model never recovered");
    }

    [Fact]
    public void Probes_come_after_four_then_eight_then_sixteen_deferrals_and_start_again_after_a_cheap_compile()
    {
        var model = new WarmUp.CostModel();
        var gaps = new System.Collections.Generic.List<int>();
        for (var probes = 0; probes < 5; probes++)
        {
            var gap = 0;
            while (!model.ShouldProbe())
            {
                model.Deferred();
                gap++;
            }

            model.Probed();
            gaps.Add(gap);
        }

        Assert.Equal([4, 8, 16, 16, 16], gaps);
        model.Learn(100, TimeSpan.FromMilliseconds(0.1));
        for (var i = 0; i < 3; i++)
            model.Deferred();
        Assert.False(model.ShouldProbe());
        model.Deferred();
        Assert.True(model.ShouldProbe());
    }
    [Fact]
    public void A_window_of_near_identical_sizes_does_not_extrapolate_a_slope_to_a_big_method()
    {
        var model = new WarmUp.CostModel();
        for (var i = 0; i < 20; i++)
            model.Learn(100 + i % 4, TimeSpan.FromTicks(1000 + i % 4 * 2000));

        Assert.True(model.Expected(1000).Ticks <= 10 * model.Expected(100).Ticks, $"{model.Expected(100)} for 100 bytes, {model.Expected(1000)} for 1000");
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
            clock.Advance(TimeSpan.FromMilliseconds(1.5));
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
