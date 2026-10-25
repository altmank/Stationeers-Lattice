using System.Linq;
using Lattice.Api;
using Lattice.Core.Runs;
using Lattice.Tests.Routes;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

/// <summary><see cref="LayOptions.SinglePiecesOnly"/>: every cell its own piece, on a drawn run and on a route alike.</summary>
public sealed class SinglePiecesTests
{
    private static readonly Cell Start = At(0, 0, 0);
    private static readonly Cell End = At(11, 0, 0);
    private static readonly LayOptions Singles = new(SinglePiecesOnly: true, MatchGrade: false);

    private static int Cells(RunBench bench, RunPlan plan) => plan.Pieces.Sum(p => bench.CellsOf(p).Count);

    [Fact]
    public void A_twelve_cell_run_lays_twelve_single_pieces_with_single_pieces_only()
    {
        var bench = new RunBench();

        var plan = bench.Plan(bench.Run(bench.Cable, Start, End) with { Lay = Singles });

        Assert.Equal(12, plan.Pieces.Count);
        Assert.All(plan.Pieces, p => Assert.Single(bench.CellsOf(p)));
        Assert.Equal(plan.Singles, plan.Pieces);
    }

    [Fact]
    public void A_twelve_cell_run_lays_long_straights_without_it()
    {
        var bench = new RunBench();

        var plan = bench.Plan(bench.Run(bench.Cable, Start, End));

        Assert.True(plan.Pieces.Count < 12);
        Assert.Contains(plan.Pieces, p => bench.CellsOf(p).Count > 1);
        Assert.Equal(12, Cells(bench, plan));
    }

    [Fact]
    public void A_twelve_cell_route_lays_twelve_single_pieces_with_single_pieces_only()
    {
        var bench = new RouteBench();

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, Start, End) with { Lay = Singles });

        Assert.Equal(12, plan.Run.Pieces.Count);
        Assert.All(plan.Run.Pieces, p => Assert.Single(bench.Runs.CellsOf(p)));
        Assert.Equal(Singles, plan.AsRun.Lay);
    }

    [Fact]
    public void A_twelve_cell_route_lays_long_straights_without_it()
    {
        var bench = new RouteBench();

        var plan = bench.Plan(bench.Route(bench.Runs.Cable, Start, End));

        Assert.Contains(plan.Run.Pieces, p => bench.Runs.CellsOf(p).Count > 1);
    }
}
