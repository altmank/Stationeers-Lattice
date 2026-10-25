using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Board;

public sealed class SpotTests
{
    private static readonly Cell Small = new(100, 100, 100);
    private static readonly Cell Large = new(10, 10, 10);

    [Fact]
    public void A_new_ghost_in_a_spot_replaces_the_old_one_in_one_change_set()
    {
        var t = new TestBoard();
        var old = Assert.Single(t.Placed(t.Draft("StructureCableStraight", Small)).Added);

        var changes = t.Placed(t.Draft("StructureCableCorner", Small));

        var added = Assert.Single(changes.Added);
        var removed = Assert.Single(changes.Removed);
        Assert.Equal(old.Id, removed.Ghost.Id);
        Assert.Equal(new RemovalCause.Replaced(added.Id), removed.Cause);
        Assert.Equal(ValueList.Of(added), t.View.At(Small));
    }

    [Fact]
    public void A_piece_overlapping_part_of_a_ghost_in_the_same_slot_replaces_it()
    {
        var t = new TestBoard();
        var straight = Assert.Single(t.Placed(t.Draft("StructureCableStraight3", Small)).Added);

        var changes = t.Placed(t.Draft("StructureCableStraight", Small.Plus(new CellOffset(0, 0, -5))));

        Assert.Equal(straight.Id, Assert.Single(changes.Removed).Ghost.Id);
        Assert.Empty(t.View.At(Small));
    }

    [Fact]
    public void One_straight_of_three_replaces_three_single_cables()
    {
        var t = new TestBoard();
        var singles = t.Placed(
            t.Draft("StructureCableStraight", Small),
            t.Draft("StructureCableStraight", Small.Plus(new CellOffset(0, 0, -5))),
            t.Draft("StructureCableStraight", Small.Plus(new CellOffset(0, 0, -10)))).Added;

        var changes = t.Placed(t.Draft("StructureCableStraight3", Small));

        Assert.Equal(3, changes.Removed.Count);
        foreach (var single in singles)
            Assert.Contains(changes.Removed, r => r.Ghost.Id == single.Id);
    }

    [Fact]
    public void Two_walls_back_to_back_on_one_boundary_are_two_spots_one_in_each_cell()
    {
        var t = new TestBoard();
        var wall = Assert.Single(t.Placed(t.Draft("StructureWallIron", Large)).Added);
        var flipped = TurnSending(Direction.ZMinus, Direction.ZPlus, t.Grid);
        var behind = Large.Plus(new CellOffset(0, 0, -20));

        var changes = t.Placed(t.Draft("StructureWallIron", behind, flipped));

        Assert.Empty(changes.Removed);
        Assert.True(t.View.Find(wall.Id).IsSome);
    }

    [Fact]
    public void A_frame_and_a_wall_share_a_cell()
    {
        var t = new TestBoard();
        t.Placed(t.Draft("StructureFrameIron", Large));

        var changes = t.Placed(t.Draft("StructureWallIron", Large));

        Assert.Empty(changes.Removed);
        Assert.Equal(2, t.View.At(Large).Count);
    }

    [Fact]
    public void A_later_draft_in_one_command_replaces_an_earlier_one_in_its_spot()
    {
        var t = new TestBoard();
        var old = Assert.Single(t.Placed(t.Draft("StructureCableStraight", Small)).Added);

        var changes = t.Placed(t.Draft("StructureCableCorner", Small), t.Draft("StructureCableJunction", Small));

        var added = Assert.Single(changes.Added);
        Assert.Equal(t.Catalogue.Named("StructureCableJunction").Key, ((Intent.Build)added.Intent).Target.Piece);
        Assert.Equal(new GhostRemoval(old, new RemovalCause.Replaced(added.Id)), Assert.Single(changes.Removed));
    }

    [Fact]
    public void A_remove_ghost_on_a_structure_replaces_an_earlier_one_on_it()
    {
        var t = new TestBoard();
        var structure = new StructureRef(new ReferenceId(7), t.Catalogue.Named("StructureFrameIron").Key, Large);
        var first = Assert.Single(t.Placed(Draft(new Intent.Remove(structure))).Added);

        var changes = t.Placed(Draft(new Intent.Remove(structure)));

        Assert.Equal(first.Id, Assert.Single(changes.Removed).Ghost.Id);
    }

    [Fact]
    public void A_piece_the_catalogue_does_not_hold_is_refused()
    {
        var t = new TestBoard();
        var unknown = Draft(new Intent.Build(new Placement(new PrefabKey(424242), Small, default), Option<Finish>.None));

        Assert.Equal(ReasonCodes.UnknownPiece, Refused(t.Place(unknown)).Why.Code);
    }

