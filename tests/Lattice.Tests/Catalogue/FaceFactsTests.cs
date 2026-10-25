using System;
using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// A face piece's placement kind, blocking points and bounds centre, read from the prefab's own data so a modded class
/// is described the same way, and carried through the catalogue data file (decompile
/// Assets.Scripts.Objects/PlacementSnap.cs, Structure.cs blockingGrids, Thing.cs Bounds).
/// </summary>
public sealed class FaceFactsTests
{
    private static readonly TypeData[] ModdedWallTypes =
    [
        Type("ModWalls.GlassWall"),
        Type("Assets.Scripts.Objects.Wall", declares: true),
        Type("Assets.Scripts.Objects.Structure", declares: true),
    ];

    private static PrefabData Face(string name, int placement, Vector centre, params Vector[] points) =>
        Piece(name, grid: 2.0, types: ModdedWallTypes) with
        {
            Placement = placement,
            BoundsCentre = centre,
            BlockingPoints = ValueList.From(points),
        };

    private static FaceFacts FaceOf(PieceFacts facts) =>
        facts.Face.TryGet(out var face) ? face : throw new Xunit.Sdk.XunitException("no face facts");

    [Fact]
    public void A_modded_wall_subclass_takes_its_placement_points_and_centre_from_its_own_data()
    {
        var prefab = Face("ModGlassWall", 1, new Vector(0, 1, -0.05), new Vector(0, 0, 0), new Vector(0, 2, 0));

        var facts = Ok(PieceMapper.Map(prefab, ValueList<KitPlace>.Empty));

        Assert.Equal(
            Option.Some(new FaceFacts(FacePlacement.Face, ValueList.From(new[] { new CellOffset(0, 0, 0), new CellOffset(0, 20, 0) }), new Vector(0, 1, -0.05))),
            facts.Face);
        Assert.Empty(facts.Footprint);
    }

    [Fact]
    public void A_wall_mount_is_a_face_mount()
    {
        var facts = Ok(PieceMapper.Map(Face("StructureWallLight", 2, new Vector(0, 0, 0.2)), ValueList<KitPlace>.Empty));

        Assert.Equal(FacePlacement.FaceMount, FaceOf(facts).Kind);
    }

    [Fact]
    public void A_piece_placed_in_a_cell_has_no_face_facts()
    {
        var facts = Ok(PieceMapper.Map(Face("StructureFrame", 0, new Vector(0, 0, 0.5), new Vector(0, 0, 0)), ValueList<KitPlace>.Empty));

        Assert.True(facts.Face.IsNone);
    }

    [Fact]
    public void Blocking_points_round_to_the_decimetre_as_the_game_keys_them()
    {
        var facts = Ok(PieceMapper.Map(Face("ModOddWall", 1, new Vector(0, 0, 0.05), new Vector(0.04, 1.05, -0.96)), ValueList<KitPlace>.Empty));

        Assert.Equal(ValueList.Of(new CellOffset(0, 11, -10)), FaceOf(facts).Points);
    }

    [Fact]
    public void A_placement_type_the_game_does_not_have_is_refused()
    {
        var reason = Error(PieceMapper.Map(Face("ModStrange", 7, new Vector(0, 0, 0)), ValueList<KitPlace>.Empty));

        Assert.Equal(ReasonCodes.UnknownPiece, reason.Code);
        Assert.Equal("placement-type", reason.Facts["problem"]);
    }

    [Fact]
    public void The_data_file_round_trips_the_face_facts()
    {
        var wall = Face("ModGlassWall", 1, new Vector(0.125, 1, -0.05), new Vector(0, 0, 0), new Vector(0, 2, 0));
        var mount = Face("StructureWallLight", 2, new Vector(0, 0, 0.2));
        var frame = Piece("StructureFrame", grid: 2.0);
        var catalogue = new PrefabCatalogue(new CountingSource([Kit("ItemKitWalls", wall, mount, frame)], [wall, mount, frame]));
        var report = catalogue.ReadAll();
        var text = new StringWriter();

        CatalogueDataFile.Write(catalogue, report, TimeSpan.Zero, text);
        var read = Ok(CatalogueFile.Read(new StringReader(text.ToString()), "written.lcat"));

        foreach (var piece in read.Pieces)
            Assert.Equal(Ok(catalogue.Piece(piece.Facts.Key)), piece.Facts);
        Assert.True(read.Named("ModGlassWall").Face.IsSome);
        Assert.True(read.Named("StructureFrame").Face.IsNone);
    }

    [Fact]
    public void The_test_kit_writes_the_face_facts_it_reads()
    {
        var read = Ok(CatalogueFile.Read(new StringReader(Board.FaceSpotTests.Text), "face-pieces.lcat"));
        var written = new StringWriter();

        CatalogueFile.Write(read, written);
        var again = Ok(CatalogueFile.Read(new StringReader(written.ToString()), "again.lcat"));

        foreach (var piece in read.Pieces)
            Assert.Equal(piece.Facts, again.Named(piece.Name));
    }

    [Theory]
    [InlineData("  block 1 2", "block needs x y z in decimetres")]
    [InlineData("  centre 0 0", "centre needs x y z in metres")]
    public void A_bad_face_line_is_refused(string line, string problem)
    {
        var text = "lattice-catalogue 3\npiece StructureFaceWall grid=large place=face\n  step air=yes\n" + line + "\n";

        var read = CatalogueFile.Read(new StringReader(text), "bad.lcat");

        Assert.True(read.TryGetError(out var error));
        Assert.Contains(problem, error.ToString());
    }

    [Fact]
    public void A_face_line_on_a_piece_placed_in_a_cell_is_refused()
    {
        var text = "lattice-catalogue 3\npiece StructureFrame grid=large\n  step air=yes\n  centre 0 0 0\n";

        Assert.True(CatalogueFile.Read(new StringReader(text), "bad.lcat").TryGetError(out var error));
        Assert.Contains("needs place=", error.ToString());
    }
}
