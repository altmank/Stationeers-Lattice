using System;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Preview;
using Lattice.Tests.Routes;
using static Lattice.Tests.Routes.RouteBench;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

/// <summary>
/// <see cref="RunRequest.Rules"/>: a drawn run's cells judged by a route's cell rules, its two ends exempt. The first
/// cell that breaks one is <see cref="RunPlan.FirstBreak"/> (<see cref="ReasonCodes.RuleBroken"/>, facts <c>cell</c>
/// and <c>rule</c>), beside the laying's first failure; the run is laid as drawn whatever it breaks.
/// </summary>
public sealed class RunRulesTests
{
    private static readonly Cell Start = At(2, 2, 2);
    private static readonly Cell Far = At(18, 2, 2);

    private static RunPlanner Planner(RouteBench bench) => new(bench.Catalogue, bench.Runs.Grid, bench.Runs.Check, bench.Openings);

    private static RunPlan Plan(RouteBench bench, RunRequest request) =>
        TestKitIds.Must(Planner(bench).Plan(request, bench.Runs.Boards.View, bench.Runs.Query));

    private static readonly Bill NoBill = new(ValueList<ItemQuantity>.Empty, ValueList<ItemQuantity>.Empty, ValueList<ItemDelta>.Empty, ValueSet<ItemKey>.Empty);

    private static RunRequest Ruled(RunRequest request, RouteToggles rules) => request with { Rules = Option.Some(rules) };

    /// <summary>Real cable straights along z through the cells x, y=2, z from <paramref name="fromZ"/> to <paramref name="toZ"/>.</summary>
    private static void LineAlongZ(RouteBench bench, int x, int fromZ, int toZ)
    {
        for (var z = fromZ; z <= toZ; z++)
            bench.Runs.Real("StructureCableStraight", At(x, 2, z));
    }

    private static CellFailure Break(RunPlan plan) =>
        plan.FirstBreak.Match(static b => b, () => throw new Xunit.Sdk.XunitException("no break"));

    private static void AssertBreak(RunPlan plan, Cell at, string rule)
    {
        var broken = Break(plan);
        Assert.Equal(at, broken.At);
        Assert.Equal(ReasonCodes.RuleBroken, broken.Why.Code);
        Assert.Equal($"{at.X},{at.Y},{at.Z}", broken.Why.Facts["cell"]);
        Assert.Equal(rule, broken.Why.Facts["rule"]);
    }

    [Fact]
    public void A_drawn_l_through_another_networks_cable_breaks_no_bridging_there_and_is_laid_in_full()
    {
        var bench = new RouteBench();
        LineAlongZ(bench, 4, 2, 2);
        var request = bench.Runs.Run(bench.Runs.Cable, At(0, 2, 2), At(8, 2, 8), At(8, 2, 2));

        var ruled = Plan(bench, Ruled(request, Off with { NoBridging = true }));
        var plain = Plan(bench, request);

        AssertBreak(ruled, At(4, 2, 2), "join");
        Assert.Equal(plain.Cells, ruled.Cells);
        Assert.Equal(plain.Changes, ruled.Changes);
        Assert.Equal(plain.FirstFailure, ruled.FirstFailure);
    }

    [Fact]
    public void A_doors_keep_out_breaks_the_opening_rule_at_its_first_cell()
    {
        var bench = new RouteBench();
        bench.Runs.Real("ModHatch", Large(2, 0, 0));

        var plan = Plan(bench, Ruled(bench.Runs.Run(bench.Runs.Cable, Large(0, 0, 0), Large(6, 0, 0)), Off with { KeepDoorsAndWindowsClear = true }));

        AssertBreak(plan, new Cell(150, 110, 110), "opening");
    }

    [Fact]
    public void A_free_device_port_breaks_the_port_rule()
    {
        var bench = new RouteBench();
        bench.Runs.Real("StructureAreaPowerControl", At(10, 2, 3), bench.Sending(Direction.XMinus, Direction.ZMinus));

        var plan = Plan(bench, Ruled(bench.Runs.Run(bench.Runs.Cable, Start, Far), Off with { KeepPortsFree = true }));

        AssertBreak(plan, At(10, 2, 2), "port");
    }

    [Fact]
    public void Running_into_a_line_breaks_keep_off_lines_and_only_the_first_line_is_reported()
    {
        var bench = new RouteBench();
        LineAlongZ(bench, 6, -1, 5);
        LineAlongZ(bench, 12, -1, 5);

        var plan = Plan(bench, Ruled(bench.Runs.Run(bench.Runs.Cable, Start, Far), Off with { KeepOffLines = true }));

        AssertBreak(plan, At(6, 2, 2), "along-line");
    }

