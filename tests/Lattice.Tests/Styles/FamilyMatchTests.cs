using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Styles;
using Lattice.Tests.Board;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// Matching in the family tree and by face: <c>Family(f)</c> holds for a piece whose family is <c>f</c> or lies under
/// <c>f</c>, so <c>Family(wall)</c> covers windows; a family rule does not outrank its parent's, order alone decides;
/// <c>Facing(axis)</c> holds for a face piece whose face, after its turn, is square to the axis, for ghosts, drafts,
/// overlays and built structures (their turn read from the world copy). Large cell centres are at odd metres, so
/// (1, 1, 2) m is a z face and (1, 2, 1) m a y face.
/// </summary>
public sealed class FamilyMatchTests
{
    private const string Window = "StructureCompositeWindowIron";
    private const string Wall = "StructureWallIron";
    private static readonly Cell ZFace = new(10, 10, 20);
    private static readonly Cell YFace = new(10, 20, 10);

    private static StyleBench Bench()
    {
        var bench = new StyleBench(true, FaceSpotTests.Pieces());
        bench.Looks.Family(Window, PieceFamilies.Window).Family(Wall, PieceFamilies.Wall)
            .Family(FaceSpotTests.Wall, PieceFamilies.Wall).Family(FaceSpotTests.ModdedWall, PieceFamilies.Window);
        bench.Add(LayerOf("probe", StyleBand.Meaning, 0,
            Rule(new StyleMatch.Family(PieceFamilies.Wall), new Style()), Rule(new StyleMatch.Family(PieceFamilies.Window), new Style()),
            Rule(new StyleMatch.Facing(Axis.X), new Style()), Rule(new StyleMatch.Facing(Axis.Y), new Style()), Rule(new StyleMatch.Facing(Axis.Z), new Style())));
        return bench;
    }

    private static readonly Style Solid = Edge(LineStyle.Solid, LibraryPalette.Select);
    private static readonly Style Dashed = Edge(LineStyle.Dashed, LibraryPalette.Warn);

    private static LineStyle? LineOf(StyleBench bench, Thing thing) =>
        bench.Styles.Look(thing.Signature).Style.Edge.Reduce(new EdgeLook.None()) is EdgeLook.Line line ? line.Style : null;

    private static LineStyle? LineOf(StyleBench bench, GhostId ghost) =>
        bench.Story(ghost).Resolved.Edge.Reduce(new EdgeLook.None()) is EdgeLook.Line line ? line.Style : null;

    private static Turn Sending(StyleBench bench, Direction from, Direction to) => TestBoard.TurnSending(from, to, bench.T.Grid);

    private static List<Thing> Drawn(StyleBench bench, ThingKind kind)
    {
        var found = new List<Thing>();
        for (var b = 0; b < bench.Styles.Plan.BatchCount; b++)
        {
            var batch = bench.Styles.Plan.BatchAt(b);
            for (var i = 0; i < batch.Count; i++)
                if (batch[i].Thing.Kind == kind)
                    found.Add(batch[i].Thing);
        }

        return found;
    }

    private static StructureRef Built(StyleBench bench, string piece, int id, Cell at) =>
        new(new ReferenceId(id), bench.T.Catalogue.Named(piece).Key, at);

    private static StructureInfo Info(StructureRef structure, Turn turn) =>
        new(structure, turn, new BuildState.Built(), Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None);

    [Fact]
    public void Wall_and_window_both_hold_for_a_window_ghost_and_only_wall_for_a_wall()
    {
        var bench = Bench();
        var window = bench.PlaceOne(Window, new Cell(10, 10, 10));
        var wall = bench.PlaceOne(Wall, new Cell(30, 10, 10));

        Assert.Contains(new StyleMatch.Family(PieceFamilies.Window), bench.Story(window.Id).Facets);
        Assert.Contains(new StyleMatch.Family(PieceFamilies.Wall), bench.Story(window.Id).Facets);
        Assert.Contains(new StyleMatch.Family(PieceFamilies.Wall), bench.Story(wall.Id).Facets);
        Assert.DoesNotContain(new StyleMatch.Family(PieceFamilies.Window), bench.Story(wall.Id).Facets);
    }