    [Fact]
    public void A_modded_piece_takes_the_spots_its_catalogue_facts_give()
    {
        var bundle = Modded("ModdedPipeBundle", GridSize.Small, SmallCollision.Pipes,
            new FootprintCell(default, Slot.Pipe), new FootprintCell(new CellOffset(5, 0, 0), Slot.Pipe));
        var tank = Modded("ModdedBigTank", GridSize.Large, SmallCollision.None,
            new FootprintCell(default, Slot.Centre), new FootprintCell(new CellOffset(0, 20, 0), Slot.Centre));
        var t = new TestBoard(default, bundle, tank);
        var pipe = Assert.Single(t.Placed(t.Draft("StructurePipeStraight", Small.Plus(new CellOffset(5, 0, 0)))).Added);
        var frame = Assert.Single(t.Placed(t.Draft("StructureFrameIron", Large.Plus(new CellOffset(0, 20, 0)))).Added);

        Assert.Equal(pipe.Id, Assert.Single(t.Placed(t.Draft("ModdedPipeBundle", Small)).Removed).Ghost.Id);
        Assert.Equal(frame.Id, Assert.Single(t.Placed(t.Draft("ModdedBigTank", Large)).Removed).Ghost.Id);
        Assert.Equal(ReasonCodes.CannotShare,
            Refused(t.Place(t.Draft("StructureChuteStraight", Small.Plus(new CellOffset(5, 0, 0))))).Why.Code);
    }

    [Fact]
    public void Cable_with_chute_cannot_share_a_cell()
    {
        var t = new TestBoard();
        var cable = Assert.Single(t.Placed(t.Draft("StructureCableStraight", Small)).Added);

        var refusal = Refused(t.Place(t.Draft("StructureChuteStraight", Small)));

        Assert.Equal(ReasonCodes.CannotShare, refusal.Why.Code);
        Assert.Equal(ValueList.Of(cable.Id), refusal.Ghosts);
    }

    [Fact]
    public void Cable_with_a_device_cannot_share_a_cell()
    {
        var t = new TestBoard();
        t.Placed(t.Draft("StructureAreaPowerControl", Small));

        Assert.Equal(ReasonCodes.CannotShare, Refused(t.Place(t.Draft("StructureCableStraight", Small))).Why.Code);
    }

    [Fact]
    public void Cable_and_pipe_side_by_side_collide_at_their_ends()
    {
        var t = new TestBoard();
        t.Placed(t.Draft("StructurePipeStraight", Small));

        Assert.Equal(ReasonCodes.EndsCollide, Refused(t.Place(t.Draft("StructureCableStraight", Small))).Why.Code);
    }

    [Fact]
    public void Cable_crossing_a_pipe_shares_its_cell()
    {
        var t = new TestBoard();
        t.Placed(t.Draft("StructurePipeStraight", Small, TurnSending(Direction.ZPlus, Direction.XPlus, t.Grid)));

        var changes = t.Placed(t.Draft("StructureCableStraight", Small));

        Assert.Empty(changes.Removed);
        Assert.Equal(2, t.View.At(Small).Count);
    }

    [Fact]
    public void A_fuse_sits_on_a_cable()
    {
        var t = new TestBoard();
        t.Placed(t.Draft("StructureCableStraight", Small));

        Assert.Single(t.Placed(t.Draft("StructureCableFuse", Small)).Added);
    }

    [Fact]
    public void A_long_straight_conflicts_in_a_far_cell_of_its_footprint()
    {
        var t = new TestBoard();
        t.Placed(t.Draft("StructureChuteStraight", Small.Plus(new CellOffset(0, 0, -10))));

        Assert.Equal(ReasonCodes.CannotShare, Refused(t.Place(t.Draft("StructureCableStraight3", Small))).Why.Code);
    }

    [Fact]
    public void Two_drafts_of_one_command_that_clash_are_refused()
    {
        var t = new TestBoard();

        var refusal = Refused(t.Place(t.Draft("StructureCableStraight", Small), t.Draft("StructureChuteStraight", Small)));

        Assert.Equal(ReasonCodes.CannotShare, refusal.Why.Code);
        Assert.Equal(default, t.Board.Revision);
    }

    private static NamedPiece Modded(string name, GridSize grid, SmallCollision collision, params FootprintCell[] cells) => new(
        name,
        ValueList.Of("ModdedKit" + name),
        new PieceFacts(
            PrefabHash.Piece(name),
            ValueList.Of(new KitPlace(PrefabHash.Kit("ModdedKit" + name), TestKitIdsOption())),
            grid,
            ValueList.Of(new BuildStep(Lattice.TestKit.TestKitIds.Must(StepIndex.Create(0)), Option<ItemKey>.None, Option<ItemQuantity>.None, Option<ItemKey>.None, false)),
            ValueList<ConnectionEnd>.Empty,
            new Ratings(Option<Watts>.None, Option<Kilopascals>.None, Option<Kilopascals>.None),
            Option<RuleId>.None)
        {
            Footprint = ValueList.From(cells),
            Collision = collision,
        },
        ValueMap<ItemKey, string>.Empty);

    private static KitOption TestKitIdsOption() => Lattice.TestKit.TestKitIds.Must(KitOption.Create(0));
}
