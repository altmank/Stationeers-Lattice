using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using FaceLoad = Lattice.Core.Consequences.FaceLoad;
using Lattice.Core.Consequences;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.Scenarios;
using Lattice.TestKit.World;

namespace Lattice.Tests.Consequences;

/// <summary>
/// The consequence models built from the world view and the catalogue (the test kit's fakes). Modded pieces, unknown
/// to the base game, are modelled from their catalogue facts like any piece of their kind.
/// </summary>
public sealed class WorldModelsTests
{
    private const string ModdedText = """
        lattice-catalogue 3
        piece ModSuperCableStraight kit=ItemModSuperCoil option=0 grid=small
          step material=ItemModSuperCoil*1 exit=ItemWireCutters air=no
          end 0 0 0 z+ power,data
          end 0 0 0 z- power,data
          rating cable=250000

        piece ModReinforcedWall kit=ItemModWallKit option=0 grid=large
          step entry=ItemModWallKit material=ItemModWallKit*1 exit=ItemWrench air=no
          step entry=ItemModPlate material=ItemModPlate*3 exit=ItemCrowbar air=yes
          rating face=900

        piece ModHeavyPipeStraight kit=ItemModPipeKit option=0 grid=small pipe=gas
          step material=ItemModPipeKit*1 exit=ItemWrench air=no
          end 0 0 0 z+ gas reach=370
          end 0 0 0 z- gas reach=340
          rating pipe=120000
        """;