    [Fact]
    public void A_window_draft_an_overlay_and_a_built_window_hold_wall_and_window()
    {
        var bench = Bench();
        bench.Add(LayerOf("families", StyleBand.View, 0,
            Rule(new StyleMatch.All(ValueList.From(new StyleMatch[] { new StyleMatch.Family(PieceFamilies.Wall), new StyleMatch.Family(PieceFamilies.Window) })), Dashed)));
        var drafts = Must(bench.Styles.Drafts(Must(DraftsId.Create(Mod, "cursor"))));
        drafts.Show(ValueList.From(new[] { bench.Draft(Window, new Cell(10, 10, 10)), bench.Draft(Wall, new Cell(30, 10, 10)) }));
        var builtWindow = Built(bench, Window, 1, new Cell(50, 10, 10));
        var builtWall = Built(bench, Wall, 2, new Cell(70, 10, 10));
        var inspected = SetId("inspected");
        Must(bench.Styles.StructureSet(inspected)).Add(ValueList.From(new[] { builtWindow, builtWall }));
        bench.Settle();

        var shown = ((DraftsHandle)drafts).Shown;
        Assert.Equal(LineStyle.Dashed, LineOf(bench, shown[0]));
        Assert.Null(LineOf(bench, shown[1]));
        var overlays = Drawn(bench, ThingKind.Structure);
        Assert.Equal(2, overlays.Count);
        Assert.Equal(LineStyle.Dashed, LineOf(bench, overlays.Find(t => t.Piece == builtWindow.Piece)!));
        Assert.Null(LineOf(bench, overlays.Find(t => t.Piece == builtWall.Piece)!));
        Assert.True(bench.Styles.Matches(new StyleMatch.Family(PieceFamilies.Wall), builtWindow));
        Assert.True(bench.Styles.Matches(new StyleMatch.Family(PieceFamilies.Window), builtWindow));
        Assert.False(bench.Styles.Matches(new StyleMatch.Family(PieceFamilies.Window), builtWall));
    }

    [Fact]
    public void A_pick_filtered_on_window_hits_the_window_and_not_the_wall()
    {
        var bench = Bench();
        var window = bench.PlaceOne(Window, new Cell(10, 10, 10));
        bench.PlaceOne(Wall, new Cell(30, 10, 10));
        var only = PickOptions.Default with { Only = Option.Some<StyleMatch>(new StyleMatch.Family(PieceFamilies.Window)) };

        var onWindow = bench.Styles.Pick(Down(1f, 0.1f), only);
        var onWall = bench.Styles.Pick(Down(3f, 0.1f), only);

        Assert.Equal(window.Id, Assert.IsType<PickHit.OnGhost>(onWindow.Match(static h => h, static () => null!)).Ghost);
        Assert.False(onWall.IsSome);
    }

    [Fact]
    public void A_window_rule_after_the_wall_rule_wins_and_before_it_loses_and_explain_names_the_deciding_rule()
    {
        var bench = Bench();
        var window = bench.PlaceOne(Window, new Cell(10, 10, 10));
        var wall = bench.PlaceOne(Wall, new Cell(30, 10, 10));
        var layer = bench.Add(LayerOf("walls", StyleBand.View, 0,
            Rule(new StyleMatch.Family(PieceFamilies.Wall), Solid), Rule(new StyleMatch.Family(PieceFamilies.Window), Dashed)));
        bench.Settle();

        Assert.Equal(LineStyle.Dashed, LineOf(bench, window.Id));
        Assert.Equal(LineStyle.Solid, LineOf(bench, wall.Id));
        Assert.Equal(new StyleSource.Layer(StyleBand.View, Layer("walls"), 1), bench.Story(window.Id).Sources[StyleProperty.Edge]);

        layer.Dispose();
        bench.Add(LayerOf("walls", StyleBand.View, 0,
            Rule(new StyleMatch.Family(PieceFamilies.Window), Dashed), Rule(new StyleMatch.Family(PieceFamilies.Wall), Solid)));
        bench.Settle();

        Assert.Equal(LineStyle.Solid, LineOf(bench, window.Id));
        Assert.Equal(new StyleSource.Layer(StyleBand.View, Layer("walls"), 1), bench.Story(window.Id).Sources[StyleProperty.Edge]);
    }

