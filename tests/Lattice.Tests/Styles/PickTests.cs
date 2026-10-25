using Lattice.Api;
using Lattice.Core.Styles;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// Picking: one box per footprint cell, clipped to the piece's bounds; the nearest entry
/// wins, then the smaller box, then a ghost over a draft and the lower id; hidden, unpickable and beyond-clip things are
/// not hit; filters by match and by drafts.
/// </summary>
public sealed class PickTests
{
    private static PickRay Down(float x, float z, float from = 10f) => Must(PickRay.Create(new WorldPoint(x, from, z), new WorldVector(0f, -1f, 0f), 50f));

    private static PickHit.OnGhost Ghost(Option<PickHit> hit) =>
        Assert.IsType<PickHit.OnGhost>(hit.Match(static h => h, static () => throw new Xunit.Sdk.XunitException("nothing hit")));

    private static PickRay Along(float z, float direction) => Must(PickRay.Create(new WorldPoint(1f, 1f, z), new WorldVector(0f, 0f, direction), 50f));

    [Fact]
    public void Back_to_back_face_walls_are_picked_each_from_its_own_side_in_the_cell_it_lies_in()
    {
        var bench = new StyleBench(true, Lattice.Tests.Board.FaceSpotTests.Pieces());
        bench.Looks.Bounds(Lattice.Tests.Board.FaceSpotTests.Wall, LocalBox.Between(new WorldVector(-1f, -1f, 0f), new WorldVector(1f, 1f, 0.1f)));
        var onFace = new Cell(10, 10, 20);
        var front = bench.PlaceOne(Lattice.Tests.Board.FaceSpotTests.Wall, onFace);
        var back = bench.PlaceOne(Lattice.Tests.Board.FaceSpotTests.Wall, onFace, Lattice.Tests.Board.TestBoard.TurnSending(Lattice.Core.Ports.Direction.ZPlus, Lattice.Core.Ports.Direction.ZMinus, bench.T.Grid));

        var fromAbove = Ghost(bench.Styles.Pick(Along(10f, -1f), PickOptions.Default));
        var fromBelow = Ghost(bench.Styles.Pick(Along(-6f, 1f), PickOptions.Default));

        Assert.Equal(front.Id, fromAbove.Ghost);
        Assert.Equal(new Cell(10, 10, 30), fromAbove.Cell);
        Assert.Equal(10f - 2.1f, fromAbove.Distance, 3);
        Assert.Equal(back.Id, fromBelow.Ghost);
        Assert.Equal(new Cell(10, 10, 10), fromBelow.Cell);
        Assert.Equal(6f + 1.9f, fromBelow.Distance, 3);
    }

    [Fact]
    public void TryPick_gives_the_same_ghost_draft_or_miss_as_Pick_as_a_struct()
    {
        var bench = new StyleBench();
        bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        var drafts = Must(bench.Styles.Drafts(Must(DraftsId.Create(Mod, "cursor"))));
        drafts.Show(ValueList.Of(bench.Draft("StructureFrame", new Cell(10, 10, 10))));
        var onlyDrafts = PickOptions.Default with { Drafts = DraftPicks.Only };

        foreach (var (ray, options) in new[] { (Down(1f, 1f), PickOptions.Default), (Down(1f, 1f), onlyDrafts), (Down(9f, 9f), PickOptions.Default) })
        {
            var union = bench.Styles.Pick(ray, options);
            var hit = bench.Styles.TryPick(ray, options, out var result);

            Assert.Equal(union.IsSome, hit);
            Assert.Equal(union.Match(PickResult.Of, static () => default), result);
        }

        Assert.True(bench.Styles.TryPick(Down(1f, 1f), onlyDrafts, out var draft));
        Assert.Equal(PickKind.Draft, draft.Kind);
        Assert.Equal(drafts.Id, draft.Drafts);
        Assert.True(bench.Styles.TryPick(Down(1f, 1f), PickOptions.Default, out var ghost));
        Assert.Equal(PickKind.Ghost, ghost.Kind);
    }

