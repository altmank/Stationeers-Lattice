using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A run bar's choices as a consumer writes them against the contract alone: the route toggles, branches and piece
/// choices, a split, and what a run report says it was laid with.
/// </summary>
public sealed class RouteOptionTests
{
    private static readonly Maker Maker = Make.Maker("runbar", "routes");

    [Fact]
    public void A_run_bar_builds_its_route_and_run_requests_from_its_toggles()
    {
        var lay = new LayOptions(SinglePiecesOnly: true, MatchGrade: true);
        var toggles = new RouteToggles(false, true, false, true, true) { First = Option.Some(FirstLeg.Vertical), HugFramesAndWalls = true };
        var route = new RouteRequest(default, new Cell(0, 0, 0), new Cell(50, 0, 0), toggles, Option<PlanId>.None, Maker)
        {
            Lay = lay,
            Branches = ValueList.Of(new RouteBranch(new Cell(20, 0, 30), Option<PortEnd>.None)),
        };
        var run = new RunRequest(default, new Cell(0, 0, 0), ValueList<Cell>.Empty, new Cell(50, 0, 0), Option<PlanId>.None, Maker) { Lay = lay };

        Assert.Equal(route.Lay, run.Lay);
        Assert.Equal(FirstLeg.Vertical, route.Toggles.First.Reduce(FirstLeg.Level));
        Assert.Single(route.Branches);
        Assert.Equal(LayOptions.Default, new LayOptions(false, false));
    }

    [Fact]
    public void A_split_names_ghosts_or_structures()
    {
        Command[] splits =
        [
            new Command.PlanSplit(new SplitTargets.Ghosts(new Selector.Everything()), Option<PlanId>.None, Maker),
            new Command.PlanSplit(new SplitTargets.Structures(new StructureSelector.Box(new Cell(0, 0, 0), new Cell(20, 0, 20))), Option<PlanId>.None, Maker),
        ];

        foreach (var split in splits)
            Assert.IsType<Command.PlanSplit>(split);
    }

    /// <summary>What a consumer shows for the kit a run was laid with.</summary>
    private static string LaidWith(RunReport report) =>
        report.LaidWith.TryGet(out var kit) ? $"laid with kit {kit.Hash}" : "laid with the kit asked for";

    [Fact]
    public void A_report_that_matched_no_grade_says_it_used_the_kit_asked_for()
    {
        var report = new RunReport(ValueList<Cell>.Empty, ValueList<Placement>.Empty, Option<CellFailure>.None, Make.EmptyBill(), Make.EmptyBill());

        Assert.Equal("laid with the kit asked for", LaidWith(report));
        Assert.Equal("laid with kit 7", LaidWith(report with { LaidWith = Option.Some(new KitKey(7)) }));
    }
}