    [Fact]
    public void A_face_piece_on_a_y_face_holds_facing_y_and_upright_it_holds_its_own_axis()
    {
        var bench = Bench();
        var flat = bench.PlaceOne(FaceSpotTests.Wall, YFace, Sending(bench, Direction.ZPlus, Direction.YPlus));
        var upright = bench.PlaceOne(FaceSpotTests.Wall, ZFace);
        var sideways = bench.PlaceOne(FaceSpotTests.Wall, new Cell(20, 10, 10), Sending(bench, Direction.ZPlus, Direction.XPlus));

        Assert.Contains(new StyleMatch.Facing(Axis.Y), bench.Story(flat.Id).Facets);
        Assert.DoesNotContain(new StyleMatch.Facing(Axis.X), bench.Story(flat.Id).Facets);
        Assert.Contains(new StyleMatch.Facing(Axis.Z), bench.Story(upright.Id).Facets);
        Assert.DoesNotContain(new StyleMatch.Facing(Axis.Y), bench.Story(upright.Id).Facets);
        Assert.Contains(new StyleMatch.Facing(Axis.X), bench.Story(sideways.Id).Facets);
    }

    [Fact]
    public void A_footprint_wall_holds_its_faces_axis_and_a_frame_and_a_cable_hold_none()
    {
        var bench = Bench();
        var wall = bench.PlaceOne(Wall, new Cell(10, 10, 10));
        var frame = bench.PlaceOne("StructureFrame", new Cell(30, 10, 10));
        var cable = bench.PlaceOne("StructureCableStraight", new Cell(5, 0, 0));

        Assert.Contains(new StyleMatch.Facing(Axis.Z), bench.Story(wall.Id).Facets);
        foreach (var none in new[] { frame.Id, cable.Id })
            foreach (var axis in new[] { Axis.X, Axis.Y, Axis.Z })
                Assert.DoesNotContain(new StyleMatch.Facing(axis), bench.Story(none).Facets);
    }

    [Fact]
    public void A_modded_face_piece_and_a_back_to_back_pair_hold_the_same_axis()
    {
        var bench = Bench();
        var front = bench.PlaceOne(FaceSpotTests.Wall, ZFace);
        var back = bench.PlaceOne(FaceSpotTests.Wall, ZFace, Sending(bench, Direction.ZPlus, Direction.ZMinus));
        var glass = bench.PlaceOne(FaceSpotTests.ModdedWall, YFace, Sending(bench, Direction.ZPlus, Direction.YMinus));

        Assert.Contains(new StyleMatch.Facing(Axis.Z), bench.Story(front.Id).Facets);
        Assert.Contains(new StyleMatch.Facing(Axis.Z), bench.Story(back.Id).Facets);
        Assert.Contains(new StyleMatch.Facing(Axis.Y), bench.Story(glass.Id).Facets);
        Assert.Contains(new StyleMatch.Family(PieceFamilies.Wall), bench.Story(glass.Id).Facets);
    }

    [Fact]
    public void A_move_that_turns_a_ghost_flat_takes_the_floor_look()
    {
        var bench = Bench();
        bench.Add(LayerOf("floors", StyleBand.View, 0, Rule(new StyleMatch.Facing(Axis.Y), Dashed)));
        var wall = bench.PlaceOne(FaceSpotTests.Wall, ZFace);
        Assert.Null(LineOf(bench, wall.Id));

        var target = new Placement(wall.Piece(), YFace, Sending(bench, Direction.ZPlus, Direction.YPlus));
        TestBoard.Applied(bench.T.Apply(new Command.Move(ValueMap.Of(wall.Id, target), Option<Revision>.None)));
        bench.Settle();

        Assert.Equal(LineStyle.Dashed, LineOf(bench, wall.Id));
    }