    [Fact]
    public void TryPick_allocates_nothing_on_a_hit_or_a_miss()
    {
        var bench = new StyleBench();
        bench.PlaceOne("StructureCableStraight10", new Cell(5, 5, 50));
        var hit = Down(0.5f, 4f);
        var miss = Down(9f, 9f);
        var options = PickOptions.Default;

        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => bench.Styles.TryPick(hit, options, out _), "TryPick hit");
        global::Lattice.TestKit.Allocations.Allocations.AssertNone(() => bench.Styles.TryPick(miss, options, out _), "TryPick miss");
    }

    [Fact]
    public void A_straight_ten_is_ten_boxes_and_each_ray_hits_its_own_cell()
    {
        var bench = new StyleBench();
        var run = bench.PlaceOne("StructureCableStraight10", new Cell(5, 5, 50));

        for (var i = 0; i < 10; i++)
        {
            var z = (50 - 5 * i) * 0.1f;
            var hit = Ghost(bench.Styles.Pick(Down(0.5f, z), PickOptions.Default));
            Assert.Equal(run.Id, hit.Ghost);
            Assert.Equal(new Cell(5, 5, 50 - 5 * i), hit.Cell);
            Assert.Equal(Face.PlusY, hit.Face);
            Assert.Equal(10f - 0.75f, hit.Distance, 3);
        }

        Assert.False(bench.Styles.Pick(Down(0.5f, 5.5f), PickOptions.Default).TryGet(out _));
    }

    [Fact]
    public void Cables_packed_in_one_large_cell_are_separate_small_boxes()
    {
        var bench = new StyleBench();
        var a = bench.PlaceOne("StructureCableStraight", new Cell(5, 5, 5));
        var b = bench.PlaceOne("StructureCableStraight", new Cell(15, 5, 5));
        var c = bench.PlaceOne("StructureCableStraight", new Cell(5, 5, 15));

        Assert.Equal(a.Id, Ghost(bench.Styles.Pick(Down(0.5f, 0.5f), PickOptions.Default)).Ghost);
        Assert.Equal(b.Id, Ghost(bench.Styles.Pick(Down(1.5f, 0.5f), PickOptions.Default)).Ghost);
        Assert.Equal(c.Id, Ghost(bench.Styles.Pick(Down(0.5f, 1.5f), PickOptions.Default)).Ghost);
        Assert.False(bench.Styles.Pick(Down(1.5f, 1.5f), PickOptions.Default).TryGet(out _));
    }

    [Fact]
    public void A_wall_face_on_a_planned_frame_is_hit_before_the_frame_from_outside()
    {
        var bench = new StyleBench();
        var frame = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        var wall = bench.PlaceOne("StructureWallIron", new Cell(10, 10, 10));
        var along = Must(PickRay.Create(new WorldPoint(1f, 1f, -5f), new WorldVector(0f, 0f, 1f), 20f));

        var hit = Ghost(bench.Styles.Pick(along, PickOptions.Default));
        Assert.Equal(wall.Id, hit.Ghost);
        Assert.Equal(Face.MinusZ, hit.Face);

        var through = PickOptions.Default with { Only = Option.Some<StyleMatch>(new StyleMatch.Picks(new Selector.OfPiece(frame.Intent is Intent.Build b ? b.Target.Piece : default))) };
        Assert.Equal(frame.Id, Ghost(bench.Styles.Pick(along, through)).Ghost);
    }

    [Fact]
    public void Only_filters_by_the_candidates_facets_with_a_modded_family()
    {
        var bench = new StyleBench();
        var conduits = FamilyOf("conduits");
        bench.Looks.Family(Modded, conduits);
        bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        var conduit = bench.PlaceOne(Modded, new Cell(5, 5, 5));
        var onlyConduits = PickOptions.Default with { Only = Option.Some<StyleMatch>(new StyleMatch.Family(conduits)) };

        Assert.NotEqual(conduit.Id, Ghost(bench.Styles.Pick(Down(0.5f, 0.5f), PickOptions.Default)).Ghost);
        Assert.Equal(conduit.Id, Ghost(bench.Styles.Pick(Down(0.5f, 0.5f), onlyConduits)).Ghost);
    }

    [Fact]
    public void Drafts_are_included_excluded_or_alone_and_a_ghost_wins_a_tie_with_a_draft()
    {
        var bench = new StyleBench();
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        var drafts = Must(bench.Styles.Drafts(Must(DraftsId.Create(Mod, "cursor"))));
        drafts.Show(ValueList.Of(bench.Draft("StructureFrame", new Cell(10, 10, 10))));

        Assert.Equal(ghost.Id, Ghost(bench.Styles.Pick(Down(1f, 1f), PickOptions.Default)).Ghost);
        var onlyDrafts = bench.Styles.Pick(Down(1f, 1f), PickOptions.Default with { Drafts = DraftPicks.Only });
        var draft = Assert.IsType<PickHit.OnDraft>(onlyDrafts.Match(static h => h, static () => null!));
        Assert.Equal(0, draft.Index);
        Assert.False(draft.Trial.TryGet(out _));
        bench.T.Apply(new Command.Remove(Lattice.Tests.Board.TestBoard.Ids(ghost.Id)));
        bench.Settle();
        Assert.False(bench.Styles.Pick(Down(1f, 1f), PickOptions.Default with { Drafts = DraftPicks.Exclude }).TryGet(out _));
    }

    [Fact]
    public void Hidden_and_unpickable_things_are_never_hit()
    {
        var bench = new StyleBench();
        var near = bench.PlaceOne("StructureFrame", new Cell(10, 30, 10));
        var far = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        var unpickable = bench.Add(LayerOf("no-pick", StyleBand.View, 0, Rule(new StyleMatch.Picks(Lattice.Tests.Board.TestBoard.Ids(near.Id)), new Style { Pickable = Option.Some(Pickable.No) })));
        bench.Settle();

        Assert.Equal(far.Id, Ghost(bench.Styles.Pick(Down(1f, 1f), PickOptions.Default)).Ghost);
        unpickable.Dispose();
        bench.Settle();
        bench.Add(LayerOf("hide", StyleBand.View, 0, Rule(new StyleMatch.Picks(Lattice.Tests.Board.TestBoard.Ids(near.Id)), new Style { Presence = Option.Some(Presence.Hidden) })));
        bench.Settle();
        Assert.Equal(far.Id, Ghost(bench.Styles.Pick(Down(1f, 1f), PickOptions.Default)).Ghost);
    }

    [Fact]
    public void Things_beyond_the_clip_are_not_hit_unless_asked_for()
    {
        var bench = new StyleBench();
        var high = bench.PlaceOne("StructureFrame", new Cell(10, 30, 10));
        var low = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        Must(bench.Styles.Clip(Must(ClipShape.Plane.Create(new WorldPoint(0f, 2f, 0f), new WorldVector(0f, 1f, 0f)))));

        Assert.Equal(low.Id, Ghost(bench.Styles.Pick(Down(1f, 1f), PickOptions.Default)).Ghost);
        Assert.Equal(high.Id, Ghost(bench.Styles.Pick(Down(1f, 1f), PickOptions.Default with { BeyondClip = BeyondClipPicks.Include })).Ghost);
    }

    [Fact]
    public void Boxes_are_clipped_to_the_pieces_bounds()
    {
        var bench = new StyleBench();
        bench.Looks.Bounds("StructureFrame", LocalBox.Between(new WorldVector(-0.2f, -1f, -0.2f), new WorldVector(0.2f, 1f, 0.2f)));
        bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));

        Assert.True(bench.Styles.Pick(Down(1f, 1f), PickOptions.Default).TryGet(out _));
        Assert.False(bench.Styles.Pick(Down(0.3f, 0.3f), PickOptions.Default).TryGet(out _));
    }

    [Fact]
    public void At_equal_distance_and_size_the_lower_index_wins_and_a_ray_starting_inside_a_box_hits_it_at_once()
    {
        var bench = new StyleBench();
        var drafts = Must(bench.Styles.Drafts(Must(DraftsId.Create(Mod, "run"))));
        drafts.Show(ValueList.From(new[] { bench.Draft("StructureCableStraight", new Cell(5, 5, 5)), bench.Draft("StructureCableStraight", new Cell(5, 5, 5)) }));

        var hit = Assert.IsType<PickHit.OnDraft>(bench.Styles.Pick(Down(0.5f, 0.5f), PickOptions.Default).Match(static h => h, static () => null!));
        Assert.Equal(0, hit.Index);
        var inside = Assert.IsType<PickHit.OnDraft>(bench.Styles.Pick(Down(0.5f, 0.5f, 0.5f), PickOptions.Default).Match(static h => h, static () => null!));
        Assert.Equal(0f, inside.Distance);
    }

    [Fact]
    public void A_far_ray_across_empty_cells_ends_at_its_reach()
    {
        var bench = new StyleBench();
        bench.PlaceOne("StructureFrame", new Cell(10, 10, 410));
        var ray = Must(PickRay.Create(new WorldPoint(1f, 1f, -3f), new WorldVector(0f, 0f, 1f), 30f));

        Assert.False(bench.Styles.Pick(ray, PickOptions.Default).TryGet(out _));
        Assert.True(bench.Styles.Pick(Must(PickRay.Create(new WorldPoint(1f, 1f, -3f), new WorldVector(0f, 0f, 1f), 60f)), PickOptions.Default).TryGet(out _));
    }
}
