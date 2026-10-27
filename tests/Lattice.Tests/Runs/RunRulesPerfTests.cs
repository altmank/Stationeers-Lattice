using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Lattice.Core.Slices;
using Lattice.TestKit;
using Lattice.Tests.Routes;
using Xunit.Abstractions;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

/// <summary>
/// The cost of judging: a 256-cell drawn run under all four cell rules, and a long route search keeping off lines,
/// against the frame's checking slice; judging allocates under 2 KB a cell.
/// </summary>
[Collection(nameof(Timing))]
public sealed class RunRulesPerfTests(ITestOutputHelper output)
{
    private static readonly RouteToggles AllFour = new(false, false, true, true, true) { KeepOffLines = true };

    /// <summary>A bench with a line across the run every 16 cells and a device beside it every 32.</summary>
    private static RouteBench Crowded()
    {
        var bench = new RouteBench();
        for (var x = 8; x < 256; x += 16)
            for (var z = -2; z <= 6; z++)
                bench.Runs.Real("StructureCableStraight", At(x, 2, z));
        for (var x = 20; x < 256; x += 32)
            bench.Runs.Real("StructureAreaPowerControl", At(x, 2, 4));
        return bench;
    }

    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void A_256_cell_drawn_run_under_all_four_rules_is_judged_well_inside_the_checking_slice()
    {
        var budget = FrameBudgets.Default.Checking.Length;
        var (plainBest, ruledBest) = (TimeSpan.MaxValue, TimeSpan.MaxValue);
        var (plainBytes, ruledBytes) = (0L, 0L);
        RunPlan? ruled = null;
        for (var run = 0; run < 5; run++)
        {
            var bench = Crowded();
            var planner = new RunPlanner(bench.Catalogue, bench.Runs.Grid, bench.Runs.Check, bench.Openings);
            var request = bench.Runs.Run(bench.Runs.Cable, At(0, 2, 2), At(255, 2, 2));
            var withRules = request with { Rules = Option.Some(AllFour) };
            planner.Plan(request, bench.Runs.Boards.View, bench.Runs.Query);

            Perf.Collect();
            var (plain, plainAlloc) = Timed(() => planner.Plan(request, bench.Runs.Boards.View, bench.Runs.Query));
            Perf.Collect();
            var (judged, judgedAlloc) = Timed(() => ruled = TestKitIds.Must(planner.Plan(withRules, bench.Runs.Boards.View, bench.Runs.Query)));
            (plainBest, ruledBest) = (Min(plainBest, plain), Min(ruledBest, judged));
            (plainBytes, ruledBytes) = (plainAlloc, judgedAlloc);
        }

        output.WriteLine($"256 cells: laid {plainBest.TotalMilliseconds:F3} ms / {plainBytes / 1024} KB; laid and judged {ruledBest.TotalMilliseconds:F3} ms / {ruledBytes / 1024} KB");
        Assert.Equal(256, ruled!.Cells.Count);
        Assert.True(ruled.FirstBreak.IsSome);
        Assert.True(ruledBest < budget, $"laid and judged in {ruledBest.TotalMilliseconds} ms");
        Assert.True(ruledBytes - plainBytes < 256 * 2048, $"judging allocated {ruledBytes - plainBytes} B");
    }

    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void Each_step_of_a_long_search_keeping_off_lines_fits_the_checking_slice()
    {
        var budget = FrameBudgets.Default.Checking.Length;
        List<TimeSpan>? fastest = null;
        long bytes = 0;
        var cells = 0;
        for (var run = 0; run < 3; run++)
        {
            var bench = Crowded();
            var request = bench.Route(bench.Runs.Cable, At(0, 2, 2), At(60, 2, 2), AllFour with { NoBridging = false });
            var job = TestKitIds.Must(bench.Planner.Start(request, bench.Runs.Boards.View, bench.Runs.Query));
            var steps = new List<TimeSpan>();
            var watch = new Stopwatch();
            Perf.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var outcome = StepOutcome.More;
            while (outcome == StepOutcome.More)
            {
                watch.Restart();
                outcome = job.Step();
                watch.Stop();
                steps.Add(watch.Elapsed);
            }

            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(job.Outcome.TryGet(out var result));
            cells = TestKitIds.Must(result).Run.Cells.Count;
            fastest = fastest is null || fastest.Count != steps.Count ? steps : fastest.Zip(steps, (a, b) => a < b ? a : b).ToList();
        }

        output.WriteLine($"{cells} cells in {fastest!.Count} steps, slowest {fastest.Max().TotalMilliseconds:F3} ms, {bytes / 1024} KB");
        Assert.All(fastest, step => Assert.True(step < budget, $"a step took {step.TotalMilliseconds} ms"));
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static (TimeSpan Took, long Bytes) Timed(Action act)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        act();
        watch.Stop();
        return (watch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
