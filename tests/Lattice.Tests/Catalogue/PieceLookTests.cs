using System;
using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// What the catalogue gives ghost drawing: a family by type, so a modded
/// subclass inherits its base's, and a look: the bounds box and where the mesh comes from (the prefab's meshes, else its
/// blueprint's, else its bounds box, else one grid cell). Both survive the catalogue data file.
/// </summary>
public sealed class PieceLookTests
{
    private const string Structure = "Assets.Scripts.Objects.Structure";

    [Theory]
    [InlineData("Assets.Scripts.Objects.Electrical.Cable", "cable")]
    [InlineData("Assets.Scripts.Objects.Pipes.Pipe", "pipe")]
    [InlineData("Assets.Scripts.Objects.Pipes.Chute", "chute")]
    [InlineData("Objects.Structures.Frame", "frame")]
    [InlineData("Assets.Scripts.Objects.Wall", "wall")]
    [InlineData("Assets.Scripts.Objects.Pipes.Device", "device")]
    [InlineData("Assets.Scripts.Objects.SmallGrid", "other")]
    public void A_game_base_class_names_the_family(string baseClass, string family)
    {
        var facts = Map(Piece("StructureThing", types: [Type(baseClass), Type(Structure, declares: true)]));

        Assert.Equal(family, facts.Family.Value);
    }

    [Fact]
    public void A_subclass_takes_the_nearest_base_along_its_chain()
    {
        var piping = Map(Piece("StructurePipeStraight", types:
            [Type("Assets.Scripts.Objects.Pipes.Piping"), Type("Assets.Scripts.Objects.Pipes.Pipe"), Type(Structure, declares: true)]));
        var window = Map(Piece("StructureWindow", grid: 2, types:
            [Type("Assets.Scripts.Objects.WallTransparent"), Type("Assets.Scripts.Objects.Wall"), Type(Structure, declares: true)]));

        Assert.Equal(PieceFamilies.Pipe, piping.Family);
        Assert.Equal(PieceFamilies.Wall, window.Family);
    }

    [Fact]
    public void A_modded_class_inherits_its_game_base_and_one_with_no_known_base_is_other()
    {
        var device = Map(Piece("ModFancyPump", types:
            [Type("FancyMod.Pumps.FancyPump", declares: true), Type("Assets.Scripts.Objects.Pipes.Device"), Type(Structure, declares: true)]));
        var stranger = Map(Piece("ModFloatingSign", types: [Type("FancyMod.Signs.FloatingSign"), Type(Structure, declares: true)]));

        Assert.Equal(PieceFamilies.Device, device.Family);
        Assert.Equal(PieceFamilies.Other, stranger.Family);
    }

    [Fact]
    public void Meshes_come_first_and_keep_their_counts()
    {
        var look = LookOf(new PrefabLook(Box(0, 1, 0, 2, 2, 2), 4, 3, 2, Option.Some(true)));

        Assert.Equal(new LookSource.Meshes(4, 3, 2), look.Source);
        Assert.Equal(Box(0, 1, 0, 2, 2, 2).Reduce(default(PieceBox)), look.Bounds);
    }

