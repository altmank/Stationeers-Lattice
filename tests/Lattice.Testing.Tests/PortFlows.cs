using System.Linq;
using Lattice.Api;
using static Lattice.Testing.Tests.Hosted;

namespace Lattice.Testing.Tests;

/// <summary>
/// An agent's flow over ports: two device ghosts, their ports read from the catalogue at the ghosts' placements, and
/// a route planned between the cells that join them, previewed then placed, with nothing built.
/// </summary>
public sealed class PortFlows
{
    private static readonly KitKey Coil = TestCatalogue.Hand.Kit("ItemCableCoil").Reduce(default(KitKey));

    /// <summary>A small modded device in its cell's device slot, with one power input facing +X and one data output facing +Y.</summary>
    private static readonly TestPiece Charger = Must(TestPiece.Create("ModCharger", "ItemModCharger", PieceGrid.Small))
        .WithPort(new CellOffset(0, 0, 0), SignedAxis.XPlus, ValueSet.Of(TestNetwork.Power), PortRole.Input)
        .WithPort(new CellOffset(0, 0, 0), SignedAxis.YPlus, ValueSet.Of(TestNetwork.Data), PortRole.Output)
        .WithCell(new CellOffset(0, 0, 0), TestSlot.Device)
        .WithCollision(4);

    private static readonly Turn HalfYaw = Enumerable.Range(0, Turn.Count)
        .Select(static i => Must(Turn.Create(i)))
        .Single(static t => t.Axes.X == SignedAxis.XMinus && t.Axes.Y == SignedAxis.YPlus);

    [Fact]
    public void A_test_pieces_ports_carry_their_roles_in_order()
    {
        using var world = Start(TestScenario.Empty.WithPiece(Charger));

        var ports = Must(world.System.Catalogue.PortsAt(new Placement(Charger.Key, At(0), default)));

        Assert.Equal(new[] { 0, 1 }, ports.Select(static p => p.Index.Value));
        Assert.Equal(new[] { Option.Some(PortRole.Input), Option.Some(PortRole.Output) }, ports.Select(static p => p.Role));
        Assert.Equal(new[] { At(1), At(0, 1) }, ports.Select(static p => p.Joins));
        Assert.Equal(new[] { "Power", "Data" }, ports.Select(static p => p.GameName));
    }

    [Fact]
    public void A_route_between_two_device_ghosts_ports_previews_and_places_with_nothing_built()
    {
        using var world = Start(TestScenario.Empty.WithPiece(Charger));
        var lattice = world.System;
        var placed = Assert.IsType<Outcome.Applied>(Submit(world, new Command.Place(ValueList.From(new[]
        {
            Draft(Charger.Key, At(0)),
            Draft(Charger.Key, At(8), HalfYaw),
        })))).Changes.Added;
        var (a, b) = (Power(lattice, placed[0]), Power(lattice, placed[1]));
        var route = new Command.PlanRoute(new RouteRequest(Coil, a.Joins, b.Joins, new RouteToggles(false, false, false, false, false), Option<PlanId>.None, Made));

        var preview = Ready(world, () => lattice.Preview.Of(route, world.Viewer))
            .Match(static p => p, static why => throw new Xunit.Sdk.XunitException(Describe(why[0].Why)));
        var run = preview.Run.Reduce(() => throw new Xunit.Sdk.XunitException("no run in the preview"));
        var laid = Assert.IsType<Outcome.Applied>(Submit(world, route)).Changes.Added;

        Assert.Equal(a.Joins, run.Cells[0]);
        Assert.Equal(b.Joins, run.Cells[run.Cells.Count - 1]);
        Assert.True(run.FirstFailure.IsNone);
        Assert.Equal(preview.WouldChange.Added.Count, laid.Count);
        Assert.Empty(lattice.World.At(a.Joins));
    }

    private static Port Power(IGhostSystem lattice, Ghost ghost)
    {
        var target = Assert.IsType<Intent.Build>(ghost.Intent).Target;
        return Must(lattice.Catalogue.PortsAt(target)).Single(static p => p.Kinds.Contains(PortKinds.Power));
    }
}
