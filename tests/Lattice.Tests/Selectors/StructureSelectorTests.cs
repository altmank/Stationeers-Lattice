using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.TestKit.Allocations;
using static Lattice.Tests.Selectors.TestSelect;

namespace Lattice.Tests.Selectors;

public sealed class StructureSelectorTests
{
    private const string Cable = "StructureCableStraight";
    private const string Junction = "StructureCableJunction";

    [Fact]
    public void References_pick_the_structures_standing_by_id_else_by_piece_and_cell()
    {
        var s = new TestSelect();
        var a = s.Built(Cable, At(100, 100, 100));
        var b = s.Built(Cable, At(105, 100, 100));
        s.Start();
        var renamedId = a with { Id = new ReferenceId(77) };
        var gone = new StructureRef(new ReferenceId(78), a.Piece, At(300, 300, 300));

        Assert.Equal(Set(a.Id, b.Id), s.PickBuilt(new StructureSelector.Refs(ValueList.From([renamedId, b, gone]))));
    }

    [Fact]
    public void A_box_picks_the_structures_whose_cell_lies_in_it()
    {
        var s = new TestSelect();
        var a = s.Built(Cable, At(100, 100, 100));
        var b = s.Built(Cable, At(105, 100, 100));
        s.Built(Cable, At(115, 100, 100));
        var frame = s.Built("StructureFrameIron", At(10, 10, 10));
        s.Start();

        Assert.Equal(Set(a.Id, b.Id), s.PickBuilt(new StructureSelector.Box(At(110, 100, 100), At(95, 100, 100))));
        Assert.Equal(Set(frame.Id), s.PickBuilt(new StructureSelector.Box(At(0, 0, 0), At(20, 20, 20))));
        Assert.Equal(4, s.PickBuilt(new StructureSelector.Box(At(-100_000, -100_000, -100_000), At(100_000, 100_000, 100_000))).Count);
    }

    [Fact]
    public void A_run_of_structures_stops_at_junctions_in_both_directions()
    {
        var s = new TestSelect();
        var run = Set(s.Built(Cable, At(100, 100, 100)).Id, s.Built(Cable, At(100, 100, 105)).Id, s.Built(Cable, At(100, 100, 110)).Id);
        s.Built(Junction, At(100, 100, 95));
        s.Built(Junction, At(100, 100, 115));
        s.Built(Cable, At(100, 100, 90));
        s.Built(Cable, At(100, 100, 120));
        s.Start();

        Assert.Equal(run, s.PickBuilt(new StructureSelector.RunThrough(At(100, 100, 105))));
    }

    [Fact]
    public void A_network_picks_its_pieces_not_its_devices()
    {
        var s = new TestSelect();
        var first = s.Built(Cable, At(100, 100, 100), s.AlongX);
        var second = s.Built(Cable, At(105, 100, 100), s.AlongX);
        s.Built("StructureAreaPowerControl", At(110, 100, 100));
        s.Built(Cable, At(200, 100, 100));
        s.Start();
        var network = TestSelect.Ok(NetworkOf(s, first));

        Assert.Equal(Set(first.Id, second.Id), s.PickBuilt(new StructureSelector.Network(network)));
        Assert.Empty(s.PickBuilt(new StructureSelector.Network(new NetworkRef(999_999))));
    }

    [Fact]
    public void A_room_picks_the_structures_in_it_and_the_large_ones_bounding_it()
    {
        var s = new TestSelect();
        var inside = s.Built(Cable, At(15, 10, 10));
        var beside = s.Built("StructureFrameIron", At(30, 10, 10));
        s.Built("StructureFrameIron", At(50, 10, 10));
        s.Built(Cable, At(25, 10, 10));
        s.World.SetRoom(new RoomInfo(new RoomRef(1), ValueList.Of(At(10, 10, 10)), true, Option<ValueList<GasHeld>>.None));
        s.Start();

        Assert.Equal(Set(inside.Id, beside.Id), s.PickBuilt(new StructureSelector.Room(new RoomRef(1))));
    }

