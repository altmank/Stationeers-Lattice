using System;
using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// The catalogue's spray cans and labellers, found by type in the prefabs (a modded one counts). A can gives as many
/// paint acts as the game spends from a full one: each act takes its use amount, in single precision, clamped at 0,
/// until the can is empty (Objects.Items/Consumable.cs, Assets.Scripts.Objects.Items/SprayCan.cs). The use amounts and
/// sizes here are test values; the game's own come from its prefabs.
/// </summary>
public sealed class PaintCanAndLabellerTests
{
    private static SprayCanData Can(string name, int colour, float use = 0.1f, float max = 1f) => new(Item(name), colour, use, max);

    private static PaintColour Colour(int index) => Ok(PaintColour.Create(index));

    [Theory]
    [InlineData(0.1f, 1f, 10)]
    [InlineData(0.25f, 1f, 4)]
    [InlineData(0.3f, 1f, 4)]
    [InlineData(1f, 1f, 1)]
    [InlineData(2f, 1f, 1)]
    [InlineData(0.05f, 2f, 41)]
    [InlineData(0.01f, 1f, 101)]
    public void A_full_can_gives_the_acts_the_game_spends_it_in(float use, float max, int acts)
    {
        Assert.Equal(acts, PrefabCatalogue.ActsPerCan(use, max));
    }

    [Fact]
    public void A_can_with_no_use_never_empties_and_one_with_no_paint_gives_none()
    {
        Assert.Equal(int.MaxValue, PrefabCatalogue.ActsPerCan(0f, 1f));
        Assert.Equal(0, PrefabCatalogue.ActsPerCan(0.1f, 0f));
    }

    [Fact]
    public void Spray_cans_are_the_sources_in_its_order_with_their_colour_and_acts()
    {
        var catalogue = new PrefabCatalogue(new CountingSource([], [],
            sprayCans: [Can("ItemSprayCanRed", 4), Can("ModSprayCanTeal", 20, 0.25f)]));

        Assert.Equal(
            new[] { new PaintCan(PrefabHash.Item("ItemSprayCanRed"), Colour(4), 10), new PaintCan(PrefabHash.Item("ModSprayCanTeal"), Colour(20), 4) },
            catalogue.PaintCans);
        Assert.Equal(Option.Some("ModSprayCanTeal"), catalogue.NameOf(PrefabHash.Item("ModSprayCanTeal")));
    }

    [Fact]
    public void A_can_whose_colour_the_game_does_not_hold_is_left_out()
    {
        var catalogue = new PrefabCatalogue(new CountingSource([], [], sprayCans: [Can("ModSprayCanNowhere", -1)]));

        Assert.Empty(catalogue.PaintCans);
    }

    [Fact]
    public void Labellers_are_the_sources_in_its_order()
    {
        var catalogue = new PrefabCatalogue(new CountingSource([], [], labellers: [Item("ItemLabeller"), Item("ModLabelGun")]));

        Assert.Equal(new[] { PrefabHash.Item("ItemLabeller"), PrefabHash.Item("ModLabelGun") }, catalogue.Labellers);
        Assert.Equal(Option.Some("ModLabelGun"), catalogue.NameOf(PrefabHash.Item("ModLabelGun")));
    }

    [Fact]
    public void Cans_and_labellers_come_through_the_kept_reads()
    {
        var source = new CountingSource([], [], sprayCans: [Can("ItemSprayCanRed", 4)], labellers: [Item("ItemLabeller")]);

        var catalogue = new PrefabCatalogue(new PrefabReads().Over(source));

        Assert.Equal(new[] { new PaintCan(PrefabHash.Item("ItemSprayCanRed"), Colour(4), 10) }, catalogue.PaintCans);
        Assert.Equal(new[] { PrefabHash.Item("ItemLabeller") }, catalogue.Labellers);
    }

    [Fact]
    public void A_source_with_no_cans_or_labellers_gives_none()
    {
        var catalogue = new PrefabCatalogue(new CountingSource([], []));

        Assert.Empty(catalogue.PaintCans);
        Assert.Empty(catalogue.Labellers);
    }

    [Fact]
    public void Cans_and_labellers_survive_the_written_file()
    {
        ListedItem Listed(string name) => new(PrefabHash.Item(name), name, name);
        var catalogue = new PrefabCatalogue(new CountingSource([], [],
            items: [Listed("ItemSprayCanRed"), Listed("ItemLabeller"), Listed("ItemWrench"), Listed("ModSprayCanTeal")],
            sprayCans: [Can("ItemSprayCanRed", 4), Can("ModSprayCanTeal", 20, 0.01f)],
            labellers: [Item("ItemLabeller")]));
        var text = new StringWriter();

        CatalogueDataFile.Write(catalogue, catalogue.ReadAll(), TimeSpan.Zero, text);
        var read = Ok(CatalogueFile.Read(new StringReader(text.ToString()), "written.lcat"));

        Assert.Contains("item ItemSprayCanRed paint=4 acts=10\n", text.ToString());
        Assert.Contains("item ItemLabeller labeller=yes\n", text.ToString());
        Assert.Contains("item ItemWrench\n", text.ToString());
        Assert.Equal(catalogue.PaintCans, read.PaintCans);
        Assert.Equal(catalogue.Labellers, read.Labellers);
    }

    [Fact]
    public void The_test_kit_writes_cans_and_labellers_back_as_it_read_them()
    {
        const string file = "lattice-catalogue 3\nitem TestCan name=Test%20Can paint=7 acts=12\nitem ItemLabeller labeller=yes\n";
        var read = Ok(CatalogueFile.Read(new StringReader(file), "hand.lcat"));
        var written = new StringWriter();

        CatalogueFile.Write(read, written);
        var again = Ok(CatalogueFile.Read(new StringReader(written.ToString()), "again.lcat"));

        Assert.Equal(new[] { new PaintCan(PrefabHash.Item("TestCan"), Colour(7), 12) }, again.PaintCans);
        Assert.Equal(new[] { PrefabHash.Item("ItemLabeller") }, again.Labellers);
    }

    [Theory]
    [InlineData("item TestCan paint=7", "acts=")]
    [InlineData("item TestCan acts=3", "paint=")]
    [InlineData("item ItemLabeller labeller=no", "labeller takes yes")]
    public void The_test_kit_refuses_a_half_given_can_or_labeller(string line, string says)
    {
        var read = CatalogueFile.Read(new StringReader("lattice-catalogue 3\n" + line + "\n"), "bad.lcat");

        Assert.True(read.TryGetError(out var error));
        Assert.Contains(says, error.ToString());
    }
}