    [Fact]
    public void A_built_structure_faces_by_the_world_turn_and_one_the_world_lacks_faces_nothing()
    {
        var bench = Bench();
        var flat = Built(bench, FaceSpotTests.Wall, 1, YFace);
        var upright = Built(bench, FaceSpotTests.Wall, 2, ZFace);
        bench.World.Load(Info(flat, Sending(bench, Direction.ZPlus, Direction.YPlus)));
        bench.World.Load(Info(upright, Turn.None));
        var unknown = Built(bench, FaceSpotTests.Wall, 3, YFace);

        Assert.True(bench.Styles.Matches(new StyleMatch.Facing(Axis.Y), flat));
        Assert.False(bench.Styles.Matches(new StyleMatch.Facing(Axis.Z), flat));
        Assert.True(bench.Styles.Matches(new StyleMatch.Facing(Axis.Z), upright));
        foreach (var axis in new[] { Axis.X, Axis.Y, Axis.Z })
            Assert.False(bench.Styles.Matches(new StyleMatch.Facing(axis), unknown));
        Assert.False(bench.Styles.Matches(new StyleMatch.Facing(Axis.Y), Built(bench, "StructureFrame", 4, new Cell(30, 10, 10))));
    }

    [Fact]
    public void An_overlay_faces_by_the_world_turn()
    {
        var bench = Bench();
        bench.Add(LayerOf("floors", StyleBand.Selection, 0, Rule(new StyleMatch.Facing(Axis.Y), Dashed)));
        var flat = Built(bench, FaceSpotTests.Wall, 1, YFace);
        bench.World.Load(Info(flat, Sending(bench, Direction.ZPlus, Direction.YPlus)));
        Must(bench.Styles.StructureSet(SetId("inspected"))).Put(flat);
        bench.Settle();

        Assert.Equal(LineStyle.Dashed, LineOf(bench, Assert.Single(Drawn(bench, ThingKind.Structure))));
    }

    [Fact]
    public void Matching_family_and_facing_on_a_structure_allocates_nothing()
    {
        var bench = Bench();
        var flat = Built(bench, FaceSpotTests.Wall, 1, YFace);
        bench.World.Load(Info(flat, Sending(bench, Direction.ZPlus, Direction.YPlus)));
        var window = Built(bench, Window, 2, new Cell(10, 10, 10));
        var wall = new StyleMatch.Family(PieceFamilies.Wall);
        var facing = new StyleMatch.Facing(Axis.Y);

        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => bench.Styles.Matches(wall, window), "Family on a structure");
        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => bench.Styles.Matches(facing, flat), "Facing on a structure");
    }

    [Fact]
    public void A_wall_rule_resolves_the_same_looks_whether_the_window_reads_wall_or_window()
    {
        var before = new StyleBench(true);
        before.Looks.Family(Window, PieceFamilies.Wall).Family(Wall, PieceFamilies.Wall);
        var after = new StyleBench(true);
        after.Looks.Family(Window, PieceFamilies.Window).Family(Wall, PieceFamilies.Wall);
        var looks = new List<Style>();

        foreach (var bench in new[] { before, after })
        {
            bench.Add(LayerOf("walls", StyleBand.View, 0, Rule(new StyleMatch.Family(PieceFamilies.Wall), Dashed),
                Rule(new StyleMatch.Not(new StyleMatch.Family(PieceFamilies.Wall)), Solid)));
            var window = bench.PlaceOne(Window, new Cell(10, 10, 10));
            var wall = bench.PlaceOne(Wall, new Cell(30, 10, 10));
            var frame = bench.PlaceOne("StructureFrame", new Cell(50, 10, 10));
            bench.Settle();
            looks.Add(bench.Story(window.Id).Resolved);
            looks.Add(bench.Story(wall.Id).Resolved);
            looks.Add(bench.Story(frame.Id).Resolved);
        }

        Assert.Equal(looks.GetRange(0, 3), looks.GetRange(3, 3));
    }

    private static PickRay Down(float x, float z) => Must(PickRay.Create(new WorldPoint(x, 10f, z), new WorldVector(0f, -1f, 0f), 50f));
}

internal static class GhostPieces
{
    public static PrefabKey Piece(this Ghost ghost) => ((Intent.Build)ghost.Intent).Target.Piece;
}
