using System;
using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// <see cref="IPieceCatalogue.ItemName"/>: every item prefab's display name, as a piece's, so a builder words a bill or
/// a <c>needs-items</c> fact in the player's language.
/// </summary>
public sealed class ItemNameTests
{
    private const string ItemsText = """
        lattice-catalogue 3
        kit ItemKitModWall kind=single name=Kit%20(Mod%20Wall) options=StructureModWall
        item ItemKitModWall name=Kit%20(Mod%20Wall)
        item ItemModIngot name=Mod%20Ingot
        item ItemModSpanner name=Mod%20Spanner
        item ItemModPlain
        piece StructureModWall kit=ItemKitModWall option=0 grid=large name=Mod%20Wall
          step material=ItemModIngot*2 exit=ItemModSpanner air=no
        """;

    [Fact]
    public void A_kit_an_ingot_and_a_tool_read_their_display_names_and_an_item_without_one_reads_its_prefab_name()
    {
        var view = View(ItemsText);

        Assert.Equal("Kit (Mod Wall)", Ok(view.ItemName(PrefabHash.Item("ItemKitModWall"))));
        Assert.Equal(Ok(view.Kit(PrefabHash.Kit("ItemKitModWall"))).Name, Ok(view.ItemName(PrefabHash.Item("ItemKitModWall"))));
        Assert.Equal("Mod Ingot", Ok(view.ItemName(PrefabHash.Item("ItemModIngot"))));
        Assert.Equal("Mod Spanner", Ok(view.ItemName(PrefabHash.Item("ItemModSpanner"))));
        Assert.Equal("ItemModPlain", Ok(view.ItemName(PrefabHash.Item("ItemModPlain"))));
    }

    [Fact]
    public void An_unknown_item_is_refused_not_found_with_its_hash()
    {
        Assert.True(View(ItemsText).ItemName(new ItemKey(424242)).TryGetError(out var why));

        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.Equal("424242", why.Facts["item"]);
    }

    [Fact]
    public void A_kit_a_file_gives_no_item_line_still_reads_its_kit_name()
    {
        var view = View("""
            lattice-catalogue 3
            kit ItemKitModWall kind=single name=Kit%20(Mod%20Wall) options=StructureModWall
            piece StructureModWall kit=ItemKitModWall option=0 grid=large
              step exit=ItemWrench air=no
            """);

        Assert.Equal("Kit (Mod Wall)", Ok(view.ItemName(PrefabHash.Item("ItemKitModWall"))));
        Assert.True(view.ItemName(PrefabHash.Item("ItemWrench")).TryGetError(out _));
    }

    [Fact]
    public void Items_the_game_lists_are_written_with_their_names_and_read_back_the_same()
    {
        var wall = Piece("StructureModWall", grid: 2.0);
        var source = new CountingSource(
            new[] { Kit("ItemKitModWall", wall) with { Kind = KitKind.Single } },
            new[] { wall },
            items: [Listed("ItemKitModWall", "ItemKitModWall"), Listed("ItemModIngot", "Mod Ingot 100%"), Listed("ItemModPlain", "ItemModPlain")]);
        var catalogue = new PrefabCatalogue(source);
        var text = new StringWriter();
        CatalogueDataFile.Write(catalogue, catalogue.ReadAll(), TimeSpan.Zero, text);

        var read = Ok(CatalogueFile.Read(new StringReader(text.ToString()), "items.lcat"));

        Assert.Equal(catalogue.Listing.Items, read.Listing.Items);
        Assert.Contains("item ItemModIngot name=Mod%20Ingot%20100%25\n", text.ToString());
        Assert.Contains("item ItemModPlain\n", text.ToString());
        var again = new StringWriter();
        CatalogueFile.Write(read, again);
        Assert.Equal(read.ItemLines, Ok(CatalogueFile.Read(new StringReader(again.ToString()), "again.lcat")).ItemLines);
    }

    [Fact]
    public void An_item_given_twice_is_refused()
    {
        var read = CatalogueFile.Read(new StringReader("lattice-catalogue 3\nitem ItemModIngot\nitem ItemModIngot name=x\n"), "twice.lcat");

        Assert.True(read.TryGetError(out var error));
        Assert.Contains("ItemModIngot given twice", error.ToString());
    }

    [GameCatalogueFact]
    public void The_games_kits_ingots_and_tools_read_their_names_and_every_kit_reads_its_kit_name()
    {
        var catalogue = GameCatalogue.Load();
        var view = new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));

        Assert.NotEmpty(catalogue.ItemLines);
        foreach (var kit in view.Kits)
            Assert.Equal(kit.Name, Ok(view.ItemName(new ItemKey(kit.Key.Hash))));
        Assert.Equal("Ingot (Iron)", Ok(view.ItemName(PrefabHash.Item("ItemIronIngot"))));
        Assert.Equal("Wrench", Ok(view.ItemName(PrefabHash.Item("ItemWrench"))));
        Assert.Equal("Kit (Wall)", Ok(view.ItemName(PrefabHash.Item("ItemKitWall"))));
    }

    private static ListedItem Listed(string prefab, string name) => new(PrefabHash.Item(prefab), prefab, name);

    private static PieceCatalogue View(string text)
    {
        var catalogue = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(text), "items.lcat"));
        return new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));
    }
}
