using System.Linq;
using Lattice.Api;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Lattice.Tests.Catalogue;
using Lattice.Tests.Routes;
using Xunit;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

/// <summary>
/// Items flow along a chute run from its From to its To, and a piece with input and output ends is laid to pass them
/// that way: an input never faces where items leave, an output never where they enter (decompile
/// Assets.Scripts.Objects.Pipes/Chute.cs, IsValidInputConnection; ChuteJunction.cs). A line the run meets keeps its flow
/// where a roled end on it gives one; where none does, Lattice chooses one and reports it.
/// </summary>
public sealed class ChuteFlowTests
{
    private const string OneWay = """
        lattice-catalogue 3
        piece ModChuteOneWay kit=ItemModFlow option=0 grid=small family=chute
          step material=ItemModFlow*1 exit=ItemWrench air=no
          end 0 0 0 x- chute role=input
          end 0 0 0 x+ chute role=output
          cell 0 0 0 chute
          collision 23

        piece ModChuteOneWay3 kit=ItemModFlow option=1 grid=small family=chute
          step material=ItemModFlow*2 exit=ItemWrench air=no
          end 0 0 0 z+ chute role=input
          end 0 0 -10 z- chute role=output
          cell 0 0 0 chute
          cell 0 0 -5 chute
          cell 0 0 -10 chute
          collision 23
        """;

    private const string Junction = "StructureChuteJunction";
    private const string Straight = "StructureChuteStraight";

    private static Steering SteeringOf(RunBench bench, Placement piece) =>
        Steering.Of(bench.Catalogue.Named(bench.NameOf(piece.Piece)), piece.Turn, bench.Grid);

    private static StructureRef[] RealLine(RunBench bench, int cells) =>
        [.. Enumerable.Range(0, cells).Select(x => bench.Real(Straight, At(x, 0, 0)))];

    private static RunBench Game() => new(GameCatalogue.Load());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_run_lays_every_directional_piece_pointing_from_its_start_to_its_end(bool reversed)
    {
        var bench = new RunBench(OneWay);
        var (from, to) = reversed ? (At(6, 0, 0), At(0, 0, 0)) : (At(0, 0, 0), At(6, 0, 0));
        var (upstream, downstream) = reversed ? ("x+", "x-") : ("x-", "x+");

        var plan = bench.Plan(bench.Run(bench.KitOf("ModChuteOneWay"), from, to));

        Assert.False(plan.FirstFailure.IsSome);
        Assert.Contains(plan.Pieces, p => bench.NameOf(p.Piece) == "ModChuteOneWay3");
        Assert.Equal(7, plan.Pieces.Sum(p => bench.CellsOf(p).Count));
        foreach (var piece in plan.Pieces)
        {
            var steering = SteeringOf(bench, piece);
            Assert.Equal(Ways(upstream), steering.Ins);
            Assert.Equal(Ways(downstream), steering.Outs);
        }

        Assert.Empty(plan.FlowChosen);
    }

    [GameCatalogueFact]
    public void A_route_branch_flows_into_the_main_run_whose_junction_output_goes_on_toward_its_end()
    {
        foreach (var reversed in new[] { false, true })
        {
            var bench = new RouteBench(Game());
            var (from, to) = reversed ? (At(12, 2, 2), At(0, 2, 2)) : (At(0, 2, 2), At(12, 2, 2));
            var request = bench.Route(bench.Runs.KitOf(Straight), from, to)
                with { Branches = ValueList.From(new[] { new RouteBranch(At(6, 2, 6), Option<PortEnd>.None) }) };

            var plan = bench.Plan(request);

            var join = Assert.Single(plan.Run.Pieces, p => bench.Runs.CellsOf(p).Contains(At(6, 2, 2)));
            Assert.Equal(Junction, bench.Runs.NameOf(join.Piece));
            var steering = SteeringOf(bench.Runs, join);
            Assert.Equal(Ways(reversed ? "x-" : "x+"), steering.Outs);
            Assert.Equal(Ways(reversed ? "x+,z+" : "x-,z+"), steering.Ins);
            Assert.Empty(plan.Run.FlowChosen);
        }
    }