    [Fact]
    public void Of_piece_and_and_or_nest()
    {
        var s = new TestSelect();
        var a = s.Built(Cable, At(100, 100, 100));
        var b = s.Built(Cable, At(200, 100, 100));
        var pipe = s.Built("StructurePipeStraight", At(105, 100, 100));
        s.Start();
        var cable = s.T.Catalogue.Named(Cable).Key;
        var box = new StructureSelector.Box(At(95, 95, 95), At(110, 110, 110));

        Assert.Equal(Set(a.Id, b.Id), s.PickBuilt(new StructureSelector.OfPiece(cable)));
        Assert.Equal(Set(a.Id), s.PickBuilt(new StructureSelector.All(ValueList.From<StructureSelector>([box, new StructureSelector.OfPiece(cable)]))));
        Assert.Equal(Set(a.Id, pipe.Id, b.Id), s.PickBuilt(new StructureSelector.Any(ValueList.From<StructureSelector>([
            box,
            new StructureSelector.All(ValueList.From<StructureSelector>([new StructureSelector.OfPiece(cable), new StructureSelector.Refs(ValueList.Of(b))])),
        ]))));
        Assert.Empty(s.PickBuilt(new StructureSelector.All(ValueList<StructureSelector>.Empty)));
    }

    [Fact]
    public void An_unknown_structure_selector_case_is_refused_unknown_kind_alone_and_inside_an_or()
    {
        var s = new TestSelect();
        s.Start();
        var later = UnknownCase.Of<StructureSelector>();

        Assert.True(s.Structures.Select(later).TryGetError(out var alone));
        Assert.Equal(ReasonCodes.UnknownKind, alone.Code);
        Assert.True(s.Structures.Select(new StructureSelector.Any(ValueList.From([later]))).TryGetError(out var nested));
        Assert.Equal(ReasonCodes.UnknownKind, nested.Code);
    }

    [Fact]
    public void Answers_are_kept_until_the_world_changes_and_are_in_id_order()
    {
        var s = new TestSelect();
        var b = s.Built(Cable, At(105, 100, 100));
        var a = s.Built(Cable, At(100, 100, 100));
        s.Start();
        var selector = new StructureSelector.Box(At(95, 95, 95), At(110, 110, 110));

        var first = TestSelect.Ok(s.Structures.Select(selector));

        Assert.Equal(first, TestSelect.Ok(s.Structures.Select(selector)));
        Allocations.AssertNone(() => s.Structures.Select(selector), "a kept answer");
        Assert.Equal([b.Id, a.Id], [first[0].Id, first[1].Id]);

        var c = s.Info(Cable, At(110, 100, 100));
        s.World.Register(c);

        Assert.Equal(3, TestSelect.Ok(s.Structures.Select(selector)).Count);
    }

    [Fact]
    public void Structure_selectors_never_scan_the_world()
    {
        var s = new TestSelect();
        var first = s.Built(Cable, At(100, 100, 100));
        s.Built(Cable, At(100, 100, 105));
        s.World.SetRoom(new RoomInfo(new RoomRef(1), ValueList.Of(At(10, 10, 10)), true, Option<ValueList<GasHeld>>.None));
        s.Start();
        var network = TestSelect.Ok(NetworkOf(s, first));
        var reads = s.Reads.Reads;

        s.PickBuilt(new StructureSelector.RunThrough(At(100, 100, 100)));
        s.PickBuilt(new StructureSelector.Box(At(0, 0, 0), At(200, 200, 200)));
        s.PickBuilt(new StructureSelector.OfPiece(first.Piece));
        s.PickBuilt(new StructureSelector.Network(network));
        s.PickBuilt(new StructureSelector.Room(new RoomRef(1)));

        Assert.Equal(1, s.Reads.Scans);
        Assert.Equal(reads + 2, s.Reads.Reads);
    }

    private static Result<NetworkRef, Reason> NetworkOf(TestSelect s, StructureRef structure) =>
        s.World.NetworkOf(structure.Id).TryGet(out var found)
            ? Result<NetworkRef, Reason>.Ok(found)
            : Result<NetworkRef, Reason>.Error(Reason.Of(ReasonCodes.NotFound, Lattice.TestKit.TestKitIds.Catalogue, ValueMap<string, string>.Empty));
}
