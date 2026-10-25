using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Threading;
using Lattice.Api;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.Core.Validation;
using Lattice.TestKit.Fixtures;
using Lattice.TestKit.World;
using Lattice.Tests.EntryPoint;
using Xunit.Abstractions;

namespace Lattice.Tests.Validation.RealBase;

/// <summary>
/// Where a frame's time goes over a large real base (Vulcan2_4) read offline, by window: the first spawned batch (the first calls,
/// reported and not judged, as the first spawn of a session in game), the rest of the held-back spawn of a 5,000-ghost
/// plan, its completion, a 5,000-ghost new plan's validation, then <see cref="SteadyFrames"/> steady frames. Each frame
/// records the checking and saving the scheduler spent, whether validation was sweeping, whether plan state was working
/// and the saving lines still queued. The lines go to <c>tests/fixtures/generated/frame-costs.txt</c>.
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class FrameCostsTests(ITestOutputHelper output)
{
    private const int PlanSize = 5000;
    private const int SpawnPerFrame = 500;
    private const int SteadyFrames = 600;
    private const int SweepFrames = 20;
    private const double SavingFrame = 3.0;
    private const int SavingFrames = 60;
    private static readonly TimeSpan CheckCost = TimeSpan.FromMilliseconds(0.05);
    private static readonly TimeSpan FrameGap = TimeSpan.FromMilliseconds(40);

    [RealBaseFact("Vulcan2_4.save")]
    [Trait(Perf.Trait, Perf.Value)]
    public void Steady_frames_neither_work_out_plan_state_nor_save_and_a_sweep_and_a_saving_step_stay_short()
    {
        var latency = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.Batch;
        try
        {
            Measure();
        }
        finally
        {
            GCSettings.LatencyMode = latency;
        }
    }

    private void Measure()
    {
        using var bench = RealBaseBench.Load("Vulcan2_4.save");
        bench.Check.Rule(_ => Spin(CheckCost));
        var built = bench.Built();
        var held = built.Take(PlanSize).ToList();
        var heldIds = held.Select(s => s.Ref.Id).ToHashSet();
        var world = new FakeWorld(bench.Catalogue);
        foreach (var s in new FakeScan(bench.Save.World).Structures().Where(s => !heldIds.Contains(s.Ref.Id)))
            world.Load(s);
        bench.Start(Option<SaveKey>.None, world);
        var drafts = ValueList.From(held.Select(s => RealBaseBench.Draft(new Placement(s.Ref.Piece, s.Ref.At, s.Turn))).ToList());
        Assert.IsType<Outcome.Applied>(bench.Submit(new Command.CreatePlan(RealBaseBench.Plan("Held back")) { Ghosts = drafts }));
        Assert.True(bench.Settle(TimeSpan.FromSeconds(120)).Settled);
        bench.Start(Option.Some(bench.GameSave()), world);
        var probe = new Probe(bench);
        probe.Run("load", until: w => w.Settled, TimeSpan.FromSeconds(120));

        for (var i = 0; i < SpawnPerFrame; i++)
            world.Register(held[i]);
        probe.Run("first spawn (first calls, not judged)", until: _ => bench.Saving.Pending == 0, TimeSpan.FromSeconds(30));

        for (var i = SpawnPerFrame; i < held.Count; i++)
        {
            world.Register(held[i]);
            if (i % SpawnPerFrame == SpawnPerFrame - 1)
                probe.Run("spawn", until: _ => true, TimeSpan.FromSeconds(5));
        }

        var completion = probe.Run("completion", until: w => w.Settled, TimeSpan.FromSeconds(120));
        var saved = probe.Run("saving", until: w => bench.Saving.Pending == 0, TimeSpan.FromSeconds(120));
        var fresh = FreshCells(built, PlanSize).Select(c => RealBaseBench.Draft(new Placement(bench.Catalogue.Named("StructureFrame").Key, c, default))).ToList();
        Assert.IsType<Outcome.Applied>(bench.Submit(new Command.CreatePlan(RealBaseBench.Plan("New wing")) { Ghosts = ValueList.From(fresh) }));
        probe.Run("validation", until: w => w.Settled, TimeSpan.FromSeconds(120));
        var steady = probe.Run("steady", until: w => w.Frames.Count >= SteadyFrames, TimeSpan.FromSeconds(120));

        var lines = probe.Windows.Select(w => w.ToString()).Append($"check cost {CheckCost.TotalMilliseconds} ms; checks {bench.Check.Calls.Count}").ToList();
        foreach (var line in lines)
            output.WriteLine(line);
        File.WriteAllLines(Path.Combine(Fixtures.Generated, "frame-costs.txt"), lines);

        var spawn = probe.Windows.Where(w => w.Name == "spawn").SelectMany(w => w.Frames).ToList();
        Assert.True(steady.Frames.Count(f => f.Planning) == 0, steady.ToString());
        Assert.True(steady.Frames.Count(f => f.Saving > 0) == 0, steady.ToString());
        Assert.True(completion.Settled && completion.Frames.Count(f => f.Sweeping) <= SweepFrames, completion.ToString());
        Assert.True(bench.Saving.Pending == 0 && saved.Frames.Count <= SavingFrames, saved.ToString());
        Assert.True(spawn.Concat(completion.Frames).Concat(saved.Frames).Max(f => f.Saving - f.Gc) <= SavingFrame, saved.ToString());
    }

    /// <summary>
    /// One frame: whole and spent by kind in milliseconds, the collector pause and the runtime's compiling in it,
    /// validation sweeping, plan state working, saving lines queued. The pause is whole only with the background
    /// collector off (the test runs in batch latency, a collector that stops the world as the game's Mono does): a
    /// background collection keeps the thread waiting far longer than the pause it reports. Compiling is reported, not
    /// taken off: Mono compiles a method on its first call too, which is why the first spawn is its own window.
    /// </summary>
    private readonly record struct Frame(double Whole, double Checking, double Saving, double Gc, double Jit, bool Busy, bool Sweeping, bool Planning, int Queued);

    private sealed record Window(string Name, List<Frame> Frames)
    {
        public int Idle { get; set; }

        public bool Settled => Idle >= 3;

        public override string ToString()
        {
            var saving = Frames.Where(f => f.Saving > 0).ToList();
            return $"{Name}: {Frames.Count} frames (settled {Settled}), whole median {Median(Frames.Select(f => f.Whole)):F3} ms, max {Max(Frames.Select(f => f.Whole)):F3} ms; " +
                   $"checking {Frames.Sum(f => f.Checking):F1} ms in all, max {Max(Frames.Select(f => f.Checking)):F3} ms; " +
                   $"plan state working {Frames.Count(f => f.Planning)} frames; sweeping {Frames.Count(f => f.Sweeping)} frames; busy {Frames.Count(f => f.Busy)}; " +
                   $"saving in {saving.Count} frames, {saving.Sum(f => f.Saving):F1} ms in all, median {Median(saving.Select(f => f.Saving)):F3} ms, max {Max(saving.Select(f => f.Saving)):F3} ms " +
                   $"({Max(saving.Select(f => f.Saving - f.Gc)):F3} ms without collector pauses); compiling {Frames.Sum(f => f.Jit):F1} ms in all, max {Max(Frames.Select(f => f.Jit)):F3} ms; " +
                   $"saving queued at the end {Frames.LastOrDefault().Queued}";
        }
    }

    private sealed class Probe(RealBaseBench bench)
    {
        private static readonly FieldInfo Sweeping = typeof(ValidationService).GetField("_sweeping", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo PlanStates = typeof(Lattice.Core.EntryPoint.LatticeWorld).GetField("_planStates", BindingFlags.Instance | BindingFlags.NonPublic)!;

        public List<Window> Windows { get; } = new();

        public Window Run(string name, Func<Window, bool> until, TimeSpan limit)
        {
            var window = new Window(name, new List<Frame>(1024));
            Windows.Add(window);
            var wall = Stopwatch.StartNew();
            while (wall.Elapsed < limit)
            {
                var paused = GC.GetTotalPauseDuration();
                var compiled = System.Runtime.JitInfo.GetCompilationTime();
                var watch = Stopwatch.StartNew();
                bench.Lattice.Frame();
                watch.Stop();
                var validation = bench.Lattice.ValidationModule!;
                var plans = PlanStates.GetValue(bench.Lattice)!;
                var planning = (bool)plans.GetType().GetField("_working", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plans)!;
                var busy = validation.Busy;
                window.Frames.Add(new Frame(
                    watch.Elapsed.TotalMilliseconds,
                    bench.Lattice.Scheduler.Spent(WorkKind.Checking).TotalMilliseconds,
                    bench.Lattice.Scheduler.Spent(WorkKind.Saving).TotalMilliseconds,
                    (GC.GetTotalPauseDuration() - paused).TotalMilliseconds,
                    (System.Runtime.JitInfo.GetCompilationTime() - compiled).TotalMilliseconds,
                    busy,
                    (bool)Sweeping.GetValue(validation)!,
                    planning,
                    bench.Saving.Pending));
                window.Idle = busy || !bench.AnswersReady() ? 0 : window.Idle + 1;
                if (until(window))
                    break;
                Thread.Sleep(FrameGap);
            }

            return window;
        }
    }

    // Large cells in a layer 20 m over the base's highest large cell, starting at its lowest corner, one cell apart.
    private static IEnumerable<Cell> FreshCells(List<StructureInfo> built, int count)
    {
        var large = built.Where(s => s.Ref.At.X % 20 != 0 && Math.Abs(s.Ref.At.X % 20) == 10).ToList();
        var minX = large.Min(s => s.Ref.At.X);
        var minZ = large.Min(s => s.Ref.At.Z);
        var y = large.Max(s => s.Ref.At.Y) + 200;
        var side = (int)Math.Ceiling(Math.Sqrt(count));
        for (var i = 0; i < count; i++)
            yield return new Cell(minX + 20 * (i % side), y, minZ + 20 * (i / side));
    }

    private static Option<Reason> Spin(TimeSpan cost)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < cost)
        {
        }

        return Option<Reason>.None;
    }

    private static double Max(IEnumerable<double> values) => values.DefaultIfEmpty().Max();

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
