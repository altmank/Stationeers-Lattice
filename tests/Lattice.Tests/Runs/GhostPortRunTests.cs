using System;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Tests.Catalogue;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Runs;

/// <summary>
/// A run whose end cell is a port's <see cref="Port.Joins"/> joins that port, for a device planned as for the same
/// device built: the end piece turns to leave toward the port, so a run arriving square to the port lays a corner.
/// </summary>
public sealed class GhostPortRunTests
{
    private const string Pump = "StructureVolumePump";

    [GameCatalogueFact]
    public void A_run_ending_at_each_port_of_a_volume_pump_ghost_joins_it_at_every_turn_as_it_joins_the_pump_built()
    {
        var pump = PumpText();
        for (var index = 0; index < Turn.Count; index++)
        {
            var turn = Prefabs.Ok(Turn.Create(index));
            var planned = new RunBench(pump);
            var built = new RunBench(pump);
            var at = At(0, 0, 0);
            planned.Ghost(Pump, at, turn);
            built.Real(Pump, at, turn);
            var ports = Prefabs.Ok(View(planned.Catalogue).PortsAt(new Placement(planned.Catalogue.Named(Pump).Key, at, turn)));

            foreach (var port in ports)
            {
                var kit = port.Kinds.Contains(PortKinds.Power) ? planned.Cable : planned.KitOf("StructurePipeStraight");
                var toward = DirectionMath.DirectionOf(port.Toward)!.Value;
                var side = Square(toward);
                var request = planned.Run(kit, Step(port.Joins, side, 3), port.Joins);

                var ghostRun = planned.Plan(request);
                var builtRun = built.Plan(request);

                var end = ghostRun.Pieces.Single(p => planned.CellsOf(p).Contains(port.Joins));
                var ways = planned.WaysOf(end, port.Joins);
                Assert.True(ways.Contains(toward.Opposite()) && ways.Contains(side),
                    $"turn {index} port {port.Index}: the end piece at {port.Joins} leaves {ways}, not back toward the pump and toward the run");
                Assert.Equal(builtRun.Pieces, ghostRun.Pieces);
            }
        }
    }

    [GameCatalogueFact]
    public void A_run_to_each_port_of_a_volume_pump_ghost_named_as_its_end_joins_it_at_every_turn_as_the_pump_built()
    {
        var pump = PumpText();
        for (var index = 0; index < Turn.Count; index++)
        {
            var turn = Prefabs.Ok(Turn.Create(index));
            var planned = new RunBench(pump);
            var built = new RunBench(pump);
            var at = At(0, 0, 0);
            var ghost = planned.Boards.Placed(planned.Boards.Draft(Pump, at, turn)).Added[0].Id;
            built.Real(Pump, at, turn);
            var ports = Prefabs.Ok(View(planned.Catalogue).PortsAt(new Placement(planned.Catalogue.Named(Pump).Key, at, turn)));

            foreach (var port in ports)
            {
                var kit = port.Kinds.Contains(PortKinds.Power) ? planned.Cable : planned.KitOf("StructurePipeStraight");
                var side = Square(DirectionMath.DirectionOf(port.Toward)!.Value);
                var plain = planned.Run(kit, Step(port.Joins, side, 3), port.Joins);

                var named = planned.Plan(plain with { ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(ghost, port.Index)) });
                var builtRun = built.Plan(plain);

                var join = Assert.Single(named.PortEnds);
                Assert.True(join.JoinedBy.IsSome, $"turn {index} port {port.Index}: no piece joins it");
                Assert.Equal(builtRun.Pieces, named.Pieces);
            }
        }
    }

    private static Direction Square(Direction toward) =>
        toward.AxisOf() == Axis.Y ? Direction.XPlus : Direction.YPlus;

    private static PieceCatalogue View(Lattice.TestKit.Catalogue.FakeCatalogue catalogue) =>
        new(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue));

    /// <summary>The pump's entry from the catalogue the game wrote, as a file of its own.</summary>
    private static string PumpText()
    {
        var lines = File.ReadAllLines(GameCatalogue.Path().Reduce(() => throw new InvalidOperationException("no game catalogue")));
        var start = Array.FindIndex(lines, l => l.StartsWith($"piece {Pump} ", StringComparison.Ordinal));
        Assert.True(start >= 0, $"{Pump} is not in the game's catalogue");
        var block = lines.Skip(start).TakeWhile((l, i) => i == 0 || l.StartsWith("  ", StringComparison.Ordinal));
        return CatalogueDataFile.Header + "\n" + string.Join("\n", block) + "\n";
    }
}
