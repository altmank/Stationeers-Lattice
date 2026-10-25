using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.TestKit;
using static Lattice.Tests.Selectors.TestSelect;

namespace Lattice.Tests.Selectors;

public sealed class GhostSelectorTests
{
    private const string Cable = "StructureCableStraight";
    private const string Junction = "StructureCableJunction";

    /// <summary>A modded monorail: a kit the game does not have, a network kind Lattice does not model, a straight and a junction.</summary>
    private const string Monorail =
        "piece ModMonorailStraight kit=ModKitMonorail option=0 grid=small\n" +
        "  step air=no\n  end 0 0 0 z+ mod:Monorail:4096\n  end 0 0 0 z- mod:Monorail:4096\n  cell 0 0 0 rail\n" +
        "piece ModMonorailJunction kit=ModKitMonorail option=1 grid=small\n" +
        "  step air=no\n  end 0 0 0 x- mod:Monorail:4096\n  end 0 0 0 z+ mod:Monorail:4096\n  end 0 0 0 z- mod:Monorail:4096\n  cell 0 0 0 rail\n";

    [Fact]
    public void A_box_picks_exactly_the_ghosts_whose_own_cell_lies_in_it_corners_in_any_order()
    {
        var s = new TestSelect();
        var a = s.Build(Cable, At(100, 100, 100));
        var b = s.Build(Cable, At(105, 100, 100));
        s.Build(Cable, At(115, 100, 100));
        var frame = s.Build("StructureFrameIron", At(10, 10, 10));
        var remove = s.Place(new Intent.Remove(s.Info(Cable, At(110, 100, 100)).Ref));

        Assert.Equal(Set(a, b, remove), s.Pick(new Selector.Box(At(110, 100, 100), At(95, 100, 100))));
        Assert.Equal(Set(frame), s.Pick(new Selector.Box(At(0, 0, 0), At(20, 20, 20))));
        Assert.Equal(5, s.Pick(new Selector.Box(At(-100_000, -100_000, -100_000), At(100_000, 100_000, 100_000))).Count);
        Assert.Empty(s.Pick(new Selector.Box(At(200, 200, 200), At(300, 300, 300))));
    }

    [Fact]
    public void A_run_selector_stops_at_junctions_in_both_directions()
    {
        var s = new TestSelect();
        var run = new[] { s.Build(Cable, At(100, 100, 100)), s.Build(Cable, At(100, 100, 105)), s.Build(Cable, At(100, 100, 110)) };
        var below = s.Build(Junction, At(100, 100, 95));
        var above = s.Build(Junction, At(100, 100, 115));
        s.Build(Cable, At(100, 100, 90));
        s.Build(Cable, At(100, 100, 120));
        s.Build(Cable, At(95, 100, 115), s.AlongX);

        Assert.Equal(Set(run), s.Pick(new Selector.RunThrough(At(100, 100, 105))));
        Assert.Equal(Set(run), s.Pick(new Selector.RunThrough(At(100, 100, 100))));
        Assert.Equal(Set(above), s.Pick(new Selector.RunThrough(At(100, 100, 115))));
        Assert.Equal(Set(below), s.Pick(new Selector.RunThrough(At(100, 100, 95))));
    }

    [Fact]
    public void A_run_follows_a_long_straight_through_its_far_end_and_passes_corners()
    {
        var s = new TestSelect();
        var longOne = s.Build("StructureCableStraight3", At(100, 100, 120));
        var before = s.Build(Cable, At(100, 100, 105));
        var after = s.Build(Cable, At(100, 100, 125));
        var corner = s.Build("StructureCableCorner", At(100, 100, 100));
        s.Build(Junction, At(100, 100, 130));

        Assert.Equal(Set(longOne, before, after, corner), s.Pick(new Selector.RunThrough(At(100, 100, 115))));
    }

    [Fact]
    public void A_run_keeps_to_one_kind_and_stops_at_a_device_port()
    {
        var s = new TestSelect();
        var cables = Set(s.Build(Cable, At(100, 100, 100)), s.Build(Cable, At(100, 100, 105)));
        var pipes = Set(s.Build("StructurePipeStraight", At(100, 100, 110)), s.Build("StructurePipeStraight", At(100, 100, 115)));
        var alongX = Set(s.Build(Cable, At(200, 100, 100), s.AlongX), s.Build(Cable, At(205, 100, 100), s.AlongX));
        s.Build("StructureAreaPowerControl", At(210, 100, 100));
        s.Build(Cable, At(215, 100, 100), s.AlongX);

        Assert.Equal(cables, s.Pick(new Selector.RunThrough(At(100, 100, 100))));
        Assert.Equal(pipes, s.Pick(new Selector.RunThrough(At(100, 100, 115))));
        Assert.Equal(alongX, s.Pick(new Selector.RunThrough(At(200, 100, 100))));
        Assert.Empty(s.Pick(new Selector.RunThrough(At(210, 100, 100))));
    }

