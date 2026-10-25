using System;
using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

public sealed class MergeAndBrokenToolTests
{
    private const string Mergeable = "Assets.Scripts.Util.IGridMergeable";

    private static readonly PrefabData Straight = Piece("StructureCableStraight", run: RunKind.Cable,
        types: new[] { Type("Assets.Scripts.Objects.Electrical.Cable", true), Type(Mergeable) });

    private static readonly PrefabData Junction = Piece("StructureCableJunction", run: RunKind.Cable,
        types: new[] { Type("Assets.Scripts.Objects.Electrical.Cable", true), Type(Mergeable) });

    private static readonly PrefabData Light = Piece("StructureWallLight", types: new[] { Type("Assets.Scripts.Objects.Electrical.WallLight", true) });

    private static KitData MergingKit(string name, params PrefabData[] pieces) =>
        Kit(name, pieces) with { MergeTool = Option.Some(Item("ItemWrench")) };

    private static PieceFacts Facts(KitData[] kits, PrefabData[] pieces, PrefabData piece) =>
        Ok(new PrefabCatalogue(new CountingSource(kits, pieces)).Piece(piece.Key));

    [Fact]
    public void A_mergeable_piece_whose_first_kit_merges_merges_with_that_kits_tool()
    {
        var facts = Facts(new[] { MergingKit("ItemCableCoil", Straight, Junction) }, new[] { Straight, Junction }, Junction);

        Assert.Equal(Option.Some(new MergeFacts(PrefabHash.Item("ItemWrench"))), facts.Merge);
    }

    [Fact]
    public void A_modded_mergeable_piece_in_a_modded_merging_kit_merges()
    {
        var modded = Piece("ModCableFancy", run: RunKind.Cable,
            types: new[] { Type("SomeMod.FancyCable", true), Type("Assets.Scripts.Objects.Electrical.Cable", true), Type(Mergeable) });
        var kit = Kit("ModKitFancyCable", modded) with { MergeTool = Option.Some(Item("ModFancyWrench")) };

        var facts = Facts(new[] { kit }, new[] { modded }, modded);

        Assert.Equal(Option.Some(new MergeFacts(PrefabHash.Item("ModFancyWrench"))), facts.Merge);
    }

    [Fact]
    public void A_piece_the_game_keeps_from_merging_does_not_merge()
    {
        var terminus = Straight with { Name = "StructurePowerPylonCableTerminus", Key = PrefabHash.Piece("StructurePowerPylonCableTerminus"), MergeBlocked = true };

        var facts = Facts(new[] { MergingKit("ItemCableCoil", terminus) }, new[] { terminus }, terminus);

        Assert.True(facts.Merge.IsNone);
    }

    [Fact]
    public void A_piece_that_is_not_mergeable_does_not_merge_even_in_a_merging_kit()
    {
        var facts = Facts(new[] { MergingKit("ItemKitMixed", Light) }, new[] { Light }, Light);

        Assert.True(facts.Merge.IsNone);
    }

    [Fact]
    public void Only_the_first_kit_decides_whether_a_piece_merges()
    {
        var facts = Facts(
            new[] { Kit("ItemCableCoil", Straight), MergingKit("ItemCableCoilHeavy", Straight) },
            new[] { Straight },
            Straight);

        Assert.Equal(PrefabHash.Kit("ItemCableCoil"), facts.Kits[0].Kit);
        Assert.True(facts.Merge.IsNone);
    }

    [Fact]
    public void Broken_tools_are_the_sources_in_its_order()
    {
        var catalogue = new PrefabCatalogue(new CountingSource([], [], [Item("ItemAngleGrinder"), Item("ModLaserCutter")]));

        Assert.Equal(new[] { PrefabHash.Item("ItemAngleGrinder"), PrefabHash.Item("ModLaserCutter") }, catalogue.BrokenTools);
    }

    [Fact]
    public void A_source_with_no_broken_tools_gives_none()
    {
        Assert.Empty(new PrefabCatalogue(new CountingSource([], [])).BrokenTools);
    }

    [Fact]
    public void Merge_tools_and_broken_tools_survive_the_written_file()
    {
        var catalogue = new PrefabCatalogue(new CountingSource(
            new[] { MergingKit("ItemCableCoil", Straight, Junction), Kit("ItemWallLight", Light) },
            new[] { Straight, Junction, Light },
            [Item("ItemAngleGrinder"), Item("ItemWeldingTorch")]));
        var text = new StringWriter();
        CatalogueDataFile.Write(catalogue, catalogue.ReadAll(), TimeSpan.Zero, text);

        var read = Ok(CatalogueFile.Read(new StringReader(text.ToString()), "written.lcat"));

        Assert.Contains(" merge=ItemWrench", text.ToString());
        Assert.Equal(catalogue.BrokenTools, read.BrokenTools);
        foreach (var piece in read.Pieces)
            Assert.Equal(Ok(catalogue.Piece(piece.Facts.Key)), piece.Facts);
    }

    [Fact]
    public void The_test_kit_writes_merge_tools_back_as_it_read_them()
    {
        const string file = "lattice-catalogue 3\npiece StructureCableStraight kit=ItemCableCoil option=0 grid=small merge=ItemWrench\n  step material=ItemCableCoil*1 air=no\n";
        var read = Ok(CatalogueFile.Read(new StringReader(file), "hand.lcat"));
        var written = new StringWriter();
        CatalogueFile.Write(read, written);

        var again = Ok(CatalogueFile.Read(new StringReader(written.ToString()), "again.lcat"));

        Assert.Equal(Option.Some(new MergeFacts(PrefabHash.Item("ItemWrench"))), again.Named("StructureCableStraight").Merge);
    }
}
