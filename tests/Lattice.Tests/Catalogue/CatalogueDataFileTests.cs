using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

public sealed class CatalogueDataFileTests
{
    private static (PrefabCatalogue Catalogue, string Text) Written()
    {
        var catalogue = new PrefabCatalogue(SampleGame.Source());
        var report = catalogue.ReadAll();
        var text = new StringWriter();
        CatalogueDataFile.Write(catalogue, report, TimeSpan.FromMilliseconds(1234), text);
        return (catalogue, text.ToString());
    }

    [Fact]
    public void The_written_file_reads_back_through_the_test_kit_as_the_same_facts()
    {
        var (catalogue, text) = Written();

        var read = Ok(CatalogueFile.Read(new StringReader(text), "written.lcat"));

        Assert.Equal(6, read.Pieces.Count);
        Assert.Empty(read.Named("StructureCableStraightBurnt").Kits);
        foreach (var piece in read.Pieces)
            Assert.Equal(Ok(catalogue.Piece(piece.Facts.Key)), piece.Facts);
    }

    [Fact]
    public void The_support_group_survives_the_round_trip()
    {
        var (_, text) = Written();

        var read = Ok(CatalogueFile.Read(new StringReader(text), "written.lcat"));

        Assert.Equal(Option.Some(SupportGroups.FrameBelow), read.Named("StructureArcFurnace").SupportGroup);
    }

    [Fact]
    public void The_file_says_it_came_from_the_game_how_long_the_load_took_and_what_was_left_out()
    {
        var (_, text) = Written();

        Assert.StartsWith(CatalogueFile.Header + "\n" + CatalogueDataFile.FromGameLine, text.Replace("\r\n", "\n"));
        Assert.Contains("# load-ms 1234", text);
        Assert.Contains("# refused StructureOdd", text);
    }

    [Fact]
    public void Every_kit_is_written_with_its_kind_and_every_option_so_the_files_view_lists_the_games_kits()
    {
        var (catalogue, text) = Written();
        var read = Ok(CatalogueFile.Read(new StringReader(text), "written.lcat"));

        var fromGame = new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue));
        var fromFile = new PieceCatalogue(read.Listing, new CatalogueAdditions(read));

        Assert.Equal(fromGame.Kits, fromFile.Kits);
        Assert.Equal(catalogue.Listing.Kits.Select(k => k.Kind), read.Listing.Kits.Select(k => k.Kind));
        foreach (var piece in fromFile.Pieces)
            Assert.Equal(Ok(fromGame.Piece(piece.Key)), piece);
    }

    [Fact]
    public void Display_names_with_spaces_and_marks_survive_the_file()
    {
        var wall = Piece("StructureModWall", grid: 2.0);
        var source = new CountingSource(
            new[] { Kit("ItemKitModWall", wall) with { DisplayName = "Kit (Mod Wall) #1, grey", Kind = KitKind.Single } },
            new[] { wall },
            displayNames: new Dictionary<PrefabKey, string> { [wall.Key] = "Mod Wall 100%" });
        var catalogue = new PrefabCatalogue(source);
        var text = new StringWriter();
        CatalogueDataFile.Write(catalogue, catalogue.ReadAll(), TimeSpan.Zero, text);

        var read = Ok(CatalogueFile.Read(new StringReader(text.ToString()), "names.lcat"));

        Assert.Equal(catalogue.Listing, read.Listing with { Pieces = ValueList.From(read.Listing.Pieces.Select(p => p with { FirstStep = PrefabCatalogue.FirstStep(wall) })) });
        Assert.Contains("kit ItemKitModWall kind=single name=Kit%20(Mod%20Wall)%20%231%2C%20grey options=StructureModWall", text.ToString());
    }

    [Fact]
    public void A_piece_in_two_kits_is_written_once_under_its_own_kit()
    {
        var (_, text) = Written();

        var read = Ok(CatalogueFile.Read(new StringReader(text), "written.lcat"));

        Assert.Equal(PrefabHash.Kit("ItemKitSensor"), read.Named("StructureGasSensor").Kits[0].Kit);
        Assert.Equal(2, read.Named("StructureGasSensor").Kits.Count);
        Assert.Equal(PrefabHash.Kit("ItemKitFurnaces"), read.Named("StructureArcFurnace").Kits[0].Kit);
    }
}

public sealed class ExportSwitchTests
{
    [Fact]
    public void No_switch_is_no_export() =>
        Assert.True(Ok(ExportSwitch.Parse(new[] { "rocketstation.exe", "-batchmode" })).IsNone);

    [Fact]
    public void The_switch_names_the_file()
    {
        var path = Ok(ExportSwitch.Parse(new[] { "rocketstation.exe", ExportSwitch.Name, @"C:\out\catalogue.lcat" }));

        Assert.Equal(@"C:\out\catalogue.lcat", path.Reduce(""));
    }

    [Fact]
    public void The_switch_without_a_file_is_refused_with_a_reason()
    {
        var reason = Error(ExportSwitch.Parse(new[] { "rocketstation.exe", ExportSwitch.Name }));

        Assert.Equal(ReasonCodes.InvalidValue, reason.Code);
    }

    [Fact]
    public void The_switch_followed_by_another_switch_is_refused() =>
        Assert.True(ExportSwitch.Parse(new[] { ExportSwitch.Name, "-batchmode" }).IsError);
}
