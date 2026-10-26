using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Slices;
using Lattice.TestKit;
using Xunit;
using Xunit.Abstractions;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Routes;

[Collection(nameof(Timing))]
public sealed class ViaPerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void Each_step_of_a_route_through_four_pins_fits_the_checking_slice()
    {
        var budget = FrameBudgets.Default.Checking.Length;
        var pins = ValueList.From(new[] { At(10, 2, 12), At(20, 8, 12), At(30, 8, -6), At(40, 2, 4) });
        List<TimeSpan>? fastest = null;
        var cells = 0;
        for (var run = 0; run < 3; run++)
        {
            var bench = new RouteBench();
            for (var x = 4; x < 40; x += 6)
                bench.Runs.Real("StructureAreaPowerControl", At(x, 2, 2));
            var request = bench.Route(bench.Runs.Cable, At(0, 2, 2), At(48, 2, 2)) with { Via = pins };
            var job = TestKitIds.Must(bench.Planner.Start(request, bench.Runs.Boards.View, bench.Runs.Query));
            var steps = new List<TimeSpan>();
            var watch = new Stopwatch();
            Perf.Collect();
            var outcome = StepOutcome.More;
            while (outcome == StepOutcome.More)
            {
                watch.Restart();
                outcome = job.Step();
                watch.Stop();
                steps.Add(watch.Elapsed);
            }

            Assert.True(job.Outcome.TryGet(out var result));
            cells = TestKitIds.Must(result).Run.Cells.Count;
            fastest = fastest is null || fastest.Count != steps.Count ? steps : fastest.Zip(steps, (a, b) => a < b ? a : b).ToList();
        }

        output.WriteLine($"{cells} cells in {fastest!.Count} steps, slowest {fastest.Max().TotalMilliseconds:F3} ms");
        Assert.True(fastest.Count > 4);
        Assert.All(fastest, step => Assert.True(step < budget, $"a step took {step.TotalMilliseconds} ms"));
    }
}
