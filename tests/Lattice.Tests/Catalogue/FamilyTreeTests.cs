using System;
using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Styles;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Catalogue.Prefabs;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// Families form a tree: <c>window</c> lies under <c>wall</c>, the other families have no parent. A piece's family is
/// the narrowest one; its chain is that family and every family above it. The game's see-through faces are
/// <c>WallTransparent</c> and its subclasses (decompile Assets.Scripts.Objects/WallTransparent.cs, ShutteredWindow.cs):
/// one whose last build state seals air is a window, one that never seals (a floor grating) is a wall, and one with no
/// states read is a window (decompile Assets.Scripts.Objects/Structure.cs, <c>BuildState.BlockAir</c>).
/// </summary>
public sealed class FamilyTreeTests
{
    private const string Structure = "Assets.Scripts.Objects.Structure";
    private const string Wall = "Assets.Scripts.Objects.Wall";
    private const string WallTransparent = "Assets.Scripts.Objects.WallTransparent";

    private static readonly PieceFamily[] ZeroSixFamilies =
        [PieceFamilies.Cable, PieceFamilies.Pipe, PieceFamilies.Chute, PieceFamilies.Frame, PieceFamilies.Wall, PieceFamilies.Device, PieceFamilies.Other];

    private const string Text = """
        lattice-catalogue 3
        piece StructureWindowPane kit=ItemKitWindow option=0 grid=large family=window
          step material=ItemKitWindow*1 air=yes
        piece StructurePlainWall kit=ItemKitWall option=0 grid=large family=wall
          step material=ItemKitWall*1 air=yes
        piece StructureFrame kit=ItemSteelFrames option=0 grid=large family=frame
          step material=ItemSteelFrames*1 air=no
        piece ModSkyPane kit=ItemKitModSky option=0 grid=large
          step material=ItemKitModSky*1 air=yes
        """;

    private static readonly FakeCatalogue Catalogue = Read(Text);

    private static FakeCatalogue Read(string text) =>
        CatalogueFile.Read(new StringReader(text), "families.lcat").Match(static c => c, static e => throw new InvalidOperationException(e.ToString()));

    private static PrefabKey Key(string name) => PrefabHash.Piece(name);

    private static ValueList<TypeData> Chain(params string[] types)
    {
        var data = new TypeData[types.Length + 1];
        for (var i = 0; i < types.Length; i++)
            data[i] = Type(types[i], declares: i == 0);
        data[types.Length] = Type(Structure, declares: true);
        return ValueList.From(data);
    }

    [Theory]
    [InlineData(new[] { WallTransparent, Wall }, "001", "window")]
    [InlineData(new[] { "Assets.Scripts.Objects.ShutteredWindow", WallTransparent, Wall }, "0001", "window")]
    [InlineData(new[] { WallTransparent, Wall }, "0", "wall")]
    [InlineData(new[] { WallTransparent, Wall }, "", "window")]
    [InlineData(new[] { "Assets.Scripts.Objects.ShutteredWindowConnector", Wall }, "011", "wall")]
    [InlineData(new[] { "Assets.Scripts.Objects.Structures.LadderPlatform", "Assets.Scripts.Objects.Structures.Floor", Wall }, "01", "wall")]
    [InlineData(new[] { "Objects.Rockets.RocketCrewUmbilicalDoor", Wall }, "01", "wall")]
    [InlineData(new[] { "SkyMod.Panes.SkyPane", WallTransparent, Wall }, "01", "window")]
    [InlineData(new[] { "SkyMod.Panes.SkyGrille", WallTransparent, Wall }, "00", "wall")]
    [InlineData(new[] { WallTransparent, Wall }, "010", "wall")]
    public void The_class_chain_and_the_last_build_state_give_the_narrowest_family(string[] types, string seals, string family) =>
        Assert.Equal(family, TypeFamilies.Of(Chain(types), States(seals)).Value);

    /// <summary>One build state per character: 1 seals air, 0 lets it through.</summary>
    private static ValueList<StateData> States(string seals)
    {
        var states = new StateData[seals.Length];
        for (var i = 0; i < states.Length; i++)
            states[i] = State(air: seals[i] == '1');
        return ValueList.From(states);
    }

    [Fact]
    public void The_mapper_gives_a_sealing_see_through_face_window_and_a_grating_wall()
    {
        var window = Ok(PieceMapper.Map(Piece("StructureWindowPane", grid: 2, types: [Type(WallTransparent), Type(Wall), Type(Structure, declares: true)],
            states: [State(), State(air: true)]), ValueList<KitPlace>.Empty));
        var grating = Ok(PieceMapper.Map(Piece("StructureGrating", grid: 2, types: [Type(WallTransparent), Type(Wall), Type(Structure, declares: true)],
            states: [State()]), ValueList<KitPlace>.Empty));

        Assert.Equal(PieceFamilies.Window, window.Family);
        Assert.Equal(PieceFamilies.Wall, grating.Family);
    }

    [Fact]
    public void A_window_lies_under_wall_and_the_families_of_before_have_no_parent()
    {
        var view = View(new CatalogueAdditions(Catalogue));

        Assert.Equal(Option.Some(PieceFamilies.Wall), view.ParentOf(PieceFamilies.Window));
        Assert.Equal("window", PieceFamilies.Window.Value);
        foreach (var family in ZeroSixFamilies)
            Assert.False(view.ParentOf(family).IsSome, family.Value);
        Assert.False(view.ParentOf(Ok(PieceFamily.Create(Ok(ModId.Create("planner")), "trim"))).IsSome);
    }

