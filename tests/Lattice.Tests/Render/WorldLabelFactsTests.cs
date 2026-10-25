using System;
using System.Linq;
using Lattice.Api;
using Lattice.Render.Labels;
using Lattice.TestKit;
using Lattice.TestKit.Allocations;
using Lattice.Tests.EntryPoint;
using Lattice.Tests.Runs;

namespace Lattice.Tests.Render;

/// <summary>
/// Label facts from a running world: a ghost's step is its place in its plan's validation report; a bill count is how
/// many of the plan's ghosts share its piece and follows the board; a plan title is the plan's name; a repeated ask on
/// an unchanged board allocates nothing.
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class WorldLabelFactsTests : IDisposable
{
    private readonly WorldBench _bench = new();

    public void Dispose() => _bench.Dispose();

    private Ghost Place(string piece, Cell at)
    {
        var outcome = _bench.Submit(new Command.Place(ValueList.Of(_bench.Draft(piece, at))));
        return Assert.Single(Assert.IsType<Outcome.Applied>(outcome).Changes.Added);
    }

    private static PrefabKey PieceOf(Ghost ghost) => ((Intent.Build)ghost.Intent).Target.Piece;

    [Fact]
    public void A_ghosts_step_is_its_place_in_its_plans_report()
    {
        var world = _bench.Start();
        var ghosts = new[] { Place("ModConduitLine", RunBench.At(0, 0, 0)), Place("ModConduitLine", RunBench.At(4, 0, 0)), Place("ModConduitLine", RunBench.At(8, 0, 0)) };
        _bench.Settle();
        var facts = new WorldLabelFacts(world.Plans, world.Validation, world.Board);
        var report = ((Answer<ValidationReport>.Ready)TestKitIds.Must(world.Validation.Check(new Selector.InPlan(ghosts[0].Plan)))).Value;

        foreach (var ghost in ghosts)
        {
            Assert.True(facts.Step(ghost.Id, out var step));
            Assert.Equal(ghost.Id, report.Findings[step - 1].Ghost);
        }
    }

    [Fact]
    public void A_bill_count_is_the_plans_ghosts_of_the_piece_and_follows_the_board()
    {
        var world = _bench.Start();
        var conduit = Place("ModConduitLine", RunBench.At(0, 0, 0));
        Place("ModConduitLine", RunBench.At(4, 0, 0));
        var facts = new WorldLabelFacts(world.Plans, world.Validation, world.Board);

        Assert.True(facts.BillCount(conduit.Plan, PieceOf(conduit), out var two));
        Assert.Equal(2, two);

        Place("ModConduitLine", RunBench.At(8, 0, 0));
        Assert.True(facts.BillCount(conduit.Plan, PieceOf(conduit), out var three));
        Assert.Equal(3, three);
    }

    [Fact]
    public void A_plan_title_is_the_plans_name()
    {
        var world = _bench.Start();
        var conduit = Place("ModConduitLine", RunBench.At(0, 0, 0));
        var facts = new WorldLabelFacts(world.Plans, world.Validation, world.Board);
        Assert.True(world.Plans.Find(conduit.Plan).TryGet(out var plan));
        var name = plan.Name.Value;

        Assert.Equal(name.Length > 0, facts.PlanTitle(conduit.Plan, out var title));
        Assert.Equal(name, title);
    }

    [Fact]
    public void A_repeated_ask_on_an_unchanged_board_allocates_nothing()
    {
        var world = _bench.Start();
        var conduit = Place("ModConduitLine", RunBench.At(0, 0, 0));
        _bench.Settle();
        var facts = new WorldLabelFacts(world.Plans, world.Validation, world.Board);
        var piece = PieceOf(conduit);

        Allocations.AssertNone(() =>
        {
            facts.Step(conduit.Id, out _);
            facts.BillCount(conduit.Plan, piece, out _);
            facts.PlanTitle(conduit.Plan, out _);
        }, "label facts on an unchanged board");
    }
}
