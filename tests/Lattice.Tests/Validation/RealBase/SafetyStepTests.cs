using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Openings;
using Lattice.Core.Safety;
using Lattice.TestKit.Fixtures;
using Lattice.Tests.EntryPoint;
using Xunit.Abstractions;

namespace Lattice.Tests.Validation.RealBase;

/// <summary>
/// Build safety as plan state steps it (<see cref="SafetyRun.StepUnits"/> units and <see cref="SafetyRun.StepReads"/>
/// cell reads a step), over a large real base (Vulcan2_4) read offline with a 5,000-ghost plan of the base's own pieces over the
/// standing base. A first run (each check's code compiled on its first call, as the game's runtime does) is reported;
/// then warm runs alternate between steps without a read budget (as before the budget) and with it, five each, and per
/// check the least of each run's worst step is kept, so a scheduler quantum or a collection in one run does not count.
/// Every run gives the same warnings. Lines go to <c>tests/fixtures/generated/safety-steps.txt</c>; run it with
/// <c>DOTNET_TieredCompilation=0</c> to compile as the game does.
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class SafetyStepTests(ITestOutputHelper output)
{
    private const int PlanSize = 5000;
    private const int WarmRuns = 5;
    private const int Checks = 8;
    private const int Vents = 3;
    private const int Controls = 4;

    [RealBaseFact("Vulcan2_4.save")]
    [Trait(Perf.Trait, Perf.Value)]
    public void A_safety_step_on_a_real_base_keeps_its_cell_reads_within_the_budget_and_its_warnings()
    {
        using var bench = RealBaseBench.Load("Vulcan2_4.save");
        var built = bench.Built();
        bench.Start(Option<Lattice.Core.Saving.SaveKey>.None);
        var drafts = ValueList.From(built.Skip(PlanSize).Take(PlanSize)
            .Select(s => RealBaseBench.Draft(new Placement(s.Ref.Piece, s.Ref.At, s.Turn))).ToList());
        var plan = Assert.IsType<Outcome.Applied>(bench.Submit(new Command.CreatePlan(RealBaseBench.Plan("Over")) { Ghosts = drafts })).Changes.Added;
        bench.World.NetworkOf(built[0].Ref.Id);
        var board = bench.Lattice.Ghosts.ViewFor(LatticeIdentity.Actor);
        var safety = new BuildSafety(bench.Catalogue, new CubeGrid(), new CatalogueSafetyFacts(bench.Catalogue, bench.World),
            new CatalogueOpeningFacts(bench.Catalogue), OpeningBands.Default, SafetyLimits.Default);
        SafetyRun Start() => safety.Start(plan, board, bench.World);

        var lines = new List<string> { $"plan {plan.Count} ghosts over {built.Count} structures; tiered compilation {Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default"}; checks: faces, power gather, power cuts, vents, controls, openings, paint, terrain" };
        var first = Run(Start(), SafetyRun.StepReads);
        lines.Add($"first run, read budget {SafetyRun.StepReads}: {first}");
        var unbounded = Enumerable.Repeat(double.MaxValue, Checks).ToArray();
        var bounded = Enumerable.Repeat(double.MaxValue, Checks).ToArray();
        StepTimes? before = null, after = null;
        for (var i = 0; i < WarmRuns; i++)
        {
            before = Run(Start(), int.MaxValue);
            after = Run(Start(), SafetyRun.StepReads);
            Least(unbounded, before.Worst);
            Least(bounded, after.Worst);
            Assert.Equal(first.Warnings, before.Warnings);
            Assert.Equal(first.Warnings, after.Warnings);
        }

        lines.Add($"warm, no read budget: {before!.Counts}; least worst by check {Ms(unbounded)}");
        lines.Add($"warm, read budget {SafetyRun.StepReads}: {after!.Counts}; least worst by check {Ms(bounded)}");
        foreach (var line in lines)
            output.WriteLine(line);
        File.WriteAllLines(Path.Combine(Fixtures.Generated, "safety-steps.txt"), lines);

        Assert.True(bounded[Vents] < 0.5 && bounded[Controls] < 0.5, $"a warm vents or controls step took {Ms(bounded)} ms");
    }

    private static StepTimes Run(SafetyRun run, int reads)
    {
        var steps = new List<double>(10_000);
        var worst = new double[Checks];
        var watch = new Stopwatch();
        var done = false;
        while (!done)
        {
            var check = run.Check;
            watch.Restart();
            done = run.Step(SafetyRun.StepUnits, reads);
            var ms = watch.Elapsed.TotalMilliseconds;
            steps.Add(ms);
            worst[check] = Math.Max(worst[check], ms);
        }

        return new StepTimes(steps, worst, run.Warnings());
    }

    private static void Least(double[] least, double[] worst)
    {
        for (var i = 0; i < least.Length; i++)
            least[i] = Math.Min(least[i], worst[i]);
    }

    private static string Ms(IEnumerable<double> values) => string.Join(" ", values.Select(v => v.ToString("F2", CultureInfo.InvariantCulture)));

    private sealed record StepTimes(List<double> Steps, double[] Worst, ValueList<SafetyWarning> Warnings)
    {
        public string Counts => FormattableString.Invariant(
            $"{Steps.Count} steps, total {Steps.Sum():F0} ms, over 0.5 ms {Steps.Count(s => s > 0.5)}, over 1 ms {Steps.Count(s => s > 1)}, warnings {Warnings.Count}");

        public override string ToString() => $"{Counts}; worst by check {Ms(Worst)}";
    }
}
