using System.IO;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.Scenarios;

namespace Lattice.Tests.TestKit;

/// <summary>The catalogue data file reads, writes back unchanged, and says where a bad file is wrong.</summary>
public sealed class CatalogueFileTests
{
    [Fact]
    public void Prefab_hashes_are_the_games() =>
        Assert.Equal(-1240951678, PrefabHash.Of("StructureFrameIron")); // the game's own hash

    [Fact]
    public void The_hand_catalogue_holds_about_twenty_pieces()
    {
        var catalogue = FakeCatalogue.Hand();
        Assert.InRange(catalogue.Pieces.Count, 20, 30);
        Assert.Equal(5000, catalogue.Named("StructureCableStraight").Ratings.Cable.Reduce(default(Watts)).Value);
        Assert.Equal(3, catalogue.Named("StructureFrameIron").Steps.Count);
    }

    [Fact]
    public void Writing_and_reading_back_gives_the_same_pieces()
    {
        var original = FakeCatalogue.Hand();
        var written = new StringWriter();
        CatalogueFile.Write(original, written);

        var read = Read(written.ToString());

        Assert.True(read.TryGet(out var copy), read.ToString());
        Assert.Equal(original.Pieces.Count, copy.Pieces.Count);
        for (var i = 0; i < original.Pieces.Count; i++)
            Assert.Equal(original.Pieces[i].Facts, copy.Pieces[i].Facts);
        Assert.Equal(original.BrokenTools, copy.BrokenTools);
    }

    [Fact]
    public void The_hand_catalogue_names_the_games_three_broken_tools() =>
        Assert.Equal(
            ValueList.From([PrefabHash.Item("ItemAngleGrinder"), PrefabHash.Item("ItemArcWelder"), PrefabHash.Item("ItemWeldingTorch")]),
            FakeCatalogue.Hand().BrokenTools);

    [Fact]
    public void A_file_without_broken_tools_names_none() =>
        Assert.Empty(TestKitIds.MustRead(Read("lattice-catalogue 3\npiece A kit=K option=0 grid=small\n  step air=no")).BrokenTools);

    [Theory]
    [InlineData("not a catalogue", 1)]
    [InlineData("lattice-catalogue 3\n  step air=no", 2)]
    [InlineData("lattice-catalogue 3\npiece A kit=K option=0 grid=huge", 2)]
    [InlineData("lattice-catalogue 3\npiece A kit=K option=0 grid=small\n  step air=no\n  end 0 0 0 up cable", 4)]
    [InlineData("lattice-catalogue 3\nbroken-tools A\nbroken-tools B", 3)]
    [InlineData("lattice-catalogue 3\nbroken-tools A,,B", 2)]
    public void A_bad_line_is_reported_with_its_number(string text, int line)
    {
        Assert.True(Read(text).TryGetError(out var error));
        Assert.Equal(line, error.Line);
    }

    [Fact]
    public void A_kit_option_given_twice_is_refused() =>
        Assert.True(Read("lattice-catalogue 3\npiece A kit=K option=0 grid=small\n  step air=no\npiece B kit=K option=0 grid=small\n  step air=no").IsError);

    private const string EveryForm =
        "lattice-catalogue 3\n" +
        "piece StructureSensor kit=ItemKitSensor option=2 also=ItemGasSensor:0 grid=small\n" +
        "  step material=ItemKitSensor*1 air=no\n" +
        "  end 0 0 0 y- power,data role=input\n" +
        "piece StructureBurnt grid=small\n" +
        "  step air=no\n" +
        "  end 0 0 0 z+ power,data reach=400\n" +
        "  cell 0 0 0 cable\n" +
        "  collision 2\n" +
        "piece StructureLiquidPipe kit=ItemKitLiquidPipe option=0 grid=small pipe=liquid\n" +
        "  step material=ItemKitLiquidPipe*1 air=no\n" +
        "  end 0 0 0 z+ liquid,mod:Hydrogen:512 reach=370\n" +
        "piece StructureWallThing kit=ItemKitWallThing option=0 grid=large\n" +
        "  step air=yes gravity=yes\n" +
        "  cell 0 0 0 face-z-\n";

    private const string Pipes =
        "lattice-catalogue 3\n" +
        "piece GasPipe kit=K option=0 grid=small pipe=gas\n  step air=no\n  end 0 0 0 z+ gas\n  end 0 0 0 z- gas\n" +
        "piece LiquidPipe kit=K option=1 grid=small pipe=liquid\n  step air=no\n  end 0 0 0 z+ liquid\n  end 0 0 0 z- liquid\n";

    [Fact]
    public void Every_form_of_the_file_reads_and_writes_back_unchanged()
    {
        var first = Read(EveryForm);
        Assert.True(first.TryGet(out var catalogue), first.ToString());
        var written = new StringWriter();
        CatalogueFile.Write(catalogue, written);
        Assert.True(Read(written.ToString()).TryGet(out var copy), written.ToString());

        for (var i = 0; i < catalogue.Pieces.Count; i++)
            Assert.Equal(catalogue.Pieces[i].Facts, copy.Pieces[i].Facts);
        var sensor = catalogue.Named("StructureSensor");
        Assert.Equal(2, sensor.Kits.Count);
        Assert.Equal(sensor.Key, TestKitIds.Must(catalogue.PieceOf(PrefabHash.Kit("ItemGasSensor"), default)));
        Assert.Empty(catalogue.Named("StructureBurnt").Kits);
        Assert.Equal(Option.Some(PipeContent.Liquid), catalogue.Named("StructureLiquidPipe").Pipe);
        Assert.Contains(new NetworkType.Unknown("Hydrogen", 512), catalogue.Named("StructureLiquidPipe").Ends[0].Kinds);
        Assert.True(catalogue.Named("StructureWallThing").Steps[0].BlocksGravity);
    }

