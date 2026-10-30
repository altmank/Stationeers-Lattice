using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.Routes;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Xunit;
using static Lattice.Tests.Routes.RouteBench;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Routes;

/// <summary>
/// A route judges a planned device's cells as it judges a built one's: every cell the device fills, not only its own
/// cell. Test values (not game claims): the stacker's kit, option and power end; its two device cells, its chute ports
/// on the upper cell (output x+, input x-) and collision class are the game's (the catalogue reader's export). The
/// chute corner's option is a test value; its ends and class are the game's.
/// </summary>
public sealed class PlannedDeviceRouteTests
{
    private const string Pieces = """
        piece StructureStacker kit=ItemKitStacker option=0 grid=small
          step material=ItemKitStacker*1 exit=ItemDrill air=no
          end 0 0 0 z+ power,data
          end 0 5 0 x+ chute role=output
          end 0 5 0 x- chute role=input
          cell 0 0 0 device
          cell 0 5 0 device
          collision 23

        piece StructureChuteCorner kit=ItemKitChute option=2 grid=small
          step material=ItemKitChute*1 exit=ItemWrench air=no
          end 0 0 0 x+ chute
          end 0 0 0 y+ chute
          cell 0 0 0 chute
          collision 21
        """;

    // A at the origin sends right from its upper cell; B, eight cells left, takes in from its left. The straight
    // between A's output and B's input runs back through A's upper cell and through B's.
    private static readonly Cell AAt = At(0, 0, 0);
    private static readonly Cell BAt = At(-8, 0, 0);

    private static RouteBench Bench() => new(extraPieces: Pieces);

    private static KitKey Chute(RouteBench bench) => bench.Runs.KitOf("StructureChuteStraight");

    private static Placement Stacker(RouteBench bench, Cell at) => new(bench.Catalogue.Named("StructureStacker").Key, at, default);

    private static Port PortOf(RouteBench bench, Cell at, PortRole role) =>
        TestKitIds.Must(PiecePorts.Over(bench.Catalogue).At(Stacker(bench, at))).Single(port => port.Role == Option.Some(role));

    private static PortEnd Planned(RouteBench bench, Cell at, PortRole role) =>
        new PortEnd.OnGhost(bench.Runs.Boards.Placed(bench.Runs.Boards.Draft("StructureStacker", at)).Added[0].Id, PortOf(bench, at, role).Index);

    private static PortEnd Built(RouteBench bench, Cell at, PortRole role) =>
        new PortEnd.OnStructure(bench.Runs.Real("StructureStacker", at), PortOf(bench, at, role).Index);

    private static Cell[] CellsOfStacker(Cell at) => [at, Step(at, Direction.YPlus)];

    private static RoutePlan OutputToInput(RouteBench bench, PortEnd from, PortEnd to)
    {
        var request = bench.Route(Chute(bench), PortOf(bench, AAt, PortRole.Output).Joins, PortOf(bench, BAt, PortRole.Input).Joins) with
        {
            FromPort = Option.Some(from),
            ToPort = Option.Some(to),
        };
        return bench.Plan(request);
    }

    private static void KeepsOut(RouteBench bench, RoutePlan plan, params Cell[] device)
    {
        foreach (var cell in device)
        {
            Assert.DoesNotContain(cell, plan.Run.Cells);
            Assert.DoesNotContain(plan.Run.Pieces, piece => bench.Runs.CellsOf(piece).Contains(cell));
        }
    }

    private static void Lays(RouteBench bench, RoutePlan plan)
    {
        Assert.True(plan.Run.FirstFailure.IsNone);
        TestBoard.Applied(bench.Runs.Boards.Apply(plan.ToCommand()));
    }

    [Fact]
    public void A_chute_from_a_planned_stackers_output_to_another_planned_stackers_input_goes_around_both()
    {
        var bench = Bench();
        var from = Planned(bench, AAt, PortRole.Output);
        var to = Planned(bench, BAt, PortRole.Input);

        var plan = OutputToInput(bench, from, to);

        KeepsOut(bench, plan, [.. CellsOfStacker(AAt), .. CellsOfStacker(BAt)]);
        Assert.Equal(2, plan.Run.PortEnds.Count);
        Assert.All(plan.Run.PortEnds, join => Assert.True(join.JoinedBy.IsSome));
        Lays(bench, plan);
    }

    [Fact]
    public void A_chute_between_built_stackers_goes_around_both()
    {
        var bench = Bench();
        var from = Built(bench, AAt, PortRole.Output);
        var to = Built(bench, BAt, PortRole.Input);

        var plan = OutputToInput(bench, from, to);

        KeepsOut(bench, plan, [.. CellsOfStacker(AAt), .. CellsOfStacker(BAt)]);
        Assert.All(plan.Run.PortEnds, join => Assert.True(join.JoinedBy.IsSome));
        Lays(bench, plan);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_chute_between_a_built_and_a_planned_stacker_goes_around_both(bool builtFirst)
    {
        var bench = Bench();
        var from = builtFirst ? Built(bench, AAt, PortRole.Output) : Planned(bench, AAt, PortRole.Output);
        var to = builtFirst ? Planned(bench, BAt, PortRole.Input) : Built(bench, BAt, PortRole.Input);

        var plan = OutputToInput(bench, from, to);

        KeepsOut(bench, plan, [.. CellsOfStacker(AAt), .. CellsOfStacker(BAt)]);
        Assert.All(plan.Run.PortEnds, join => Assert.True(join.JoinedBy.IsSome));
        Lays(bench, plan);
    }

    [Theory]
    [InlineData("StructureChuteStraight")]
    [InlineData("StructureCableStraight")]
    [InlineData("StructurePipeStraight")]
    public void A_route_goes_around_a_planned_stacker_whose_upper_cell_is_on_the_straight_line(string piece)
    {
        var bench = Bench();
        var stacker = At(0, 0, 4);
        bench.Runs.Ghost("StructureStacker", stacker);

        var plan = bench.Plan(bench.Route(bench.Runs.KitOf(piece), At(-6, 1, 4), At(6, 1, 4)));

        Assert.NotEqual(0, plan.Bends);
        KeepsOut(bench, plan, CellsOfStacker(stacker));
        Lays(bench, plan);
    }

    [Fact]
    public void With_ports_free_and_no_joins_a_route_past_a_planned_stacker_takes_the_built_stackers_path()
    {
        var planned = Bench();
        planned.Runs.Ghost("StructureStacker", At(0, 0, 4));
        var built = Bench();
        built.Runs.Real("StructureStacker", At(0, 0, 4));
        var toggles = new RouteToggles(false, false, false, true, true);

        var fromPlanned = planned.Plan(planned.Route(planned.Runs.Cable, At(-6, 1, 4), At(6, 1, 4), toggles));
        var fromBuilt = built.Plan(built.Route(built.Runs.Cable, At(-6, 1, 4), At(6, 1, 4), toggles));

        Assert.Equal(fromBuilt.Run.Cells, fromPlanned.Run.Cells);
    }
}
