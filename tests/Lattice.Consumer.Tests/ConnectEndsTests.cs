using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A connect tool's requests as a consumer writes them against the contract alone: a run to a built device's port, and
/// a route pinned through cells.
/// </summary>
public sealed class ConnectEndsTests
{
    private static readonly Maker Maker = Make.Maker("connect", "routes");

    /// <summary>What a consumer shows for a port end.</summary>
    private static string Describe(PortEnd end) => end switch
    {
        PortEnd.OnGhost ghost => $"ghost {ghost.Ghost.Value} port {ghost.Port.Value}",
        PortEnd.OnStructure built => $"structure {built.Structure.Id.Value} port {built.Port.Value}",
        _ => "an end this version does not know",
    };

    [Fact]
    public void A_run_ends_on_a_built_devices_port()
    {
        var device = new StructureRef(new ReferenceId(42), new PrefabKey(7), new Cell(0, 0, 0));
        var port = PortIndex.Create(1).Match(static p => p, static _ => default);
        var run = new RunRequest(default, new Cell(50, 0, 0), ValueList<Cell>.Empty, new Cell(0, 0, 5), Option<PlanId>.None, Maker)
        {
            ToPort = Option.Some<PortEnd>(new PortEnd.OnStructure(device, port)),
        };

        Assert.Equal("structure 42 port 1", Describe(run.ToPort.Reduce(() => new PortEnd.OnGhost(default, default))));
    }

    [Fact]
    public void A_route_is_pinned_through_cells_in_order()
    {
        var route = new RouteRequest(default, new Cell(0, 0, 0), new Cell(100, 0, 0), new RouteToggles(false, false, false, true, true), Option<PlanId>.None, Maker)
        {
            Via = ValueList.From(new[] { new Cell(30, 0, 20), new Cell(70, 0, 20) }),
        };

        Assert.Equal(new Cell(30, 0, 20), route.Via[0]);
        Assert.Equal(2, route.Via.Count);
        Assert.Empty(new RouteRequest(default, default, default, route.Toggles, Option<PlanId>.None, Maker).Via);
    }
}