    [Fact]
    public void A_drag_from_a_lines_end_along_it_breaks_at_the_next_cell_not_at_its_start()
    {
        var bench = new RouteBench();
        for (var x = 0; x <= 3; x++)
            bench.Runs.Real("StructureCableStraight", At(x, 2, 2), bench.Sending(Direction.ZPlus, Direction.XPlus));

        var plan = Plan(bench, Ruled(bench.Runs.Run(bench.Runs.Cable, At(3, 2, 2), At(8, 2, 2)), Off with { KeepOffLines = true }));

        AssertBreak(plan, At(4, 2, 2), "along-line");
    }

    [Fact]
    public void A_break_and_a_lay_failure_are_each_reported_in_their_own_member()
    {
        var bench = new RouteBench();
        LineAlongZ(bench, 3, 0, 4);
        bench.Runs.Check.Refuse(p => bench.Runs.CellsOf(p).Contains(At(9, 2, 2)), ReasonCodes.GameRefused, "in the way");

        var plan = Plan(bench, Ruled(bench.Runs.Run(bench.Runs.Cable, At(0, 2, 2), At(12, 2, 2)), Off with { NoBridging = true }));

        AssertBreak(plan, At(3, 2, 2), "join");
        var failure = plan.FirstFailure.Match(static f => f, () => throw new Xunit.Sdk.XunitException("no failure"));
        Assert.Equal(At(9, 2, 2), failure.At);
        Assert.Equal(ReasonCodes.GameRefused, failure.Why.Code);
    }

    [Fact]
    public void Without_rules_nothing_is_judged_and_the_run_is_laid_as_with_rules_all_off()
    {
        var bench = new RouteBench();
        LineAlongZ(bench, 6, -1, 5);
        var request = bench.Runs.Run(bench.Runs.Cable, Start, Far);

        var plain = Plan(bench, request);
        var allOff = Plan(bench, Ruled(request, Off));

        Assert.True(plain.FirstBreak.IsNone);
        Assert.True(allOff.FirstBreak.IsNone);
        Assert.Equal(plain.Cells, allOff.Cells);
        Assert.Equal(plain.Changes, allOff.Changes);
        Assert.Equal(plain.Report(_ => NoBill), allOff.Report(_ => NoBill));
    }

    [Fact]
    public void The_report_carries_the_break_beside_the_failure()
    {
        var bench = new RouteBench();
        LineAlongZ(bench, 6, -1, 5);

        var report = Plan(bench, Ruled(bench.Runs.Run(bench.Runs.Cable, Start, Far), Off with { KeepOffLines = true })).Report(_ => NoBill);

        Assert.Equal(At(6, 2, 2), report.FirstBreak.Match(static b => b.At, () => default));
        Assert.True(report.FirstFailure.IsNone);
    }

    [Fact]
    public void A_submitted_run_that_breaks_a_rule_places_every_ghost_and_its_preview_names_the_break()
    {
        var bench = new PreviewBench();
        for (var z = 0; z <= 4; z++)
            bench.Runs.Real("StructureCableStraight", At(3, 2, z));
        bench.Start();
        var run = new Command.PlanRun(Ruled(bench.Runs.Run(bench.Runs.Cable, At(0, 2, 2), At(8, 2, 2)), Off with { NoBridging = true }));

        var preview = bench.Ok(run);
        var applied = TestBoard.Applied(Submit(bench, run));

        var report = preview.Run.Match(static r => r, () => throw new Xunit.Sdk.XunitException("no run report"));
        Assert.Equal(At(3, 2, 2), report.FirstBreak.Match(static b => b.At, () => default));
        Assert.Equal(preview.WouldChange.Added.Count, applied.Added.Count);
        Assert.All(report.Cells, cell => Assert.NotEmpty(bench.Runs.Boards.View.At(cell)));
    }

    private static Outcome Submit(PreviewBench bench, Command command)
    {
        var id = new CommandId(Guid.NewGuid());
        Outcome? answered = null;
        void Answered(Ticket ticket, Outcome outcome)
        {
            if (ticket == new Ticket(id))
                answered = outcome;
        }

        bench.Events.Answered += Answered;
        try
        {
            if (bench.Commands.Submit(id, command, TestBoard.Alice) is Submission.Answered now)
                return now.Outcome;
            for (var i = 0; i < 200 && answered is null; i++)
                bench.Frame();
            return answered ?? throw new InvalidOperationException("not answered");
        }
        finally
        {
            bench.Events.Answered -= Answered;
        }
    }
}