    [GameCatalogueFact]
    public void A_run_ending_on_a_plain_line_mid_way_is_the_junctions_input_and_the_line_flow_it_chose_is_reported()
    {
        var bench = Game();
        var line = RealLine(bench, 3);
        var middle = At(1, 0, 0);

        var plan = bench.Plan(bench.Run(bench.KitOf(Straight), At(1, 0, 3), middle));

        Assert.False(plan.FirstFailure.IsSome);
        var swap = Assert.Single(plan.Changes.OfType<Intent.Swap>());
        Assert.Equal(line[1], swap.Old);
        Assert.Equal(Junction, bench.NameOf(swap.New.Piece));
        var steering = SteeringOf(bench, swap.New);
        Assert.Contains(Direction.ZPlus, Each(steering.Ins));
        Assert.Equal(1, steering.Outs.Count);
        Assert.Equal(Axis.X, Each(steering.Outs).Single().AxisOf());
        var chosen = Assert.Single(plan.FlowChosen);
        Assert.Equal(middle, chosen.At);
        Assert.Equal(Each(steering.Outs).Single().ToFace(), chosen.Downstream);
    }

    [GameCatalogueFact]
    public void A_run_ending_on_a_line_a_bin_gives_a_flow_keeps_that_flow_through_the_junction()
    {
        foreach (var (bin, downstream) in new[] { ("StructureChuteBin", "x-"), ("StructureChuteExportBin", "x+") })
        {
            var bench = Game();
            RealLine(bench, 3);
            bench.Real(bin, At(3, 0, 0), bench.Sending(Direction.YMinus, Direction.XMinus));

            var plan = bench.Plan(bench.Run(bench.KitOf(Straight), At(1, 0, 3), At(1, 0, 0)));

            Assert.False(plan.FirstFailure.IsSome);
            var swap = Assert.Single(plan.Changes.OfType<Intent.Swap>());
            var steering = SteeringOf(bench, swap.New);
            Assert.Equal(Ways(downstream), steering.Outs);
            Assert.Empty(plan.FlowChosen);
        }
    }

    [GameCatalogueFact]
    public void A_run_starting_from_a_line_mid_way_fails_flow_blocked_at_that_cell()
    {
        foreach (var withBin in new[] { false, true })
        {
            var bench = Game();
            RealLine(bench, 3);
            if (withBin)
                bench.Real("StructureChuteBin", At(3, 0, 0), bench.Sending(Direction.YMinus, Direction.XMinus));
            var middle = At(1, 0, 0);

            var plan = bench.Plan(bench.Run(bench.KitOf(Straight), middle, At(1, 0, 3)));

            Assert.True(plan.FirstFailure.TryGet(out var failure));
            Assert.Equal(middle, failure.At);
            Assert.Equal(ReasonCodes.FlowBlocked, failure.Why.Code);
            Assert.Equal("x+,x-,z+", failure.Why.Facts["shape"]);
            Assert.DoesNotContain(plan.Changes, c => c is Intent.Swap);
        }
    }

    [Fact]
    public void A_cable_tee_off_a_line_chooses_no_flow()
    {
        var bench = new RunBench();
        foreach (var x in Enumerable.Range(0, 3))
            bench.Real("StructureCableStraight", At(x, 0, 0), bench.Sending(Direction.ZPlus, Direction.XPlus));

        var plan = bench.Plan(bench.Run(bench.Cable, At(1, 0, 0), At(1, 0, 3)));

        Assert.False(plan.FirstFailure.IsSome);
        Assert.Empty(plan.FlowChosen);
        Assert.Equal(Ways("x+,x-,z+"), bench.WaysOf(Assert.Single(plan.Changes.OfType<Intent.Swap>()).New, At(1, 0, 0)));
    }
}
