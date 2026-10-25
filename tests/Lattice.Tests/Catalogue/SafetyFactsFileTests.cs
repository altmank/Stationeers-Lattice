using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Openings;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.World;

namespace Lattice.Tests.Catalogue;

/// <summary>The build safety facts on pieces and in the world, the file words that carry them, and the catalogue's opening facts.</summary>
public sealed class SafetyFactsFileTests
{
    private static FakeCatalogue Read(string text) =>
        CatalogueFile.Read(new StringReader(text), "hand.lcat").Match(static c => c, static e => throw new System.InvalidOperationException(e.ToString()));

    private static string Line(string words) =>
        "lattice-catalogue 3\npiece ModHeater kit=ItemModHeaterKit option=0 grid=large " + words + "\n  step material=ItemModHeaterKit*1 air=no\n";

    [Fact]
    public void Safety_words_are_read_and_written_back_as_they_were()
    {
        var words = "keeps=air,heat supplies=yes control=" + CatalogueText.Direction(Direction.ZPlus) + " vents=yes";
        var read = Read(Line(words));
        var written = new StringWriter();
        CatalogueFile.Write(read, written);

        var facts = Read(written.ToString()).Named("ModHeater");

        Assert.Equal(Keeps.Air | Keeps.Heat, facts.Keeps);
        Assert.True(facts.Supplies);
        Assert.Equal(Option.Some(Direction.ZPlus), facts.ControlFace);
        Assert.True(facts.Vents);
    }

    [Fact]
    public void A_piece_line_without_safety_words_keeps_nothing_and_supplies_nothing()
    {
        var facts = Read(Line("")).Named("ModHeater");

        Assert.Equal(Keeps.None, facts.Keeps);
        Assert.False(facts.Supplies);
        Assert.True(facts.ControlFace.IsNone);
        Assert.False(facts.Vents);
    }

    [Theory]
    [InlineData("keeps=warmth")]
    [InlineData("supplies=no")]
    [InlineData("control=sideways")]
    [InlineData("vents=maybe")]
    public void A_bad_safety_word_is_a_file_error(string words)
    {
        Assert.True(CatalogueFile.Read(new StringReader(Line(words)), "bad.lcat").TryGetError(out _));
    }

    [Fact]
    public void Opening_facts_come_from_the_catalogue_and_an_unknown_piece_is_none()
    {
        var catalogue = Read(Line("opening=door"));
        var openings = new CatalogueOpeningFacts(catalogue);

        Assert.Equal(Option.Some(OpeningKind.Door), openings.Of(catalogue.Named("ModHeater").Key));
        Assert.True(openings.Of(PrefabHash.Piece("NotAPiece")).IsNone);
    }

    [Fact]
    public void World_temperatures_are_none_until_read_and_terrain_is_per_large_cell()
    {
        var room = new RoomInfo(new RoomRef(1), ValueList<Cell>.Empty, true, Option<ValueList<GasHeld>>.None);
        var world = new FakeWorld(Read(Line("")));
        world.SetTerrain(new Cell(10, 10, 10));

        Assert.True(room.Temperature.IsNone);
        Assert.Equal(Option.Some(new Kelvin(400)), (room with { Temperature = Option.Some(new Kelvin(400)) }).Temperature);
        Assert.True(world.Terrain(new Cell(10, 10, 10)));
        Assert.False(world.Terrain(new Cell(30, 10, 10)));
    }

    [Fact]
    public void The_safety_codes_are_the_api_codes()
    {
        Assert.Equal(ReasonCodes.HotGas, Lattice.Core.Safety.SafetyCodes.HotGas);
        Assert.Equal("terrain-in-the-way", ReasonCodes.TerrainInTheWay.ToString());
    }
}
