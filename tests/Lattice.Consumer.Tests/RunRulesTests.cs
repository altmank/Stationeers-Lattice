using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A run bar's line and rule choices against the contract alone: a route that keeps off lines, a drag judged by the
/// same rules, and a mark for the first cell that breaks one or fails to lay.
/// </summary>
public sealed class RunRulesTests
{
    private static readonly Maker Maker = Make.Maker("runbar", "rules");

    /// <summary>Where a drag preview puts its mark: the first break, else the first lay failure.</summary>
    private static Option<Cell> Mark(RunReport report) =>
        (report.FirstBreak.IsSome ? report.FirstBreak : report.FirstFailure).Map(static at => at.At);

    [Fact]
    public void A_run_bar_asks_a_route_and_a_drag_for_the_same_rules()
    {
        var rules = new RouteToggles(false, false, true, true, true) { KeepOffLines = true };
        var route = new RouteRequest(default, new Cell(0, 0, 0), new Cell(50, 0, 0), rules, Option<PlanId>.None, Maker);
        var drag = new RunRequest(default, new Cell(0, 0, 0), ValueList<Cell>.Empty, new Cell(50, 0, 0), Option<PlanId>.None, Maker)
        {
            Rules = Option.Some(rules),
        };

        Assert.True(route.Toggles.KeepOffLines);
        Assert.Equal(route.Toggles, drag.Rules.Reduce(new RouteToggles(false, false, false, false, false)));
    }

    [Fact]
    public void The_mark_is_the_first_break_before_the_first_failure()
    {
        var broken = new CellFailure(new Cell(5, 0, 0), Reason.Of(ReasonCodes.RuleBroken, RuleId.Contract,
            ValueMap.Of("cell", "5,0,0").With("rule", "along-line")));
        var failed = new CellFailure(new Cell(30, 0, 0), Reason.Of(ReasonCodes.GameRefused, RuleId.Contract));
        var report = new RunReport(ValueList<Cell>.Empty, ValueList<Placement>.Empty, Option.Some(failed), Make.EmptyBill(), Make.EmptyBill());

        Assert.Equal(new Cell(30, 0, 0), Mark(report).Reduce(default(Cell)));
        Assert.Equal(new Cell(5, 0, 0), Mark(report with { FirstBreak = Option.Some(broken) }).Reduce(default(Cell)));
        Assert.Equal("along-line", broken.Why.Facts["rule"]);
    }
}
