using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Plans;
using Lattice.Core.Safety;
using Lattice.TestKit;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Plans;

/// <summary>
/// A plan larger than one step's share is worked out across steps: its facts are the ones its findings, bill and
/// build safety give at once, and build safety is asked a few ghosts a step, never the whole plan.
/// </summary>
public sealed class PlanStateStepsTests
{
    private const int Size = 200;

    [Fact]
    public void A_big_plans_facts_worked_out_in_steps_are_its_findings_and_bill_at_once()
    {
        // Every third frame fails a mod's rule, so the failing list spans every findings step.
        var bench = new PlanBench(v => v.Rules.Register(new RuleDescriptor(Rule("third"), input =>
            input.Ghost.Id.Value % 3 == 0 ? new RuleVerdict.Fail(Why("third")) : new RuleVerdict.Pass())));
        var frames = bench.V.Ghosts(Frames(Size));

        var facts = bench.Facts();

        var which = new Selector.InPlan(bench.Default);
        var report = Assert.IsType<Answer<ValidationReport>.Ready>(TestKitIds.Must(bench.V.Service.Check(which))).Value;
        var failing = report.Findings.OfType<Finding.WillFail>().ToList();
        Assert.Equal(failing, Assert.IsType<Validity.Invalid>(facts.ValidAlone).Failing);
        Assert.Equal(frames.Where(id => id.Value % 3 != 0), facts.BuildableNext);
        Assert.Equal(TestKitIds.Must(bench.Bills.For(which)), facts.Cost);
        Assert.Equal(new Progress(0, 0, Size), facts.Progress);
    }

    [Fact]
    public void Build_safety_is_asked_a_few_ghosts_a_step_and_its_warnings_reach_the_facts()
    {
        var safety = new CountingSafety(unitsPerGhost: 8);
        var bench = new PlanBench(safety: safety);
        bench.V.Ghosts(Frames(Size));

        var facts = bench.Facts();

        Assert.Equal(safety.Warning, Assert.Single(facts.Consequences));
        Assert.Equal(Size * 8, safety.Units);
        Assert.InRange(safety.MostInOneStep, 1, 8);
    }

    private static (string, Cell)[] Frames(int count) =>
        Enumerable.Range(0, count).Select(i => ("StructureFrameIron", Large(2 * (i % 20), 0, 2 * (i / 20)))).ToArray();

    /// <summary>A build safety stand-in whose run takes a number of units per ghost and counts what each step asks.</summary>
    private sealed class CountingSafety(int unitsPerGhost) : IPlanSafety
    {
        public SafetyWarning Warning { get; } = new(Why("counted"), ValueList<GhostId>.Empty);

        public int Units { get; private set; }

        public int MostInOneStep { get; private set; }

        public ISafetyRun Start(IReadOnlyList<Ghost> plan)
        {
            Units = 0;
            return new Run(this, plan.Count * unitsPerGhost);
        }

        private sealed class Run(CountingSafety owner, int total) : ISafetyRun
        {
            public bool Step(int units)
            {
                var taken = System.Math.Min(units, total - owner.Units);
                owner.Units += taken;
                owner.MostInOneStep = System.Math.Max(owner.MostInOneStep, taken);
                return owner.Units == total;
            }

            public ValueList<SafetyWarning> Warnings() => ValueList.From([owner.Warning]);
        }
    }
}