    [Fact]
    public void A_window_pieces_chain_is_window_then_wall_and_its_family_is_the_top()
    {
        var view = View(new CatalogueAdditions(Catalogue));

        Assert.Equal(ValueList.From(new[] { PieceFamilies.Window, PieceFamilies.Wall }), Ok(view.FamiliesOf(Key("StructureWindowPane"))));
        Assert.Equal(PieceFamilies.Wall, Ok(view.FamilyOf(Key("StructureWindowPane"))));
        Assert.Equal(ValueList.Of(PieceFamilies.Wall), Ok(view.FamiliesOf(Key("StructurePlainWall"))));
        Assert.Equal(ValueList.Of(PieceFamilies.Frame), Ok(view.FamiliesOf(Key("StructureFrame"))));
        Assert.Equal(PieceFamilies.Frame, Ok(view.FamilyOf(Key("StructureFrame"))));
        Assert.Equal(ReasonCodes.UnknownPiece, Error(view.FamiliesOf(Key("ModNotLoaded"))).Code);
    }

    [Fact]
    public void A_modded_piece_put_in_window_joins_wall_too_until_disposed()
    {
        var additions = new CatalogueAdditions(Catalogue);
        var view = View(additions);
        var looks = new CataloguePieceLooks(Catalogue, additions);
        var pane = Key("ModSkyPane");

        var handle = Ok(additions.Register(new CatalogueAddition.InFamily(pane, PieceFamilies.Window)));

        Assert.Equal(ValueList.From(new[] { PieceFamilies.Window, PieceFamilies.Wall }), Ok(view.FamiliesOf(pane)));
        Assert.Equal(PieceFamilies.Wall, Ok(view.FamilyOf(pane)));
        Assert.Equal(PieceFamilies.Window, looks.Family(pane));
        Assert.Equal(ValueList.From(new[] { PieceFamilies.Window, PieceFamilies.Wall }), looks.Families(pane));
        handle.Dispose();
        Assert.Equal(ValueList.Of(PieceFamilies.Other), Ok(view.FamiliesOf(pane)));
        Assert.Equal(ValueList.Of(PieceFamilies.Other), looks.Families(pane));
    }

    [Fact]
    public void A_window_put_in_a_mods_own_family_leaves_wall()
    {
        var additions = new CatalogueAdditions(Catalogue);
        var view = View(additions);
        var trim = Ok(PieceFamily.Create(Ok(ModId.Create("planner")), "trim"));

        using var handle = Ok(additions.Register(new CatalogueAddition.InFamily(Key("StructureWindowPane"), trim)));

        Assert.Equal(ValueList.Of(trim), Ok(view.FamiliesOf(Key("StructureWindowPane"))));
        Assert.Equal(trim, Ok(view.FamilyOf(Key("StructureWindowPane"))));
    }

    [Fact]
    public void The_family_reads_allocate_nothing()
    {
        var additions = new CatalogueAdditions(Catalogue);
        var view = View(additions);
        var looks = new CataloguePieceLooks(Catalogue, additions);
        var window = Key("StructureWindowPane");
        var pane = Key("ModSkyPane");
        using var handle = Ok(additions.Register(new CatalogueAddition.InFamily(pane, PieceFamilies.Window)));

        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => view.FamiliesOf(window), "FamiliesOf");
        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => view.FamiliesOf(pane), "FamiliesOf a registered window");
        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => view.FamilyOf(window), "FamilyOf");
        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => view.ParentOf(PieceFamilies.Window), "ParentOf");
        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => looks.Families(window), "the styles' chain");
    }

    [Fact]
    public void The_data_file_writes_the_narrowest_family_and_reads_window_back()
    {
        var window = Piece("StructureWindowPane", grid: 2, types: [Type(WallTransparent), Type(Wall), Type(Structure, declares: true)],
            states: [State(Stack("ItemKitWindow", 1), air: true)]);
        var catalogue = new PrefabCatalogue(new CountingSource([Kit("ItemKitWindow", window)], [window]));
        var report = catalogue.ReadAll();
        var text = new StringWriter();
        CatalogueDataFile.Write(catalogue, report, TimeSpan.Zero, text);

        Assert.Contains("family=window", text.ToString());
        var read = Ok(CatalogueFile.Read(new StringReader(text.ToString()), "written.lcat"));
        Assert.Equal(PieceFamilies.Window, read.Named("StructureWindowPane").Family);
        var again = new StringWriter();
        CatalogueFile.Write(read, again);
        Assert.Equal(PieceFamilies.Window, Ok(CatalogueFile.Read(new StringReader(again.ToString()), "again.lcat")).Named("StructureWindowPane").Family);
    }

    [Fact]
    public void A_file_that_says_wall_for_a_window_reads_wall()
    {
        var old = Read("""
            lattice-catalogue 3
            piece StructureWindowPane kit=ItemKitWindow option=0 grid=large family=wall
              step material=ItemKitWindow*1 air=yes
            """);

        Assert.Equal(ValueList.Of(PieceFamilies.Wall), Ok(View(old, new CatalogueAdditions(old)).FamiliesOf(Key("StructureWindowPane"))));
    }

    [Fact]
    public void The_hand_catalogues_window_is_a_window()
    {
        var hand = FakeCatalogue.Hand();

        Assert.Equal(PieceFamilies.Window, hand.Named("StructureCompositeWindowIron").Family);
    }

    private static PieceCatalogue View(CatalogueAdditions additions) => new(Catalogue.Listing, additions, PiecePorts.Over(Catalogue), Lattice.Core.Styles.Facings.Over(Catalogue));

    private static PieceCatalogue View(FakeCatalogue catalogue, CatalogueAdditions additions) => new(catalogue.Listing, additions, PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));
}
