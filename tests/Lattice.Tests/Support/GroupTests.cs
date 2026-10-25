using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.Support;
using static Lattice.Tests.Support.SupportBench;

namespace Lattice.Tests.Support;

/// <summary>
/// Each census group Lattice models: with its support built the ghost passes, with its support planned it waits, without
/// it the ghost will fail with the game's own string. Every piece here is modded (a kit no base game has).
/// </summary>
public sealed class GroupTests
{
    // A small piece standing on top of the large cell (0,0,0): on the face plane y = 20 dm.
    private static readonly Cell OnTopOfOrigin = new(10, 20, 10);

    [Fact]
    public void A_floor_machine_on_a_built_frame_passes()
    {
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0))
            .Ghost("ModFloorMachine", OnTopOfOrigin, out var machine)
            .Build();

        Assert.IsType<Finding.Passes>(Verdict(Ready(Order(built)), machine));
    }

    [Fact]
    public void A_floor_machine_on_a_planned_frame_waits_for_it()
    {
        var built = Scenario()
            .Ghost("ModFloorMachine", OnTopOfOrigin, out var machine)
            .Ghost("StructureFrameIron", Large(0, 0, 0), out var frame)
            .Build();

        var report = Ready(Order(built));

        AssertWaitsFor(Verdict(report, machine), frame);
        Assert.IsType<Finding.Passes>(Verdict(report, frame));
        Assert.Equal(new[] { frame }, report.Stages[0]);
        Assert.Equal(new[] { machine }, report.Stages[1]);
    }

    [Fact]
    public void A_floor_machine_with_nothing_below_fails_as_the_game_does()
    {
        var built = Scenario().Ghost("ModFloorMachine", OnTopOfOrigin, out var machine).Build();

        AssertNeedsSupport(Verdict(Ready(Order(built)), machine), SupportGroups.FrameBelow, "PlacementRequiresFrame");
    }

    [Fact]
    public void A_floor_machine_on_a_wall_fails_because_a_wall_does_not_allow_mounting()
    {
        var built = Scenario()
            .Built("StructureWallIron", Large(0, 0, 0))
            .Ghost("ModFloorMachine", OnTopOfOrigin, out var machine)
            .Build();

        AssertNeedsSupport(Verdict(Ready(Order(built)), machine), SupportGroups.FrameBelow, "PlacementRequiresFrame");
    }

    [Fact]
    public void A_large_machine_rests_on_the_large_cell_below_its_own()
    {
        var built = Scenario()
            .Built("StructureFrame", Large(0, 0, 0))
            .Ghost("ModBigMachine", Large(0, 1, 0), out var onFrame)
            .Ghost("ModBigMachine", Large(3, 1, 0), out var inAir)
            .Build();

        var report = Ready(Order(built));

        Assert.IsType<Finding.Passes>(Verdict(report, onFrame));
        AssertNeedsSupport(Verdict(report, inAir), SupportGroups.FrameBelow, "PlacementRequiresFrame");
    }

    [Fact]
    public void Without_support_facts_for_the_piece_below_the_game_answers()
    {
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0))
            .Ghost("ModFloorMachine", OnTopOfOrigin, out var machine)
            .Build();
        var at = PlacementOf(built, machine);

        var pending = Order(built, facts: new NoSupportFacts());
        var answered = Ready(Order(built, Answers((at, new GameAnswer.Accepts())), facts: new NoSupportFacts()));

        Assert.Equal([at], Assert.IsType<OrderOutcome.Pending>(pending).Ask);
        Assert.IsType<Finding.Passes>(Verdict(answered, machine));
    }

    [Fact]
    public void A_flag_on_a_frame_passes_and_without_one_the_game_answers_for_the_ground()
    {
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0))
            .Ghost("ModFlag", OnTopOfOrigin, out var onFrame)
            .Ghost("ModFlag", new Cell(90, 20, 10), out var onGround)
            .Build();
        var ground = PlacementOf(built, onGround);

        var report = Ready(Order(built, Answers((ground, new GameAnswer.Refuses(Option.Some("needs ground"))))));

        Assert.IsType<Finding.Passes>(Verdict(report, onFrame));
        var fails = Assert.IsType<Finding.WillFail>(Verdict(report, onGround));
        Assert.Equal(ReasonCodes.GameRefused, fails.Why.Code);
        Assert.Equal(Option.Some("needs ground"), fails.Why.GameMessage);
    }

    // A light on the face plane x = 20 dm, facing +x, with the large cell (0,0,0) behind it.
    private static readonly Cell OnFace = new(20, 10, 10);

    [Fact]
    public void A_wall_light_on_a_built_frame_behind_passes()
    {
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0))
            .Ghost("ModWallLight", OnFace, out var light, Facing(Direction.XPlus))
            .Build();

        Assert.IsType<Finding.Passes>(Verdict(Ready(Order(built)), light));
    }

    [Fact]
    public void A_wall_light_on_a_planned_frame_waits_for_it()
    {
        var built = Scenario()
            .Ghost("StructureFrameIron", Large(0, 0, 0), out var frame)
            .Ghost("ModWallLight", OnFace, out var light, Facing(Direction.XPlus))
            .Build();

        AssertWaitsFor(Verdict(Ready(Order(built)), light), frame);
    }

    [Fact]
    public void A_wall_light_with_nothing_behind_fails_with_missing_support()
    {
        var built = Scenario().Ghost("ModWallLight", OnFace, out var light, Facing(Direction.XPlus)).Build();

        AssertNeedsSupport(Verdict(Ready(Order(built)), light), SupportGroups.WallMount, "InvalidMissingSupport");
    }

    [Fact]
    public void A_wall_light_facing_into_a_frame_fails_as_blocked()
    {
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0))
            .Built("StructureFrameIron", Large(1, 0, 0))
            .Ghost("ModWallLight", OnFace, out var light, Facing(Direction.XPlus))
            .Build();

        AssertNeedsSupport(Verdict(Ready(Order(built)), light), SupportGroups.WallMount, "InvalidBlocked");
    }

    // A wall in the large cell (0,0,0) whose face sits on the plane x = 20 dm: the unturned wall takes face z-.
    private static readonly int WallOnXPlus = Facing(Direction.XMinus);

    [Fact]
    public void A_wall_light_on_a_wall_that_does_not_allow_mounting_fails_as_not_mountable()
    {
        var built = Scenario()
            .Built("StructureWallIron", Large(0, 0, 0), WallOnXPlus)
            .Ghost("ModWallLight", OnFace, out var light, Facing(Direction.XPlus))
            .Build();

        AssertNeedsSupport(Verdict(Ready(Order(built)), light), SupportGroups.WallMount, "InvalidNotMountable");
    }

    [Fact]
    public void A_wall_light_on_a_mountable_wall_passes_and_one_that_requires_a_frame_fails()
    {
        var built = Scenario()
            .Built("StructureWallIron", Large(0, 0, 0), WallOnXPlus)
            .Ghost("ModWallLight", OnFace, out var light, Facing(Direction.XPlus))
            .Ghost("ModFrameLight", new Cell(20, 10, 15), out var frameLight, Facing(Direction.XPlus))
            .Build();
        var facts = Facts().With("StructureWallIron", new SupportFacts(true, false, false, ValueSet<HostRole>.Empty));

        var report = Ready(Order(built, facts: facts));

        Assert.IsType<Finding.Passes>(Verdict(report, light));
        AssertNeedsSupport(Verdict(report, frameLight), SupportGroups.WallMount, "InvalidRequiresFrame");
    }

    [Fact]
    public void A_wall_light_on_a_wall_on_another_plane_has_no_support()
    {
        var built = Scenario()
            .Built("StructureWallIron", Large(0, 0, 0))
            .Ghost("ModWallLight", OnFace, out var light, Facing(Direction.XPlus))
            .Build();

        AssertNeedsSupport(Verdict(Ready(Order(built)), light), SupportGroups.WallMount, "InvalidMissingSupport");
    }

    [Fact]
    public void A_wall_light_on_an_edge_is_the_games_to_answer()
    {
        var built = Scenario()
            .Built("StructureFrameIron", Large(0, 0, 0))
            .Ghost("ModWallLight", new Cell(20, 20, 10), out var light, Facing(Direction.XPlus))
            .Build();

        Assert.IsType<OrderOutcome.Pending>(Order(built));
    }

    private static readonly Cell SmallCell = new(5, 5, 5);

    [Fact]
    public void A_fuse_on_a_built_straight_cable_passes()
    {
        var built = Scenario()
            .Built("StructureCableStraight", SmallCell)
            .Ghost("ModFuse", SmallCell, out var fuse)
            .Build();

        Assert.IsType<Finding.Passes>(Verdict(Ready(Order(built)), fuse));
    }

    [Fact]
    public void A_fuse_on_a_planned_cable_waits_for_it()
    {
        var built = Scenario()
            .Ghost("ModFuse", SmallCell, out var fuse)
            .Ghost("StructureCableStraight", SmallCell, out var cable)
            .Build();

        AssertWaitsFor(Verdict(Ready(Order(built)), fuse), cable);
    }

    [Fact]
    public void A_fuse_with_no_cable_fails_with_must_mount_to_cable()
    {
        var built = Scenario().Ghost("ModFuse", SmallCell, out var fuse).Build();

        AssertNeedsSupport(Verdict(Ready(Order(built)), fuse), SupportGroups.OnCable, "MustMountToCable");
    }

    [Fact]
    public void A_fuse_on_a_corner_or_across_the_cable_is_blocked_by_it()
    {
        var built = Scenario()
            .Built("StructureCableCorner", SmallCell)
            .Built("StructureCableStraight", new Cell(5, 5, 50))
            .Ghost("ModFuse", SmallCell, out var onCorner)
            .Ghost("ModFuse", new Cell(5, 5, 50), out var across, Facing(Direction.XPlus))
            .Build();

        var report = Ready(Order(built));

        AssertNeedsSupport(Verdict(report, onCorner), SupportGroups.OnCable, "PlacementBlockedBySmallGrid");
        AssertNeedsSupport(Verdict(report, across), SupportGroups.OnCable, "PlacementBlockedBySmallGrid");
    }

    [Fact]
    public void A_fuse_on_a_broken_cable_fails_as_the_game_does()
    {
        var built = Scenario()
            .Built("StructureCableStraight", SmallCell, out var cable)
            .Ghost("ModFuse", SmallCell, out var fuse)
            .Build();
        var world = built.World;
        var info = world.Structure(cable.Id).Reduce(default(StructureInfo)!);
        world.Remove(cable.Id);
        world.Load(info with { State = new BuildState.Broken() });

        AssertNeedsSupport(Verdict(Ready(Order(built)), fuse), SupportGroups.OnCable, "CannotPlaceOnBrokenCable");
    }

    [Fact]
    public void A_liquid_gauge_on_a_gas_pipe_fails_on_content()
    {
        var built = Scenario()
            .Built("StructurePipeStraight", SmallCell)
            .Ghost("ModLiquidGauge", SmallCell, out var gauge)
            .Build();

        AssertNeedsSupport(Verdict(Ready(Order(built)), gauge), SupportGroups.OnPipe, "PipeContentTypeIncorrect");
    }

    [Fact]
    public void A_pipe_gauge_on_a_pipe_passes_and_without_one_fails()
    {
        var built = Scenario()
            .Built("StructurePipeStraight", SmallCell)
            .Ghost("ModPipeGauge", SmallCell, out var onPipe)
            .Ghost("ModPipeGauge", new Cell(50, 5, 5), out var alone)
            .Build();

        var report = Ready(Order(built));

        Assert.IsType<Finding.Passes>(Verdict(report, onPipe));
        AssertNeedsSupport(Verdict(report, alone), SupportGroups.OnPipe, "MustMountToPipe");
    }

    [Fact]
    public void A_filtration_unit_outside_passes_and_inside_a_room_fails()
    {
        var built = Scenario()
            .Room(1, true, Large(0, 0, 0))
            .Ghost("ModFiltration", Large(0, 0, 0), out var inside)
            .Ghost("ModFiltration", Large(20, 0, 0), out var outside)
            .Build();

        var report = Ready(Order(built));

        AssertNeedsSupport(Verdict(report, inside), SupportGroups.Outside, "Must be placed outside");
        Assert.IsType<Finding.Passes>(Verdict(report, outside));
    }

    [Fact]
    public void A_filtration_unit_near_planned_walls_waits_for_them_and_asks_the_game()
    {
        var built = Scenario()
            .Ghost("StructureWallIron", Large(1, 0, 0), out var wall)
            .Ghost("ModFiltration", Large(0, 0, 0), out var filter)
            .Build();
        var at = PlacementOf(built, filter);

        var report = Ready(Order(built, Answers((at, new GameAnswer.Accepts()))));

        AssertWaitsFor(Verdict(report, filter), wall);
    }

    [Fact]
    public void An_elevator_shaft_in_line_with_the_one_below_passes_and_turned_fails()
    {
        var built = Scenario()
            .Built("ModShaft", Large(0, 0, 0))
            .Built("ModShaft", Large(5, 0, 0))
            .Ghost("ModShaft", Large(0, 1, 0), out var inLine)
            .Ghost("ModShaft", Large(5, 1, 0), out var turned, Facing(Direction.XPlus))
            .Build();

        var report = Ready(Order(built));

        Assert.IsType<Finding.Passes>(Verdict(report, inLine));
        AssertNeedsSupport(Verdict(report, turned), SupportGroups.ElevatorShaft, "ElevatorShaftRotationFail");
    }
}
