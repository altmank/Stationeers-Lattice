using System.Collections.Generic;
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
/// A chute run branches: the game's chute junction gives its ends a flow role (two inputs into one output, decompile
/// Assets.Scripts.Objects.Pipes/ChuteJunction.cs), and a piece of a network's own family is a run piece whatever roles
/// its ends carry. A device with roled ends stays out of a kit's run pieces.
/// </summary>
public sealed class ChuteTeeTests
{
    private const string ModChutes = """
        lattice-catalogue 3
        piece ModChuteLine kit=ItemModChute option=0 grid=small family=chute
          step material=ItemModChute*1 exit=ItemWrench air=no
          end 0 0 0 x+ chute
          end 0 0 0 x- chute
          cell 0 0 0 chute
          collision 23

        piece ModChuteMerge kit=ItemModChute option=1 grid=small family=chute
          step material=ItemModChute*2 exit=ItemWrench air=no
          end 0 0 0 x+ chute role=input
          end 0 0 0 y+ chute role=input2
          end 0 0 0 x- chute role=output
          cell 0 0 0 chute
          collision 23

        piece ModChuteSorter kit=ItemModChute option=2 grid=small family=device
          step material=ItemModChute*2 exit=ItemWrench air=no
          end 0 0 0 x- chute role=input
          end 0 0 0 x+ chute role=output
          end 0 0 0 z+ chute role=output2
          end 0 0 0 z- chute role=waste
          cell 0 0 0 chute
          collision 23
        """;

    private const string Junction = "StructureChuteJunction";

    /// <summary>Every T: an opposite pair and one way square to it.</summary>
    private static IEnumerable<Directions> Tees()
    {
        for (var axis = Axis.X; axis <= Axis.Z; axis++)
            foreach (var side in Each(Ways("x+,x-,y+,y-,z+,z-")))
                if (side.AxisOf() != axis)
                    yield return Directions.Along(axis).With(side);
    }

    /// <summary>Every corner: two ways square to each other.</summary>
    private static IEnumerable<Directions> Corners() =>
        from a in Each(Ways("x+,x-,y+,y-,z+,z-"))
        from b in Each(Ways("x+,x-,y+,y-,z+,z-"))
        where a < b && a.AxisOf() != b.AxisOf()
        select Directions.Of(a, b);

    private static StructureRef[] RealLine(RunBench bench, Cell from, Direction along, int cells) =>
        [.. Enumerable.Range(0, cells).Select(i => bench.Real("StructureChuteStraight", Step(from, along, i), bench.Sending(Direction.XPlus, along)))];

    [Fact]
    public void A_chute_family_piece_with_flow_roles_is_the_kits_tee()
    {
        var bench = new RunBench(ModChutes);
        var kit = bench.Shapes(bench.KitOf("ModChuteLine"));

        Assert.True(kit.For(Ways("x+,x-,z+")).TryGet(out var tee));
        Assert.Equal("ModChuteMerge", bench.NameOf(tee.Piece));
        Assert.Equal(Ways("x+,x-,z+"), tee.Ways);
        Assert.True(kit.For(Ways("x+,x-,z+,z-")).IsNone);
    }

    [Fact]
    public void The_same_merge_as_a_device_gives_the_kit_no_tee()
    {
        var bench = new RunBench(ModChutes.Replace("option=1 grid=small family=chute", "option=1 grid=small family=device"));
        var kit = bench.Shapes(bench.KitOf("ModChuteLine"));

        Assert.True(kit.For(Ways("x+,x-,z+")).IsNone);
        Assert.Equal(ReasonCodes.NoPiece, PieceChooser.NoPiece(kit, Origin, Ways("x+,x-,z+")).Code);
    }

    [GameCatalogueFact]
    public void The_games_chute_kit_gives_every_tee_as_the_junction_and_every_corner_as_the_corner_in_turns_its_builder_allows()
    {
        var bench = new RunBench(GameCatalogue.Load());
        var kit = bench.Shapes(bench.KitOf("StructureChuteStraight"));

        foreach (var (shapes, piece) in new[] { (Tees(), Junction), (Corners(), "StructureChuteCorner") })
            foreach (var shape in shapes)
            {
                Assert.True(kit.For(shape).TryGet(out var found), shape.ToString());
                Assert.Equal(piece, bench.NameOf(found.Piece));
                Assert.Equal(shape, found.Ways);
                Assert.True(bench.Catalogue.Named(piece).AllowedTurns.Contains(found.Turn), shape.ToString());
            }

        Assert.True(kit.For(Ways("x+,x-,z+,z-")).IsNone);
    }

    [GameCatalogueFact]
    public void A_chute_run_leaving_a_built_line_mid_way_swaps_that_piece_for_the_junction_on_every_axis()
    {
        var catalogue = GameCatalogue.Load();
        foreach (var (along, away) in new[] { (Direction.XPlus, Direction.ZPlus), (Direction.ZPlus, Direction.YPlus), (Direction.YPlus, Direction.XMinus) })
        {
            var bench = new RunBench(catalogue);
            var line = RealLine(bench, Origin, along, 3);
            var middle = Step(Origin, along);

            var plan = bench.Plan(bench.Run(bench.KitOf("StructureChuteStraight"), middle, Step(middle, away, 3)));

            Assert.False(plan.FirstFailure.IsSome);
            var swap = Assert.Single(plan.Changes.OfType<Intent.Swap>());
            Assert.Equal(line[1], swap.Old);
            Assert.Equal(Junction, bench.NameOf(swap.New.Piece));
            Assert.Equal(Directions.Along(along.AxisOf()).With(away), bench.WaysOf(swap.New, middle));
            Assert.True(bench.Catalogue.Named(Junction).AllowedTurns.Contains(swap.New.Turn));
        }
    }

    [GameCatalogueFact]
    public void A_chute_route_with_a_branch_joins_it_with_the_junction()
    {
        var bench = new RouteBench(new RunBench(GameCatalogue.Load()));
        var kit = bench.Runs.KitOf("StructureChuteStraight");
        var request = bench.Route(kit, At(0, 2, 2), At(12, 2, 2))
            with { Branches = ValueList.From(new[] { new RouteBranch(At(6, 2, 6), Option<PortEnd>.None) }) };

        var plan = bench.Plan(request);
        TestBoard.Applied(bench.Runs.Boards.Apply(plan.ToCommand()));

        var join = Assert.Single(plan.Run.Pieces, p => bench.Runs.CellsOf(p).Contains(At(6, 2, 2)));
        Assert.Equal(Junction, bench.Runs.NameOf(join.Piece));
        Assert.Equal(Ways("x+,x-,z+"), bench.Runs.WaysOf(join, At(6, 2, 2)));
        Assert.True(bench.Catalogue.Named(Junction).AllowedTurns.Contains(join.Turn));
        Assert.Equal(13 + 4, plan.Run.Pieces.Sum(p => bench.Runs.CellsOf(p).Count));
    }
}
