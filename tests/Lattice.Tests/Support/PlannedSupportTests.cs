using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.Support;
using static Lattice.Tests.Support.SupportBench;

namespace Lattice.Tests.Support;

/// <summary>
/// Planned support on the wall-mount path and on the game's path: a device on a frame ghost waits for it, on a built
/// frame passes, on nothing or off the faces fails with Lattice's own reason; where the game's check answers, a large
/// ghost around a small piece makes it wait, and one further off does not.
/// </summary>
public sealed class PlannedSupportTests
{
    // The frame's large cell (0, 0, 0) centres on 10 dm; its +X face plane is x = 20 dm.
    private static readonly Cell OnPlusXFace = new(20, 10, 10);

    // The forward that puts a device's back (its local -Z) into the frame from its +X face.
    private static readonly int BackToFrame = Facing(Direction.XPlus);

    [Fact]
    public void A_device_on_a_frame_ghosts_face_with_its_back_to_it_waits_for_the_frame()
    {
        var built = Scenario()
            .Ghost("StructureFrameIron", Large(0, 0, 0), out var frame)
            .Ghost("ModWallLight", OnPlusXFace, out var light, BackToFrame)
            .Build();

        AssertWaitsFor(Verdict(Ready(Order(built)), light), frame);
    }

    [Fact]
    public void The_same_device_on_a_built_frame_passes()
    {
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0))
            .Ghost("ModWallLight", OnPlusXFace, out var light, BackToFrame)
            .Build();

        Assert.IsType<Finding.Passes>(Verdict(Ready(Order(built)), light));
    }

    [Fact]
    public void The_same_device_on_nothing_fails_with_missing_support()
    {
        var built = Scenario().Ghost("ModWallLight", OnPlusXFace, out var light, BackToFrame).Build();

        AssertNeedsSupport(Verdict(Ready(Order(built)), light), SupportGroups.WallMount, "InvalidMissingSupport");
    }

    [Fact]
    public void A_device_facing_away_from_a_frame_ghost_has_empty_air_behind_and_fails_with_missing_support()
    {
        var built = Scenario()
            .Ghost("StructureFrameIron", Large(0, 0, 0), out _)
            .Ghost("ModWallLight", OnPlusXFace, out var light, Facing(Direction.XMinus))
            .Build();

        AssertNeedsSupport(Verdict(Ready(Order(built)), light), SupportGroups.WallMount, "InvalidMissingSupport");
    }

    [Theory]
    [InlineData(25, 10, 10)]
    [InlineData(-5, 10, 10)]
    [InlineData(10, 25, 10)]
    [InlineData(25, 25, 25)]
    public void A_device_on_no_face_plane_fails_with_missing_support_without_asking_the_game(int x, int y, int z)
    {
        var built = Scenario()
            .Ghost("StructureFrameIron", Large(0, 0, 0), out _)
            .Ghost("ModWallLight", new Cell(x, y, z), out var light, BackToFrame)
            .Build();

        var outcome = Order(built);

        AssertNeedsSupport(Verdict(Ready(outcome), light), SupportGroups.WallMount, "InvalidMissingSupport");
    }

    [Fact]
    public void A_device_on_a_frame_ghosts_edge_waits_for_it_while_the_game_refuses_on_the_built_world()
    {
        var built = Scenario()
            .Ghost("StructureFrameIron", Large(0, 0, 0), out var frame)
            .Ghost("ModWallLight", new Cell(20, 20, 10), out var light, BackToFrame)
            .Build();
        var at = PlacementOf(built, light);

        var report = Ready(Order(built, Answers((at, new GameAnswer.Refuses(Option.Some("Placement requires support"))))));

        AssertWaitsFor(Verdict(report, light), frame);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void On_a_built_frames_edge_the_games_answer_decides(bool accepts)
    {
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0))
            .Ghost("ModWallLight", new Cell(20, 20, 10), out var light, BackToFrame)
            .Build();
        var at = PlacementOf(built, light);
        GameAnswer answer = accepts ? new GameAnswer.Accepts() : new GameAnswer.Refuses(Option.Some("Placement requires support"));

        var finding = Verdict(Ready(Order(built, Answers((at, answer)))), light);

        if (accepts)
            Assert.IsType<Finding.Passes>(finding);
        else
            Assert.Equal(ReasonCodes.GameRefused, Assert.IsType<Finding.WillFail>(finding).Why.Code);
    }

    [Theory]
    [InlineData("ModOddity")]
    [InlineData("ModGadget")]
    public void A_small_piece_the_game_answers_for_waits_for_a_frame_ghost_in_the_large_cell_beside_it(string piece)
    {
        var built = Scenario()
            .Ghost("StructureFrameIron", Large(0, 0, 0), out var frame)
            .Ghost(piece, new Cell(25, 10, 10), out var small)
            .Build();
        var at = PlacementOf(built, small);

        var report = Ready(Order(built, Answers((at, new GameAnswer.Refuses(Option<string>.None)))));

        AssertWaitsFor(Verdict(report, small), frame);
    }

    [Fact]
    public void A_frame_ghost_two_large_cells_off_does_not_make_it_wait()
    {
        var built = Scenario()
            .Ghost("StructureFrameIron", Large(3, 0, 0), out _)
            .Ghost("ModOddity", new Cell(25, 10, 10), out var small)
            .Build();
        var at = PlacementOf(built, small);

        var report = Ready(Order(built, Answers((at, new GameAnswer.Refuses(Option<string>.None)))));

        Assert.Equal(ReasonCodes.GameRefused, Assert.IsType<Finding.WillFail>(Verdict(report, small)).Why.Code);
    }
}
