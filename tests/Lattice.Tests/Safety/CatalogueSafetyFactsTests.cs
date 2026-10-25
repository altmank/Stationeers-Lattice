using System.IO;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Safety;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.World;
using static Lattice.Tests.Safety.SafetyBench;

namespace Lattice.Tests.Safety;

/// <summary>Build safety facts read from catalogue entries and the world view; every piece is modded.</summary>
public sealed class CatalogueSafetyFactsTests
{
    private const string Text = """
        lattice-catalogue 3
        piece ModHeater kit=ItemModHeaterKit option=0 grid=small keeps=heat,air control=y+
          step material=ItemModHeaterKit*1 exit=ItemWrench air=no
          cell 0 0 0 device

        piece ModDynamo kit=ItemModDynamoKit option=0 grid=small supplies=yes
          step material=ItemModDynamoKit*1 exit=ItemWrench air=no
          cell 0 0 0 device

        piece ModGrille kit=ItemModGrilleKit option=0 grid=small pipe=gas vents=yes
          step material=ItemModGrilleKit*1 exit=ItemWrench air=no
          end 0 0 0 x- gas
          cell 0 0 0 pipe

        piece ModPlain kit=ItemModPlainKit option=0 grid=small
          step material=ItemModPlainKit*1 exit=ItemWrench air=no
          cell 0 0 0 device
        """;

    private static FakeCatalogue Catalogue() =>
        TestKitIds.MustRead(CatalogueFile.Read(new StringReader(Text), "safety-facts.lcat"));

    [Fact]
    public void Piece_facts_come_from_the_catalogue_entry()
    {
        var catalogue = Catalogue();
        var facts = new CatalogueSafetyFacts(catalogue, new FakeWorld(catalogue));
        PrefabKey Key(string name) => catalogue.Named(name).Key;

        Assert.Equal(Keeps.Heat | Keeps.Air, facts.Keeps(Key("ModHeater")));
        Assert.Equal(Option.Some(Direction.YPlus), facts.ControlFace(Key("ModHeater")));
        Assert.True(facts.Supplies(Key("ModDynamo")));
        Assert.True(facts.Vents(Key("ModGrille")));
        Assert.Equal(Keeps.None, facts.Keeps(Key("ModPlain")));
        Assert.False(facts.Supplies(Key("ModPlain")));
        Assert.False(facts.Vents(Key("ModPlain")));
        Assert.True(facts.ControlFace(Key("ModPlain")).IsNone);
    }

    [Fact]
    public void A_piece_the_catalogue_does_not_hold_has_no_facts()
    {
        var catalogue = Catalogue();
        var facts = new CatalogueSafetyFacts(catalogue, new FakeWorld(catalogue));
        var unknown = new PrefabKey(-424242);

        Assert.Equal(Keeps.None, facts.Keeps(unknown));
        Assert.False(facts.Supplies(unknown));
        Assert.False(facts.Vents(unknown));
        Assert.True(facts.ControlFace(unknown).IsNone);
    }

    [Fact]
    public void Room_temperature_and_terrain_come_from_the_world_view()
    {
        var catalogue = Catalogue();
        var world = new FakeWorld(catalogue);
        var room = new RoomRef(7);
        world.SetRoom(new RoomInfo(room, ValueList.Of(Large(0, 0, 0)), true, Option<ValueList<GasHeld>>.None)
        {
            Temperature = Option.Some(new Kelvin(350)),
        });
        world.SetTerrain(Large(1, 0, 0));
        var facts = new CatalogueSafetyFacts(catalogue, world);

        Assert.Equal(Option.Some(new Kelvin(350)), facts.Temperature(room));
        Assert.True(facts.Temperature(new RoomRef(8)).IsNone);
        Assert.True(facts.Temperature(new NetworkRef(9)).IsNone);
        Assert.True(facts.Terrain(Large(1, 0, 0)));
        Assert.False(facts.Terrain(Large(0, 0, 0)));
    }

    [Fact]
    public void Build_safety_over_the_catalogue_facts_warns_on_terrain()
    {
        var bench = new SafetyBench();
        bench.World.SetTerrain(Large(0, 0, 0));
        var truss = bench.Build("ModTruss", Large(0, 0, 0));

        var warnings = bench.Check(new CatalogueSafetyFacts(bench.Pieces, bench.World));

        var warning = Assert.Single(warnings, w => w.Why.Code == SafetyCodes.TerrainInTheWay);
        Assert.Equal(ValueList.Of(truss), warning.Ghosts);
    }
}