    [Fact]
    public void A_modded_run_of_a_kind_lattice_does_not_model_stops_at_its_junctions()
    {
        var s = new TestSelect(Pieces(Monorail));
        var run = Set(s.Build("ModMonorailStraight", At(100, 100, 100)), s.Build("ModMonorailStraight", At(100, 100, 105)));
        s.Build("ModMonorailJunction", At(100, 100, 110));
        s.Build("ModMonorailJunction", At(100, 100, 95));
        s.Build("ModMonorailStraight", At(100, 100, 115));

        Assert.Equal(run, s.Pick(new Selector.RunThrough(At(100, 100, 105))));
    }

    [Fact]
    public void A_network_selector_picks_ghosts_on_its_pieces_and_ghosts_joined_to_them_end_to_end()
    {
        var s = new TestSelect();
        s.Built(Cable, At(100, 100, 100));
        var middle = s.Built(Cable, At(100, 100, 105));
        var top = s.Built(Cable, At(100, 100, 110));
        s.Start();
        var network = TestSelect.Ok(Result(s.World.NetworkOf(top.Id)));
        var joined = s.Build(Cable, At(100, 100, 115));
        var throughGhost = s.Build(Cable, At(100, 100, 120));
        s.Build(Cable, At(100, 100, 130));
        s.Build("StructurePipeStraight", At(100, 100, 95));
        var removal = s.Place(new Intent.Remove(middle));

        Assert.Equal(Set(joined, throughGhost, removal), s.Pick(new Selector.Network(network)));
        Assert.Empty(s.Pick(new Selector.Network(new NetworkRef(999_999))));
    }

    [Fact]
    public void A_modded_network_joins_ghosts_by_its_own_kind()
    {
        var s = new TestSelect(Pieces(Monorail));
        var rail = s.Built("ModMonorailStraight", At(100, 100, 100));
        s.Start();
        var network = TestSelect.Ok(Result(s.World.NetworkOf(rail.Id)));
        var joined = s.Build("ModMonorailStraight", At(100, 100, 105));
        s.Build(Cable, At(100, 100, 95));

        Assert.Equal(Set(joined), s.Pick(new Selector.Network(network)));
    }

    [Fact]
    public void A_room_selector_picks_ghosts_in_the_room_and_large_pieces_bounding_it()
    {
        var s = new TestSelect();
        s.World.SetRoom(new RoomInfo(new RoomRef(1), ValueList.From([At(10, 10, 10), At(30, 10, 10)]), true, Option<ValueList<GasHeld>>.None));
        var inside = s.Build(Cable, At(15, 10, 10));
        var onSurface = s.Build(Cable, At(40, 10, 10));
        s.Build(Cable, At(45, 10, 10));
        var frameBeside = s.Build("StructureFrameIron", At(50, 10, 10));
        s.Build("StructureFrameIron", At(50, 30, 10));
        s.Build("StructureFrameIron", At(70, 10, 10));
        var wallOnRoomFace = s.Build("StructureWallIron", At(10, 10, 30));
        var wallInRoomCell = s.Build("StructureWallIron", At(30, 10, 10));
        s.Build("StructureWallIron", At(10, 10, -10));

        Assert.Equal(Set(inside, onSurface, frameBeside, wallOnRoomFace, wallInRoomCell), s.Pick(new Selector.Room(new RoomRef(1))));
        Assert.Empty(s.Pick(new Selector.Room(new RoomRef(2))));
    }

    [Fact]
    public void A_tag_selector_picks_ghosts_with_the_key_and_with_the_value_when_one_is_given()
    {
        var s = new TestSelect();
        var key = TestKitIds.Must(AttachmentKey.Create(TestKitIds.Mod, "route"));
        var other = TestKitIds.Must(AttachmentKey.Create(TestKitIds.Mod, "other"));
        var red = TestKitIds.Must(Attachment.Create("red"));
        var blue = TestKitIds.Must(Attachment.Create("blue"));
        var a = s.Place(Build(s, At(100, 100, 100)), attached: ValueMap.Of(key, red));
        var b = s.Place(Build(s, At(105, 100, 100)), attached: ValueMap.Of(key, blue));
        s.Place(Build(s, At(110, 100, 100)), attached: ValueMap.Of(other, red));

        Assert.Equal(Set(a, b), s.Pick(new Selector.Tagged(key, Option<Attachment>.None)));
        Assert.Equal(Set(b), s.Pick(new Selector.Tagged(key, Option.Some(blue))));
        s.T.Apply(new Command.Remove(new Selector.Ids(ValueList.Of(b))));
        Assert.Equal(Set(a), s.Pick(new Selector.Tagged(key, Option<Attachment>.None)));
    }