    [Fact]
    public void Without_meshes_the_blueprint_then_the_bounds_box_then_one_cell()
    {
        Assert.Equal(new LookSource.Blueprint(false), LookOf(new PrefabLook(Box(0, 0, 0, 1, 1, 1), 0, 0, 0, Option.Some(false))).Source);
        Assert.Equal(new LookSource.Box(), LookOf(new PrefabLook(Box(0, 0, 0, 1, 1, 1), 0, 0, 0, Option<bool>.None)).Source);
        Assert.Equal(new LookSource.Cell(), LookOf(new PrefabLook(Option<PieceBox>.None, 0, 0, 0, Option<bool>.None)).Source);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    public void A_piece_with_no_bounds_fills_one_cell_of_its_grid(double grid)
    {
        var facts = Map(Piece("ModInvisible", grid: grid) with { Look = Option.Some(new PrefabLook(Option<PieceBox>.None, 0, 0, 0, Option<bool>.None)) });

        Assert.True(facts.Look.TryGet(out var look));
        Assert.Equal(PieceBox.Cell(grid), look.Bounds);
    }

    [Fact]
    public void A_prefab_whose_look_was_not_read_has_none()
    {
        Assert.True(Map(Piece("StructureThing")).Look.IsNone);
    }

    [Fact]
    public void A_modded_piece_with_a_kit_the_game_lacks_gets_a_look_and_a_family_like_any_other()
    {
        var modded = Piece("ModHexWall", grid: 2, states: [State(Stack("ItemKitModHex", 2))], types:
            [Type("HexMod.HexWall"), Type("Assets.Scripts.Objects.Wall"), Type(Structure, declares: true)])
            with { Look = Option.Some(new PrefabLook(Box(0, 0, -0.9, 2, 2, 0.2), 1, 0, 0, Option<bool>.None)) };
        var catalogue = new PrefabCatalogue(new CountingSource([Kit("ItemKitModHex", modded)], [modded]));
        catalogue.ReadAll();

        var facts = Ok(catalogue.Piece(modded.Key));

        Assert.Equal(PieceFamilies.Wall, facts.Family);
        Assert.True(facts.Look.TryGet(out var look));
        Assert.Equal(new LookSource.Meshes(1, 0, 0), look.Source);
    }

    [Fact]
    public void Family_and_look_survive_the_data_file_and_the_test_kit_writes_them_back_the_same()
    {
        var catalogue = new PrefabCatalogue(SampleGame.Source());
        var report = catalogue.ReadAll();
        var text = new StringWriter();
        CatalogueDataFile.Write(catalogue, report, TimeSpan.Zero, text);

        var read = Ok(CatalogueFile.Read(new StringReader(text.ToString()), "written.lcat"));
        var again = new StringWriter();
        CatalogueFile.Write(read, again);
        var reread = Ok(CatalogueFile.Read(new StringReader(again.ToString()), "again.lcat"));

        Assert.Equal(PieceFamilies.Frame, read.Named("StructureFrame").Family);
        Assert.Equal(PieceFamilies.Device, read.Named("StructureArcFurnace").Family);
        Assert.Equal(new LookSource.Meshes(3, 2, 1), Look(read, "StructureFrame").Source);
        Assert.Equal(new LookSource.Blueprint(true), Look(read, "StructureCableStraight").Source);
        Assert.Equal(new LookSource.Box(), Look(read, "StructureArcFurnace").Source);
        Assert.Equal(new LookSource.Cell(), Look(read, "StructureCableCorner").Source);
        Assert.True(read.Named("StructureGasSensor").Look.IsNone);
        foreach (var piece in read.Pieces)
            Assert.Equal(piece.Facts, reread.Named(piece.Name));
    }

    [Fact]
    public void A_file_without_family_or_look_reads_as_other_and_no_look()
    {
        const string text = """
            lattice-catalogue 3
            piece StructureOld kit=ItemKitOld option=0 grid=large
              step material=ItemKitOld*1 air=no
            """;

        var piece = Ok(CatalogueFile.Read(new StringReader(text), "old.lcat")).Named("StructureOld");

        Assert.Equal(PieceFamilies.Other, piece.Family);
        Assert.True(piece.Look.IsNone);
    }

    [Theory]
    [InlineData("  look 0 0 0 1 1 1 meshes 2 3 0")]
    [InlineData("  look 0 0 0 1 -1 1 box")]
    [InlineData("  look 0 0 0 1 1 1 shiny")]
    [InlineData("  look 0 0 0 1 1 box")]
    [InlineData("  look 0 0 0 1 1 1 box\n  look 0 0 0 1 1 1 cell")]
    public void A_bad_look_line_is_refused_with_its_line(string lookLine)
    {
        var text = "lattice-catalogue 3\npiece StructureX kit=ItemKitX option=0 grid=large\n  step material=ItemKitX*1 air=no\n" + lookLine + "\n";

        Assert.True(CatalogueFile.Read(new StringReader(text), "bad.lcat").TryGetError(out var error));
        Assert.True(error.Line >= 4);
    }

    [Fact]
    public void A_bad_family_is_refused()
    {
        const string text = "lattice-catalogue 3\npiece StructureX kit=ItemKitX option=0 grid=large family=Not_A_Key\n  step material=ItemKitX*1 air=no\n";

        Assert.True(CatalogueFile.Read(new StringReader(text), "bad.lcat").IsError);
    }

    private static PieceFacts Map(PrefabData prefab) => Ok(PieceMapper.Map(prefab, ValueList<KitPlace>.Empty));

    private static LookFacts LookOf(PrefabLook look) =>
        Map(Piece("StructureThing", grid: 2) with { Look = Option.Some(look) }).Look.Reduce(() => throw new InvalidOperationException("no look"));

    private static LookFacts Look(FakeCatalogue catalogue, string name) =>
        catalogue.Named(name).Look.Reduce(() => throw new InvalidOperationException($"{name} has no look"));

    private static Option<PieceBox> Box(double cx, double cy, double cz, double sx, double sy, double sz) =>
        Option.Some(new PieceBox(new Vector(cx, cy, cz), new Vector(sx, sy, sz)));
}
