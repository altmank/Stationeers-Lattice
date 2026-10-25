using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Lattice.Api;
using Lattice.Core.Saving;
using Lattice.TestKit.Fixtures;
using Lattice.TestKit.World;
using Lattice.Tests.EntryPoint;
using Xunit.Abstractions;

namespace Lattice.Tests.Validation.RealBase;

/// <summary>
/// The live big-base run's load half over a large real base (Vulcan2_4) read offline: a 5,000-ghost plan over the first built
/// structures of the base while they are held back, the rest standing, saved and loaded again (the load's matching
/// pass); the held-back structures then register, <see cref="SpawnPerFrame"/> a frame, and validation settles within a
/// few frames of the last; a game save; a 5,000-ghost new plan beside the base settles; then steady frames, long
/// enough to hold re-check sweeps, run near nothing. Frames are <see cref="FrameGap"/> apart (the game's 40 ms frames
/// on the test server) and each game check costs <see cref="CheckCost"/> of spinning. The lines go to
/// <c>tests/fixtures/generated/completion-perf.txt</c>.
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class CompletionTests(ITestOutputHelper output)
{
    private const int PlanSize = 5000;
    private const int SpawnPerFrame = 500;
    private const int SettleFrames = 30;
    private const int SteadyFrames = 600;
    private const double SteadyMedian = 0.1;
    private static readonly TimeSpan CheckCost = TimeSpan.FromMilliseconds(double.TryParse(Environment.GetEnvironmentVariable("LATTICE_CHECK_MS"), System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : 0.05);
    private static readonly TimeSpan FrameGap = TimeSpan.FromMilliseconds(40);

    [RealBaseFact("Vulcan2_4.save")]
    [Trait(Perf.Trait, Perf.Value)]
    public void Held_back_structures_complete_their_plan_within_a_few_frames_and_steady_frames_cost_near_nothing()
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
        var planned = bench.Settle(TimeSpan.FromSeconds(120));
        Assert.True(planned.Settled, planned.ToString());
        bench.Start(Option.Some(bench.GameSave()), world);
        var loaded = Frames(bench, until: Settled, TimeSpan.FromSeconds(120));
        var bookLoaded = Book(bench);

        var spawn = new List<double>(held.Count / SpawnPerFrame);
        for (var i = 0; i < held.Count; i++)
        {
            world.Register(held[i]);
            if (i % SpawnPerFrame == SpawnPerFrame - 1)
                spawn.AddRange(Frames(bench, until: _ => true, TimeSpan.FromSeconds(5)).Frames);
        }

        var bookSpawned = Book(bench);
        var completion = Frames(bench, until: Settled, TimeSpan.FromSeconds(120));
        var bookCompleted = Book(bench);
        var left = bench.Lattice.Ghosts.Index.Ghosts.Count;
        bench.GameSave();

        var fresh = FreshCells(built, PlanSize).Select(c => RealBaseBench.Draft(new Placement(bench.Catalogue.Named("StructureFrame").Key, c, default))).ToList();
        Assert.IsType<Outcome.Applied>(bench.Submit(new Command.CreatePlan(RealBaseBench.Plan("New wing")) { Ghosts = ValueList.From(fresh) }));
        var checking = Frames(bench, until: Settled, TimeSpan.FromSeconds(120));
        var steady = Frames(bench, until: s => s.Frames.Count >= SteadyFrames, TimeSpan.FromSeconds(120));

        var lines = new List<string>
        {
            $"held-back plan of {drafts.Count} ghosts, settled; loaded again: {loaded}",
            $"held back registered over {spawn.Count} frames, median {Median(spawn):F2} ms, max {spawn.DefaultIfEmpty().Max():F2} ms",
            $"completion of the held-back plan: {completion}; ghosts left {left}",
            $"validation of the new plan: {checking}",
            $"steady: {steady}",
            bench.Diagnose(),
            $"book after the load {bookLoaded}; after the spawn {bookSpawned}; after completion {bookCompleted}; at the end {Book(bench)}; check cost {CheckCost.TotalMilliseconds} ms; checks {bench.Check.Calls.Count}",
        };
        foreach (var line in lines)
            output.WriteLine(line);
        File.WriteAllLines(Path.Combine(Fixtures.Generated, "completion-perf.txt"), lines);

        Assert.True(completion.Settled && completion.Frames.Count <= SettleFrames, lines[2]);
        Assert.True(Median(steady.Frames) <= SteadyMedian, lines[4]);
    }

    private static string Book(RealBaseBench bench)
    {
        var v = bench.Lattice.ValidationModule!;
        var book = v.GetType().GetField("_book", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(v)!;
        var keys = (System.Collections.IEnumerable)book.GetType().GetMethod("Keys")!.Invoke(book, null)!;
        return string.Join(", ", keys.Cast<object>().GroupBy(k => k.GetType().GetProperty("Kind")!.GetValue(k)!.ToString()).Select(g => $"{g.Key} {g.Count()}"));
    }

    private static string Why(RealBaseBench bench)
    {
        var v = bench.Lattice.ValidationModule!;
        object? F(string name) => v.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(v);
        int C(string name) => (F(name) as System.Collections.ICollection)?.Count ?? -1;
        return $"[{((bool)F("_inFlight")! ? "run " : "")}{((bool)F("_asking")! ? "ask " : "")}{((bool)F("_sweeping")! ? "sweep " : "")}{(C("_pending") > 0 ? "pending " : "")}{(C("_asks") > 0 ? "asks " : "")}{(C("_trials") > 0 ? "trials" : "")}]";
    }

    private static bool Settled(Run run) => run.Settled;

    // Frames FrameGap apart until the run is done, each timed whole; Idle counts the frames since validation was last busy.
    private static Run Frames(RealBaseBench bench, Func<Run, bool> until, TimeSpan limit)
    {
        var run = new Run(new List<double>(1024), new List<bool>(1024), 0, Stopwatch.StartNew()) { Why = new Dictionary<string, int>() };
        while (run.Wall.Elapsed < limit)
        {
            var frame = Stopwatch.StartNew();
            bench.Lattice.Frame();
            frame.Stop();
            var busy = bench.Lattice.ValidationModule is { Busy: true };
            if (busy)
            {
                var why = Why(bench);
                run.Why[why] = run.Why.GetValueOrDefault(why) + 1;
            }

            run.Frames.Add(frame.Elapsed.TotalMilliseconds);
            run.Busy.Add(busy);
            run = run with { Idle = busy ? 0 : run.Idle + 1 };
            if (until(run))
                return run with { Took = run.Wall.Elapsed };
            Thread.Sleep(FrameGap);
        }

        return run with { Took = run.Wall.Elapsed };
    }

    private sealed record Run(List<double> Frames, List<bool> Busy, int Idle, Stopwatch Wall)
    {
        public TimeSpan Took { get; init; }

        public Dictionary<string, int> Why { get; init; } = null!;

        public bool Settled => Idle >= 3;

        public override string ToString() =>
            $"{Frames.Count} frames in {Took.TotalSeconds:F1} s (settled {Settled}), {Busy.Count(b => b)} busy; " +
            $"frame median {Median(Frames):F3} ms, p99 {Percentile(Frames, 0.99):F3} ms, max {Frames.DefaultIfEmpty().Max():F3} ms; busy with {string.Join(", ", Why.OrderByDescending(w => w.Value).Take(6).Select(w => $"{w.Key} x{w.Value}"))}";
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

    private static double Median(IEnumerable<double> values) => Percentile(values, 0.5);

    private static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
    }
}