    private static FakeCatalogue WithModded()
    {
        var modded = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(ModdedText), "modded.lcat"));
        return new FakeCatalogue([.. FakeCatalogue.Hand().Pieces, .. modded.Pieces]);
    }

    private static Cell Small(int z) => new(5, 5, z * 5);

    private static (WorldModels Models, FakeWorld World, List<StructureRef> Built) Run(FakeCatalogue catalogue, params string[] pieces)
    {
        var scenario = Scenario.With(catalogue);
        var built = new List<StructureRef>();
        for (var index = 0; index < pieces.Length; index++)
        {
            scenario.Built(pieces[index], Small(index), out var structure);
            built.Add(structure);
        }

        var world = scenario.Build().World;
        return (new WorldModels(world, catalogue, new CubeGrid()), world, built);
    }

    private static List<ReferenceId> Ids(List<StructureRef> built) => built.ConvertAll(static s => s.Id);

    [Fact]
    public void A_built_cable_run_links_each_neighbour_both_ways()
    {
        var (models, _, built) = Run(FakeCatalogue.Hand(), "StructureCableStraight", "StructureCableStraight", "StructureCableStraight");
        var pieces = models.Pieces(Ids(built));

        var links = Connectivity.LinksTouching(pieces, [built[1].Id]);

        Assert.Equal(3, pieces.Count);
        Assert.Equal(4, links.Count);
        Assert.Contains(new Link(built[0].Id, built[1].Id), links);
        Assert.Contains(new Link(built[2].Id, built[1].Id), links);
    }

    [Fact]
    public void Links_agree_with_the_worlds_networks()
    {
        var (models, world, built) = Run(FakeCatalogue.Hand(), "StructureCableStraight", "StructureCableJunction4", "StructureCableStraight");
        var pieces = models.Pieces(Ids(built));

        foreach (var a in pieces)
            foreach (var b in pieces)
                if (Connectivity.Links(a, b))
                    Assert.Equal(world.NetworkOf(a.Id), world.NetworkOf(b.Id));
    }

    [Fact]
    public void A_modded_cable_joins_a_base_cable_run_like_a_cable()
    {
        var catalogue = WithModded();
        var (models, world, built) = Run(catalogue, "StructureCableStraight", "ModSuperCableStraight", "StructureCableStraight");
        var pieces = models.Pieces(Ids(built));

        var links = Connectivity.LinksTouching(pieces, [built[1].Id]);

        Assert.Equal(4, links.Count);
        Assert.Contains(new Link(built[1].Id, built[0].Id), links);
        Assert.Contains(new Link(built[1].Id, built[2].Id), links);
        Assert.Equal(world.NetworkOf(built[0].Id), world.NetworkOf(built[2].Id));
    }

    [Fact]
    public void A_modded_cables_rating_bounds_the_power_forecast()
    {
        var catalogue = WithModded();
        var modded = catalogue.Named("ModSuperCableStraight");
        var normal = catalogue.Named("StructureCableStraight");
        var after = new ForecastNetwork(0);
        after.NetworksBefore.Add(new NetworkRef(1));
        after.NewPieces.Add(new ReferenceId(-1));
        var before = new Dictionary<NetworkRef, NetworkPower> { [new NetworkRef(1)] = new(200000.0, 200000.0, Option<double>.None, Option<double>.None) };

        var onModded = PowerAfter.Of(after, before, WorldModels.CableRatings([(new ReferenceId(-1), modded)]));
        var onNormal = PowerAfter.Of(after, before, WorldModels.CableRatings([(new ReferenceId(-1), normal)]));

        Assert.False(onModded.Overloads);
        Assert.True(onNormal.Overloads);
    }

    [Fact]
    public void A_modded_wall_blocks_air_and_bears_stress_from_its_catalogue_facts()
    {
        var catalogue = WithModded();
        var wall = catalogue.Named("ModReinforcedWall");
        var scenario = Scenario.With(catalogue)
            .Built("ModReinforcedWall", new Cell(10, 10, 10), out var finished)
            .Built("ModReinforcedWall", new Cell(30, 10, 10), out var unfinished, step: 0);
        var world = scenario.Build().World;
        var load = new FaceLoad(500.0, 0.0, false);

        Assert.True(world.Structure(finished.Id).TryGet(out var done));
        Assert.True(world.Structure(unfinished.Id).TryGet(out var started));
        Assert.True(WorldModels.AirBlockerOf(done, wall).BlocksAir);
        Assert.False(WorldModels.AirBlockerOf(started, wall).BlocksAir);
        Assert.Equal(StressVerdict.Ok, WallStress.Judge(load, WorldModels.FaceRatingKpa(wall), 0.8));
        Assert.Equal(StressVerdict.Overstressed, WallStress.Judge(load, WorldModels.FaceRatingKpa(catalogue.Named("StructureWallIron")), 0.8));
    }

    [Fact]
    public void A_structure_the_world_does_not_know_is_refused_with_a_reason()
    {
        var (models, _, _) = Run(FakeCatalogue.Hand(), "StructureCableStraight");

        Assert.True(models.Piece(new ReferenceId(999)).TryGetError(out var reason));
        Assert.Equal(ReasonCodes.NotFound, reason.Code);
    }

    private static T Some<T>(Option<T> option) where T : notnull
    {
        Assert.True(option.TryGet(out var value));
        return value;
    }

    private static GasKind Gas(string name) => TestKitIds.Must(GasKind.Create(name));

    private static ValueList<GasHeld> Held(params (string Gas, double Mol, double J)[] gases)
    {
        var held = new List<GasHeld>(gases.Length);
        foreach (var (gas, mol, j) in gases)
            held.Add(new GasHeld(Gas(gas), new Moles(mol), new Joules(j)));
        return ValueList.From(held);
    }

    [Fact]
    public void A_pipe_networks_gas_reading_carries_its_volume_contents_cells_and_weakest_rating()
    {
        var (models, world, built) = Run(WithModded(), "ModHeavyPipeStraight", "StructurePipeStraight");
        var network = Some(world.NetworkOf(built[0].Id));
        world.SetNetworkGas(network, new Litres(20), Held(("oxygen", 30, 600_000), ("nitrogen", 70, 1_400_000)));
        var info = Some(world.Network(network));
        var order = GasOrder.Of([info.Contents.Reduce(ValueList<GasHeld>.Empty)]);

        Assert.True(models.Gas(info, order).TryGet(out var gas));
        Assert.Equal(20.0, gas.VolumeL);
        Assert.Equal(100.0, gas.Gas.TotalMol);
        Assert.Equal(600_000.0, gas.Gas.EnergyOf(order.IndexOf(Gas("oxygen")).Reduce(-1)));
        Assert.Equal(Ids(built), [.. gas.Members]);
        Assert.Equal([Small(0), Small(1)], [.. gas.Cells]);
        Assert.Equal(Option.Some(60_000.0), gas.RatingKpa);
    }

    [Fact]
    public void A_network_without_a_read_volume_or_contents_gives_no_gas_reading()
    {
        var (models, world, built) = Run(FakeCatalogue.Hand(), "StructurePipeStraight", "StructureCableStraight");
        var order = GasOrder.Of([]);
        foreach (var structure in built)
        {
            var info = Some(world.Network(Some(world.NetworkOf(structure.Id))));
            Assert.True(models.Gas(info, order).IsNone);
        }
    }

    [Fact]
    public void A_gas_order_lists_every_gas_once_by_name()
    {
        var order = GasOrder.Of([Held(("oxygen", 1, 1), ("co2", 1, 1)), Held(("oxygen", 2, 2), ("mod.helium", 1, 1))]);

        Assert.Equal(3, order.Count);
        Assert.Equal(Option.Some(0), order.IndexOf(Gas("co2")));
        Assert.Equal(Option.Some(1), order.IndexOf(Gas("mod.helium")));
        Assert.Equal(Option.Some(2), order.IndexOf(Gas("oxygen")));
        Assert.True(order.IndexOf(Gas("water")).IsNone);
    }

    [Fact]
    public void A_rooms_record_sums_its_air_over_its_cells()
    {
        var cells = ValueList.From([new Cell(0, 0, 0), new Cell(10, 0, 0)]);
        var room = new RoomInfo(new RoomRef(4), cells, true, Option.Some(Held(("oxygen", 40, 800_000), ("nitrogen", 60, 1_200_000))));

        Assert.True(WorldModels.Room(room).TryGet(out var record));
        Assert.Equal(new RoomRef(4), record.Ref);
        Assert.Equal(cells, record.Cells);
        Assert.Equal(100.0, record.TotalMol);
        Assert.Equal(2_000_000.0, record.EnergyJ);
        Assert.True(WorldModels.Room(room with { Air = Option<ValueList<GasHeld>>.None }).IsNone);
    }
}