    [Fact]
    public void A_gas_pipe_and_a_liquid_pipe_never_join()
    {
        var catalogue = TestKitIds.MustRead(Read(Pipes));
        var world = Scenario.With(catalogue)
            .Built("GasPipe", new Cell(0, 0, 0), out var gas)
            .Built("GasPipe", new Cell(0, 0, 5), out var gas2)
            .Built("LiquidPipe", new Cell(0, 0, 10), out var liquid)
            .Build().World;

        Assert.Equal(world.NetworkOf(gas.Id), world.NetworkOf(gas2.Id));
        Assert.NotEqual(world.NetworkOf(gas2.Id), world.NetworkOf(liquid.Id));
    }

    private static Result<FakeCatalogue, FileError> Read(string text) => CatalogueFile.Read(new StringReader(text), "test.lcat");

    [Fact]
    public void A_file_of_an_older_format_is_refused_saying_to_write_it_again()
    {
        var error = Read("lattice-catalogue 2\npiece A kit=K option=0 grid=small\n  step air=no");

        Assert.True(error.TryGetError(out var problem));
        Assert.Contains("format 2, this reader reads format 3: write the file again", problem.Problem);
    }

    private const string KitLines = """
        lattice-catalogue 3
        kit ItemKitMod kind=multi name=Kit%20(Mod%20%231)%2C%20wide options=ModA,ModGone,ModB
        piece ModA kit=ItemKitMod option=0 grid=small name=Mod%20A%25
          step air=no
        piece ModB kit=ItemKitMod option=2 grid=small
          step air=no
        """;

    [Fact]
    public void Kit_lines_and_display_names_read_and_write_back_unchanged()
    {
        var catalogue = TestKitIds.MustRead(Read(KitLines));
        var written = new StringWriter();
        CatalogueFile.Write(catalogue, written);
        var copy = TestKitIds.MustRead(Read(written.ToString()));

        var kit = Assert.Single(copy.Listing.Kits);
        Assert.Equal("Kit (Mod #1), wide", kit.Name);
        Assert.Equal(Lattice.Core.Catalogue.KitKind.Multi, kit.Kind);
        Assert.Equal(new[] { PrefabHash.Piece("ModA"), PrefabHash.Piece("ModGone"), PrefabHash.Piece("ModB") }, kit.Options);
        Assert.Equal("Mod A%", copy.Listing.Pieces[0].Name);
        Assert.Equal("ModB", copy.Listing.Pieces[1].Name);
        Assert.Equal(catalogue.Listing, copy.Listing);
    }

    [Theory]
    [InlineData("kit ItemKitMod kind=multi options=ModB,ModA\npiece ModA kit=ItemKitMod option=0 grid=small\n  step air=no", "option 0 of ItemKitMod is ModB")]
    [InlineData("kit ItemKitMod kind=multi options=ModA\npiece ModA kit=ItemKitMod option=1 grid=small\n  step air=no", "past its last")]
    [InlineData("kit ItemKitMod kind=huge options=ModA", "kind=")]
    [InlineData("kit ItemKitMod kind=multi options=ModA\nkit ItemKitMod kind=multi options=ModA", "given twice")]
    [InlineData("piece ModA grid=small name=Bad%2\n  step air=no", "bad name")]
    public void A_kit_line_that_disagrees_or_a_gap_without_one_is_refused(string body, string says)
    {
        var error = Read("lattice-catalogue 3\n" + body);

        Assert.True(error.TryGetError(out var problem));
        Assert.Contains(says, problem.Problem);
    }

    [Fact]
    public void A_kit_whose_piece_lines_leave_a_gap_reads_and_says_so()
    {
        var part = TestKitIds.MustRead(Read("lattice-catalogue 3\npiece ModB kit=ItemKitMod option=1 grid=small\n  step air=no"));

        Assert.Equal(Option.Some("ItemKitMod has no option 0"), part.Gap);
        Assert.Equal(new[] { default, PrefabHash.Piece("ModB") }, Assert.Single(part.Listing.Kits).Options);
        Assert.True(FakeCatalogue.Hand().Gap.IsNone);
    }

    [Theory]
    [InlineData("Iron Wall")]
    [InlineData("Kit (Pipe) #2, 100%")]
    [InlineData("Tür\tÖffner\u3000日本")]
    [InlineData("")]
    public void A_display_name_is_one_word_of_the_file_and_reads_back(string name)
    {
        var word = Lattice.Core.Catalogue.CatalogueText.Name(name);

        Assert.DoesNotContain(word, c => char.IsWhiteSpace(c) || c is '#' or ',');
        Assert.Equal(Option.Some(name), Lattice.Core.Catalogue.CatalogueText.ParseName(word));
    }
}
