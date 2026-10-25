using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Lattice.Api;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.TestKit.Fixtures;
using Lattice.TestKit.World;
using Lattice.Tests.EntryPoint;
using Xunit.Abstractions;

namespace Lattice.Tests.Validation.RealBase;

/// <summary>
/// Steady frames over a large real base (Vulcan2_4) read offline: a 5,000-ghost plan of the base's own pieces settles, then
/// Lattice runs frames with nothing new to do for long enough to hold two of validation's re-check sweeps. A frame
/// with nothing to do costs near nothing: the 99th percentile of Lattice frames, judged on the main thread's CPU time
/// plus collector pauses (the test machine is shared, so wall time carries other processes' turns), stays under <see cref="SteadyP99"/>. Each game check costs <see cref="CheckCost"/> of spinning, standing in for the game's own
/// placement check (the fake answers at once). The lines go to <c>tests/fixtures/generated/steady-perf.txt</c>.
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class SteadyFrameTests(ITestOutputHelper output)
{
    private const int PlanSize = 5000;
    private const double SteadyP99 = 0.75;
    private static readonly TimeSpan CheckCost = TimeSpan.FromMilliseconds(0.05);
    private static readonly TimeSpan Steady = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan FrameGap = TimeSpan.FromMilliseconds(16);

    [RealBaseFact("Vulcan2_4.save")]
    [Trait(Perf.Trait, Perf.Value)]
    public void Steady_frames_on_a_settled_real_base_cost_near_nothing()
    {
        using var bench = RealBaseBench.Load("Vulcan2_4.save");
        bench.Check.Rule(_ => Spin(CheckCost));
        var built = bench.Built();
        bench.Start(Option<SaveKey>.None, new FakeWorld(bench.Catalogue));
        var drafts = ValueList.From(built.Take(PlanSize).Select(s => RealBaseBench.Draft(new Placement(s.Ref.Piece, s.Ref.At, s.Turn))).ToList());
        Assert.IsType<Outcome.Applied>(bench.Submit(new Command.CreatePlan(RealBaseBench.Plan("The base")) { Ghosts = drafts }));
        var settled = bench.Settle(TimeSpan.FromSeconds(120));
        Assert.True(settled.Settled, settled.ToString());

        var cpu = ThreadCpu.Calibrate();
        var frames = new List<(double Whole, double Judged, double Checking, double Gc, bool Sweeping)>(2048);
        var sweeping = typeof(Lattice.Core.Validation.ValidationService).GetField("_sweeping", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var runs = 0;
        bench.Lattice.ValidationModule!.Ran += _ => runs++;
        var all = Stopwatch.StartNew();
        while (all.Elapsed < Steady)
        {
            var paused = GC.GetTotalPauseDuration();
            var cycles = cpu.Now();
            var frame = Stopwatch.StartNew();
            bench.Lattice.Frame();
            frame.Stop();
            var gc = (GC.GetTotalPauseDuration() - paused).TotalMilliseconds;
            var judged = cpu.Measures ? cpu.Between(cycles, cpu.Now()).TotalMilliseconds + gc : frame.Elapsed.TotalMilliseconds;
            frames.Add((frame.Elapsed.TotalMilliseconds, judged, bench.Lattice.Scheduler.Spent(WorkKind.Checking).TotalMilliseconds,
                gc, (bool)sweeping.GetValue(bench.Lattice.ValidationModule)!));
            Thread.Sleep(FrameGap);
        }

        var judgedFrames = frames.Select(f => f.Judged).OrderBy(m => m).ToList();
        var whole = frames.Select(f => f.Whole).OrderBy(m => m).ToList();
        double P(double p) => judgedFrames[Math.Min(judgedFrames.Count - 1, (int)Math.Ceiling(p * judgedFrames.Count) - 1)];
        double W(double p) => whole[Math.Min(whole.Count - 1, (int)Math.Ceiling(p * whole.Count) - 1)];
        var lines = new List<string>
        {
            $"settle: {settled}",
            $"steady: {frames.Count} frames over {Steady.TotalSeconds:F0} s, judged on {cpu}, collector pauses added: median {P(0.5):F3} ms, p99 {P(0.99):F3} ms, max {judgedFrames[^1]:F3} ms",
            $"steady wall clock (the thread descheduled included): median {W(0.5):F3} ms, p99 {W(0.99):F3} ms, max {whole[^1]:F3} ms",
            $"frames while sweeping: {frames.Count(f => f.Sweeping)}, their checking median {Median(frames.Where(f => f.Sweeping).Select(f => f.Checking)):F3} ms, max {frames.Where(f => f.Sweeping).Select(f => f.Checking).DefaultIfEmpty().Max():F3} ms",
            $"frames not sweeping: judged median {Median(frames.Where(f => !f.Sweeping).Select(f => f.Judged)):F3} ms, max {frames.Where(f => !f.Sweeping).Select(f => f.Judged).DefaultIfEmpty().Max():F3} ms",
            $"frames with a collector pause: {frames.Count(f => f.Gc > 0)}, longest {frames.Select(f => f.Gc).DefaultIfEmpty().Max():F1} ms",
            $"worst judged frames (judged, wall, checking, pause, sweeping): {string.Join("; ", frames.OrderByDescending(f => f.Judged).Take(5).Select(f => $"{f.Judged:F3} {f.Whole:F3} {f.Checking:F3} {f.Gc:F1} {f.Sweeping}"))}",
            $"frames over {SteadyP99} ms (index: judged, sweeping): {string.Join("; ", frames.Select((f, i) => (f, i)).Where(x => x.f.Judged > SteadyP99).Select(x => $"{x.i}: {x.f.Judged:F3} {x.f.Sweeping}"))}",
            $"game checks asked: {bench.Check.Calls.Count}; validation runs while steady: {runs}",
        };
        foreach (var line in lines)
            output.WriteLine(line);
        File.WriteAllLines(Path.Combine(Fixtures.Generated, "steady-perf.txt"), lines);

        Assert.True(P(0.99) <= SteadyP99, lines[1]);
    }

    private static Option<Reason> Spin(TimeSpan cost)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < cost)
        {
        }

        return Option<Reason>.None;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