    [Fact]
    public void And_and_or_nest_over_board_and_module_selectors()
    {
        var s = new TestSelect();
        var group = TestKitIds.Must(GroupName.Create("Hydro"));
        var cable = s.T.Catalogue.Named(Cable).Key;
        var inBoxGrouped = s.Place(Build(s, At(100, 100, 100)), groups: ValueSet.Of(group));
        var inBox = s.Build(Cable, At(105, 100, 100));
        var pipeInBox = s.Build("StructurePipeStraight", At(100, 105, 100));
        var outside = s.Build(Cable, At(200, 100, 100));
        var box = new Selector.Box(At(95, 95, 95), At(110, 110, 110));

        var cablesInBox = new Selector.All(ValueList.From<Selector>([box, new Selector.OfPiece(cable)]));
        var nested = new Selector.Any(ValueList.From<Selector>([
            new Selector.All(ValueList.From<Selector>([new Selector.Group(group), box])),
            new Selector.All(ValueList.From<Selector>([new Selector.OfPiece(cable), new Selector.Any(ValueList.From<Selector>([new Selector.Ids(ValueList.Of(outside))]))])),
        ]));

        Assert.Equal(Set(inBoxGrouped, inBox), s.Pick(cablesInBox));
        Assert.Equal(Set(inBoxGrouped, outside), s.Pick(nested));
        Assert.Equal(Set(pipeInBox), s.Pick(new Selector.All(ValueList.From<Selector>([box, new Selector.OfPiece(s.T.Catalogue.Named("StructurePipeStraight").Key)]))));
    }

    [Fact]
    public void An_unknown_selector_case_is_refused_unknown_kind_alone_and_inside_an_and()
    {
        var s = new TestSelect();
        s.Build(Cable, At(100, 100, 100));
        var later = UnknownCase.Of<Selector>();

        Assert.True(s.T.View.Select(later).TryGetError(out var alone));
        Assert.Equal(ReasonCodes.UnknownKind, alone.Code);
        Assert.True(s.T.View.Select(new Selector.All(ValueList.From<Selector>([new Selector.Box(At(0, 0, 0), At(200, 200, 200)), later]))).TryGetError(out var nested));
        Assert.Equal(ReasonCodes.UnknownKind, nested.Code);
    }

    [Fact]
    public void Network_answers_follow_the_world_without_a_board_change()
    {
        var s = new TestSelect();
        var bottom = s.Built(Cable, At(100, 100, 100));
        s.Start();
        var ghost = s.Build(Cable, At(100, 100, 110));
        var network = TestSelect.Ok(Result(s.World.NetworkOf(bottom.Id)));
        Assert.Empty(s.Pick(new Selector.Network(network)));

        s.World.Register(s.Info(Cable, At(100, 100, 105)));

        Assert.Equal(Set(ghost), s.Pick(new Selector.Network(network)));
    }

    [Fact]
    public void Ghost_selectors_never_scan_the_world()
    {
        var s = new TestSelect();
        var bottom = s.Built(Cable, At(100, 100, 100));
        s.World.SetRoom(new RoomInfo(new RoomRef(1), ValueList.Of(At(10, 10, 10)), true, Option<ValueList<GasHeld>>.None));
        s.Start();
        s.Build(Cable, At(100, 100, 105));
        var network = TestSelect.Ok(Result(s.World.NetworkOf(bottom.Id)));
        var reads = s.Reads.Reads;

        s.Pick(new Selector.RunThrough(At(100, 100, 105)));
        s.Pick(new Selector.Box(At(0, 0, 0), At(200, 200, 200)));
        s.Pick(new Selector.Network(network));
        s.Pick(new Selector.Room(new RoomRef(1)));

        Assert.Equal(1, s.Reads.Scans);
        Assert.Equal(reads + 2, s.Reads.Reads);
    }

    private static Intent.Build Build(TestSelect s, Cell at) =>
        new(new Placement(s.T.Catalogue.Named(Cable).Key, at, default), Option<Finish>.None);

    private static Result<NetworkRef, Reason> Result(Option<NetworkRef> network) =>
        network.TryGet(out var found)
            ? Result<NetworkRef, Reason>.Ok(found)
            : Result<NetworkRef, Reason>.Error(Reason.Of(ReasonCodes.NotFound, TestKitIds.Catalogue, ValueMap<string, string>.Empty));
}
