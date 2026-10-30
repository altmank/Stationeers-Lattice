using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// Each piece's placement (<see cref="PieceEntry.Placing"/>): the game's <c>Structure.PlacementType</c>, read for every
/// piece whatever its grid or turns (decompile <c>Assets.Scripts.Objects/PlacementSnap.cs</c>), and kept by the
/// catalogue data file.
/// </summary>
public sealed class PiecePlacingTests
{
    /// <summary>A frame, a wall placed on a face, a small device mounted on one, and a device turned freely on a face.</summary>
    private const string Text = """
        lattice-catalogue 3
        piece ModFrame grid=large
          step material=ItemSteelFrames*1 exit=ItemWrench air=no
          cell 0 0 0 centre

        piece ModWall grid=large place=face
          step entry=ItemIronSheets material=ItemIronSheets*2 exit=ItemCrowbar air=yes gravity=yes
          centre 0 0 0.05

        piece ModLight grid=small turning=face-mount/z/wcf
          step material=ItemModLight*1 exit=ItemWrench air=no
          cell 0 0 0 device

        piece ModPanel grid=small turning=face/xyz/wcf
          step material=ItemModPanel*1 exit=ItemWrench air=no
          cell 0 0 0 device
        """;

    private static PieceCatalogue ViewOf(FakeCatalogue catalogue) =>
        new(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));

    [GameCatalogueFact]
    public void The_games_frame_is_placed_in_a_cell_its_wall_on_a_face_and_its_power_control_and_vent_mounted_on_one()
    {
        var catalogue = GameCatalogue.Load();
        var view = ViewOf(catalogue);
        PiecePlacing PlacingOf(string piece) => Prefabs.Ok(view.Piece(PrefabHash.Piece(piece))).Placing;

        Assert.Equal(PiecePlacing.InGrid, PlacingOf("StructureFrame"));
        Assert.Equal(PiecePlacing.InGrid, PlacingOf("StructureCableStraight"));
        Assert.Equal(PiecePlacing.OnFace, PlacingOf("StructureWallIron"));
        Assert.Equal(PiecePlacing.OnFaceMount, PlacingOf("StructureAreaPowerControl"));
        Assert.Equal(PiecePlacing.OnFaceMount, PlacingOf("StructureFlashingLight"));
        Assert.Equal(PiecePlacing.OnFaceMount, PlacingOf("StructurePassiveVent"));
        Assert.True(Prefabs.Ok(view.Piece(PrefabHash.Piece("StructurePassiveVent"))).AllowedTurns.IsAll);
        var onFace = 0;
        var mounted = 0;
        foreach (var entry in view.Pieces)
            switch (entry.Placing)
            {
                case PiecePlacing.Face:
                    onFace++;
                    break;
                case PiecePlacing.FaceMount:
                    mounted++;
                    break;
            }

        Assert.Equal(87, onFace);
        Assert.Equal(119, mounted);
    }

    [Fact]
    public void An_entry_built_without_a_placing_and_every_hand_catalogue_piece_is_placed_in_a_cell()
    {
        var entry = new PieceEntry(new PrefabKey(1), "Piece", "Piece", PieceGrid.Large, ValueList<KitPlace>.Empty);

        Assert.Equal(PiecePlacing.InGrid, entry.Placing);
        Assert.All(ViewOf(FakeCatalogue.Hand()).Pieces, piece => Assert.Equal(PiecePlacing.InGrid, piece.Placing));
    }

    [Fact]
    public void A_file_gives_the_placing_from_place_or_from_turning()
    {
        var catalogue = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(Text), "placing.lcat"));
        var view = ViewOf(catalogue);
        PiecePlacing PlacingOf(string piece) => Prefabs.Ok(view.Piece(catalogue.Named(piece).Key)).Placing;

        Assert.Equal(PiecePlacing.InGrid, PlacingOf("ModFrame"));
        Assert.Equal(PiecePlacing.OnFace, PlacingOf("ModWall"));
        Assert.Equal(PiecePlacing.OnFaceMount, PlacingOf("ModLight"));
        Assert.Equal(PiecePlacing.OnFace, PlacingOf("ModPanel"));
        Assert.True(catalogue.Named("ModPanel").AllowedTurns.IsAll);
    }

    [Fact]
    public void A_place_and_a_turning_that_disagree_are_refused()
    {
        const string text = """
            lattice-catalogue 3
            piece ModWall grid=large place=face turning=face-mount/z/wcf
              step entry=ItemIronSheets material=ItemIronSheets*2 exit=ItemCrowbar air=yes gravity=yes
              centre 0 0 0.05
            """;

        var read = CatalogueFile.Read(new StringReader(text), "disagree.lcat");

        Assert.False(read.TryGet(out _));
        Assert.Contains("disagree", read.ToString());
    }

    [Fact]
    public void The_data_file_writes_the_placing_of_a_piece_placed_off_the_grid_even_when_it_turns_freely_and_reads_it_back()
    {
        var plain = Prefabs.Piece("ModPlain");
        var panel = Prefabs.Piece("ModPanel") with { Placement = (int)PlacedBy.Face };
        var light = Prefabs.Piece("ModLight") with { Placement = (int)PlacedBy.FaceMount };
        var catalogue = new PrefabCatalogue(new CountingSource([Prefabs.Kit("ItemKitMod", plain, panel, light)], [plain, panel, light]));
        var text = new StringWriter();

        CatalogueDataFile.Write(catalogue, catalogue.ReadAll(), System.TimeSpan.Zero, text);
        var written = text.ToString();
        var read = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(written), "placing.lcat"));
        var view = ViewOf(read);

        Assert.Equal(2, written.Split(" turning=").Length - 1);
        Assert.Contains("turning=face/xyz/wcf", written);
        Assert.Contains("turning=face-mount/xyz/wcf", written);
        Assert.Equal(PiecePlacing.InGrid, Prefabs.Ok(view.Piece(read.Named("ModPlain").Key)).Placing);
        Assert.Equal(PiecePlacing.OnFace, Prefabs.Ok(view.Piece(read.Named("ModPanel").Key)).Placing);
        Assert.Equal(PiecePlacing.OnFaceMount, Prefabs.Ok(view.Piece(read.Named("ModLight").Key)).Placing);
    }
}
