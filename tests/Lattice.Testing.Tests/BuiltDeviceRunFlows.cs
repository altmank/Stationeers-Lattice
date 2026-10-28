using System.Linq;
using Lattice.Api;
using Lattice.Testing;
using static Lattice.Testing.Tests.Hosted;

namespace Lattice.Testing.Tests;

/// <summary>
/// A built device's cells are taken for a run as a ghost's are: by the game's small-cell clash rule, collision bits
/// first (a cable and a device share the cables bit in a real device's 23), then ends. A drawn run through one fails at
/// the first such cell, naming the built piece; a route goes round; a run may still end on the device's port.
/// </summary>
public sealed class BuiltDeviceRunFlows
{
    private static readonly KitKey CableKit = TestCatalogue.Hand.Kit("ItemCableCoil").Reduce(default(KitKey));
    private static readonly KitKey PipeKit = TestCatalogue.Hand.Kit("ItemKitPipe").Reduce(default(KitKey));
    private static readonly RouteToggles Off = new(false, false, false, false, false);

    /// <summary>A small device as the game's are: collision 23 (pipes, cables, devices, rails), a power and data end facing +X.</summary>
    private static TestPiece Battery(int collision = 23) => Must(TestPiece.Create("ModBattery", "ItemModBattery", PieceGrid.Small))
        .WithCell(new CellOffset(0, 0, 0), TestSlot.Device)
        .WithCollision(collision)
        .WithEnd(new CellOffset(0, 0, 0), SignedAxis.XPlus, ValueSet.From(new[] { TestNetwork.Power, TestNetwork.Data }))
        .InFamily(PieceFamilies.Device);

    /// <summary>A station battery's shape: eight small device cells, two on each axis.</summary>
    private static TestPiece Station()
    {
        var piece = Must(TestPiece.Create("ModStationBattery", "ItemModStationBattery", PieceGrid.Small)).WithCollision(23).InFamily(PieceFamilies.Device);
        foreach (var x in new[] { 0, 5 })
            foreach (var y in new[] { 0, 5 })
                foreach (var z in new[] { 0, 5 })
                    piece = piece.WithCell(new CellOffset(x, y, z), TestSlot.Device);
        return piece;
    }

    private static TestWorld WithBuilt(TestPiece piece, Cell at) =>
        Start(TestScenario.Empty.WithPiece(piece).WithStructure(piece.Key, at, default));

    private static Preview Previewed(TestWorld world, Command command) =>
        Ready(world, () => world.System.Preview.Of(command, world.Viewer))
            .Match(static p => p, static why => throw new Xunit.Sdk.XunitException(Describe(why[0].Why)));

    private static RunReport Report(Preview preview) => preview.Run.Reduce(() => throw new Xunit.Sdk.XunitException("no run report"));

    private static Command.PlanRun Run(KitKey kit, Cell from, Cell to) =>
        new(new RunRequest(kit, from, ValueList<Cell>.Empty, to, Option<PlanId>.None, Made));

    private static CellFailure Failure(RunReport report) =>
        report.FirstFailure.Reduce(() => throw new Xunit.Sdk.XunitException("no failure"));

    [Fact]
    public void A_run_through_a_built_battery_fails_at_its_cell_naming_the_battery()
    {
        var battery = Battery();
        using var world = WithBuilt(battery, At(0));

        var report = Report(Previewed(world, Run(CableKit, At(0, 0, -2), At(0, 0, 2))));

        var failure = Failure(report);
        Assert.Equal(At(0), failure.At);
        Assert.Equal(ReasonCodes.CannotShare, failure.Why.Code);
        Assert.Equal(battery.Key.Hash.ToString(), failure.Why.Facts["with"]);
        Assert.True(failure.Why.Facts.ContainsKey("structure"));
        Assert.Equal(5, report.Cells.Count);
    }

    [Fact]
    public void A_route_between_the_same_ends_goes_round_the_battery()
    {
        using var world = WithBuilt(Battery(), At(0));

        var report = Report(Previewed(world, new Command.PlanRoute(new RouteRequest(CableKit, At(0, 0, -2), At(0, 0, 2), Off, Option<PlanId>.None, Made))));

        Assert.DoesNotContain(At(0), report.Cells);
        Assert.True(report.FirstFailure.IsNone);
    }

    [Fact]
    public void A_run_ending_on_the_batterys_port_fits_and_joins_it()
    {
        var battery = Battery();
        using var world = Start(TestScenario.Empty.WithPiece(battery));
        var built = Must(world.Build(battery.Key, At(0), default));
        Must(world.Settle());
        var port = Must(world.System.Catalogue.PortsAt(new Placement(battery.Key, At(0), default))).Single();
        var run = new Command.PlanRun(new RunRequest(CableKit, At(4), ValueList<Cell>.Empty, port.Joins, Option<PlanId>.None, Made)
        {
            ToPort = Option.Some<PortEnd>(new PortEnd.OnStructure(built, port.Index)),
        });

        var report = Report(Previewed(world, run));

        Assert.True(report.FirstFailure.IsNone);
        Assert.DoesNotContain(At(0), report.Cells);
        Assert.True(Assert.Single(report.PortEnds).JoinedBy.IsSome);
    }

    [Fact]
    public void A_pipe_run_through_the_battery_fails_there_too()
    {
        using var world = WithBuilt(Battery(), At(0));

        var failure = Failure(Report(Previewed(world, Run(PipeKit, At(0, 0, -2), At(0, 0, 2)))));

        Assert.Equal(At(0), failure.At);
        Assert.Equal(ReasonCodes.CannotShare, failure.Why.Code);
    }

    [Fact]
    public void A_device_whose_collision_shares_no_bit_with_cable_lets_the_run_through_as_the_game_does()
    {
        using var world = WithBuilt(Battery(collision: 4), At(0));

        var report = Report(Previewed(world, Run(CableKit, At(0, 0, -2), At(0, 0, 2))));

        Assert.True(report.FirstFailure.IsNone);
    }

    [Fact]
    public void A_built_multi_cell_device_takes_every_one_of_its_cells()
    {
        var station = Station();
        using var world = WithBuilt(station, At(0));
        var hit = new System.Collections.Generic.HashSet<Cell>();

        foreach (var y in new[] { 0, 1 })
            foreach (var z in new[] { 0, 1 })
                foreach (var (from, to) in new[] { (At(-2, y, z), At(3, y, z)), (At(3, y, z), At(-2, y, z)) })
                {
                    var failure = Failure(Report(Previewed(world, Run(CableKit, from, to))));
                    Assert.Equal(station.Key.Hash.ToString(), failure.Why.Facts["with"]);
                    hit.Add(failure.At);
                }

        Assert.Equal(8, hit.Count);
        Assert.All(hit, cell => Assert.InRange(cell.X, At(0).X, At(1).X));
    }
}
