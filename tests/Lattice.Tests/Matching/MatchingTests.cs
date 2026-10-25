using System.Linq;
using Lattice.Api;
using Lattice.Core.Matching;
using Lattice.Core.Ports;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using static Lattice.Tests.Matching.TestWorld;

namespace Lattice.Tests.Matching;

public sealed class MatchingTests
{
    private static readonly Turn Quarter = TurnOf(1);

    // Build

    [Fact]
    public void A_build_ghost_is_started_by_a_part_built_structure_and_done_when_it_is_fully_built()
    {
        var w = new TestWorld();
        w.Run();
        var ghost = w.Build("StructureFrame", Large);
        var done = w.Completions();
        var frame = w.Structure("StructureFrame", Large, state: Step(0));

        w.World.Register(frame);
        Assert.Equal(new GhostState.Started(frame.Ref), w.StateOf(ghost.Id));

        w.World.ChangeStep(frame.Ref.Id, TestKitIds.Must(StepIndex.Create(1)));
        Assert.Equal(new GhostState.Started(frame.Ref), w.StateOf(ghost.Id));

        w.World.ChangeState(frame.Ref.Id, new BuildState.Built());
        w.T.Events.Flush();
        Assert.True(w.Find(ghost.Id).IsNone);
        var (id, how) = Assert.Single(done);
        Assert.Equal(ghost.Id, id);
        Assert.Equal(Option.Some(frame.Ref), how.Built);
    }

    [Fact]
    public void A_different_piece_in_the_spot_makes_a_build_ghost_stale_with_the_piece_as_its_problem()
    {
        var w = new TestWorld();
        w.Run();
        var ghost = w.Build("StructureCableStraight", Small);
        var heavy = w.Structure("StructureCableStraightH", Small);

        w.World.Register(heavy);

        var stale = Assert.IsType<GhostState.Stale>(w.StateOf(ghost.Id));
        Assert.Equal(ReasonCodes.Different, stale.Why.Code);
        Assert.Equal(Matcher.Rule, stale.Why.From);
        Assert.Equal(ValueMap.Of("structure", heavy.Ref.Id.Value.ToString()).With("piece", heavy.Ref.Piece.Hash.ToString()).With("problem", "piece"), stale.Why.Facts);
    }

    [Fact]
    public void Its_piece_in_another_turn_makes_a_build_ghost_stale_with_the_turn_as_its_problem()
    {
        var w = new TestWorld();
        w.Run();
        var ghost = w.Build("StructureCableStraight", Small);

        w.World.Register(w.Structure("StructureCableStraight", Small, Quarter));

        var stale = Assert.IsType<GhostState.Stale>(w.StateOf(ghost.Id));
        Assert.Equal("turn", stale.Why.Facts["problem"]);
    }

    [Fact]
    public void A_frame_ghost_matches_a_frame_built_in_another_turn()
    {
        var w = new TestWorld();
        w.Run();
        var ghost = w.Build("StructureFrame", Large);

        w.World.Register(w.Structure("StructureFrame", Large, TurnOf(17)));

        Assert.True(w.Find(ghost.Id).IsNone);
    }

    [Fact]
    public void A_broken_structure_of_its_piece_makes_a_build_ghost_stale()
    {
        var w = new TestWorld();
        w.Run();
        var ghost = w.Build("StructureWallIron", Large);
        var wall = w.Structure("StructureWallIron", Large, state: Step(0));
        w.World.Register(wall);

        w.World.ChangeState(wall.Ref.Id, new BuildState.Broken());

        Assert.Equal("broken", Assert.IsType<GhostState.Stale>(w.StateOf(ghost.Id)).Why.Facts["problem"]);
    }

    [Fact]
    public void A_ghost_goes_back_to_planned_when_what_stood_in_its_spot_is_gone()
    {
        var w = new TestWorld();
        w.Run();
        var ghost = w.Build("StructureCableStraight", Small);
        var other = w.Structure("StructurePipeStraight", Small);
        var started = w.Structure("StructureCableStraight", Small, state: Step(0));
        w.World.Register(started);
        w.World.Remove(started.Ref.Id);

        Assert.IsType<GhostState.Planned>(w.StateOf(ghost.Id));
        w.World.Register(other);
        Assert.IsType<GhostState.Planned>(w.StateOf(ghost.Id));
    }

    [Fact]
    public void A_build_ghost_with_a_finish_is_started_until_the_built_structure_has_it()
    {
        var w = new TestWorld();
        w.Run();
        var blue = TestKitIds.Must(PaintColour.Create(3));
        var finish = TestKitIds.Must(Finish.Create(Option.Some(blue), Option<ThingName>.None, Option<Setting>.None));
        var ghost = w.Build("StructureFrame", Large, finish: Option.Some(finish));
        var frame = w.Structure("StructureFrame", Large);

        w.World.Register(frame);
        Assert.Equal(new GhostState.Started(frame.Ref), w.StateOf(ghost.Id));

        w.World.Paint(frame.Ref.Id, blue);
        Assert.True(w.Find(ghost.Id).IsNone);
    }

    [Fact]
    public void Structures_in_other_spots_of_the_cell_leave_a_ghost_alone()
    {
        var w = new TestWorld();
        w.Run();
        var wall = w.Build("StructureWallIron", Large);
        var cable = w.Build("StructureCableStraight", Small);

        w.World.Register(w.Structure("StructureFrame", Large));
        w.World.Register(w.Structure("StructurePipeStraight", Small));

        Assert.IsType<GhostState.Planned>(w.StateOf(wall.Id));
        Assert.IsType<GhostState.Planned>(w.StateOf(cable.Id));
    }

    // Remove, swap, finish

    [Fact]
    public void A_remove_ghost_is_done_when_the_structure_is_gone()
    {
        var w = new TestWorld();
        var frame = w.Structure("StructureFrame", Large);
        w.World.Load(frame);
        w.Run();
        var ghost = w.Place(new Intent.Remove(frame.Ref));
        var done = w.Completions();

        w.World.ChangeStep(frame.Ref.Id, TestKitIds.Must(StepIndex.Create(1)));
        Assert.IsType<GhostState.Planned>(w.StateOf(ghost.Id));

        w.World.Remove(frame.Ref.Id);
        w.T.Events.Flush();
        Assert.True(w.Find(ghost.Id).IsNone);
        Assert.Equal(Option<StructureRef>.None, Assert.Single(done).How.Built);
    }

    [Fact]
    public void A_swap_ghost_is_done_when_the_old_is_gone_and_the_new_fully_built()
    {
        var w = new TestWorld();
        var old = w.Structure("StructureCableStraight", Small);
        w.World.Load(old);
        w.Run();
        var ghost = w.Place(new Intent.Swap(old.Ref, w.Placement("StructureCableStraightH", Small), Option<Finish>.None));

        w.World.Remove(old.Ref.Id);
        Assert.IsType<GhostState.Planned>(w.StateOf(ghost.Id));

        var heavy = w.Structure("StructureCableStraightH", Small);
        w.World.Register(heavy);
        Assert.True(w.Find(ghost.Id).IsNone);
    }

    [Fact]
    public void A_swap_ghost_whose_new_spot_holds_the_wrong_piece_is_stale()
    {
        var w = new TestWorld();
        var old = w.Structure("StructureCableStraight", Small);
        w.World.Load(old);
        w.Run();
        var ghost = w.Place(new Intent.Swap(old.Ref, w.Placement("StructureCableStraightH", Small), Option<Finish>.None));

        w.World.Remove(old.Ref.Id);
        w.World.Register(w.Structure("StructureCableStraight", Small));

        Assert.Equal("piece", Assert.IsType<GhostState.Stale>(w.StateOf(ghost.Id)).Why.Facts["problem"]);
    }

    [Fact]
    public void A_finish_ghost_is_done_when_colour_and_name_match()
    {
        var w = new TestWorld();
        var wall = w.Structure("StructureWallIron", Large);
        w.World.Load(wall);
        w.Run();
        var red = TestKitIds.Must(PaintColour.Create(4));
        var name = TestKitIds.Must(ThingName.Create("Airlock"));
        var ghost = w.Place(new Intent.FinishOnly(wall.Ref, TestKitIds.Must(Finish.Create(Option.Some(red), Option.Some(name), Option<Setting>.None))));

        w.World.Paint(wall.Ref.Id, red);
        Assert.IsType<GhostState.Planned>(w.StateOf(ghost.Id));

        w.World.Rename(wall.Ref.Id, name);
        Assert.True(w.Find(ghost.Id).IsNone);
    }

    [Fact]
    public void A_finish_ghost_whose_target_is_destroyed_while_playing_leaves_with_target_gone()
    {
        var w = new TestWorld();
        var wall = w.Structure("StructureWallIron", Large);
        w.World.Load(wall);
        w.Run();
        var ghost = w.Place(new Intent.FinishOnly(wall.Ref, TestKitIds.Must(Finish.Create(Option.Some(TestKitIds.Must(PaintColour.Create(4))), Option<ThingName>.None, Option<Setting>.None))));
        var sets = w.Recorded();

        w.World.Remove(wall.Ref.Id);
        w.T.Events.Flush();

        var removal = Assert.Single(sets.SelectMany(s => s.Removed));
        Assert.Equal(ghost.Id, removal.Ghost.Id);
        Assert.IsType<RemovalCause.TargetGone>(removal.Cause);
    }

    // The pass at load

    [Fact]
    public void The_pass_at_load_judges_every_ghost_once_against_the_world()
    {
        var w = new TestWorld();
        var built = w.Build("StructureFrame", Large);
        var started = w.Build("StructureFrame", Large.Plus(new CellOffset(20, 0, 0)));
        var wrong = w.Build("StructureCableStraight", Small);
        var planned = w.Build("StructurePipeStraight", Small.Plus(new CellOffset(0, 50, 0)));
        w.World.Load(w.Structure("StructureFrame", Large, TurnOf(5)));
        var partBuilt = w.Structure("StructureFrame", Large.Plus(new CellOffset(20, 0, 0)), state: Step(1));
        w.World.Load(partBuilt);
        w.World.Load(w.Structure("StructureCableStraightH", Small));

        var report = w.Run();

        Assert.Equal((4, 1, 1, 1, 0, 0), (report.Judged, report.Done, report.Started, report.Stale, report.TargetGone, report.Unjudged));
        Assert.True(w.Find(built.Id).IsNone);
        Assert.Equal(new GhostState.Started(partBuilt.Ref), w.StateOf(started.Id));
        Assert.IsType<GhostState.Stale>(w.StateOf(wrong.Id));
        Assert.IsType<GhostState.Planned>(w.StateOf(planned.Id));
    }

    [Fact]
    public void At_load_a_swap_or_finish_ghost_whose_target_is_gone_is_deleted_with_an_event()
    {
        var w = new TestWorld();
        var swapped = w.Structure("StructureCableStraight", Small);
        var painted = w.Structure("StructureWallIron", Large);
        var removed = w.Structure("StructureFrame", Large.Plus(new CellOffset(20, 0, 0)));
        var swap = w.Place(new Intent.Swap(swapped.Ref, w.Placement("StructureCableStraightH", Small), Option<Finish>.None));
        var finish = w.Place(new Intent.FinishOnly(painted.Ref, TestKitIds.Must(Finish.Create(Option.Some(TestKitIds.Must(PaintColour.Create(1))), Option<ThingName>.None, Option<Setting>.None))));
        var remove = w.Place(new Intent.Remove(removed.Ref));
        w.T.Events.Flush();
        var sets = w.Recorded();

        var report = w.Run();
        w.T.Events.Flush();

        Assert.Equal(2, report.TargetGone);
        var removals = sets.SelectMany(s => s.Removed).ToDictionary(r => r.Ghost.Id, r => r.Cause);
        Assert.IsType<RemovalCause.TargetGone>(removals[swap.Id]);
        Assert.IsType<RemovalCause.TargetGone>(removals[finish.Id]);
        Assert.IsType<RemovalCause.Done>(removals[remove.Id]);
    }

    [Fact]
    public void At_load_a_swap_already_done_is_done_not_deleted()
    {
        var w = new TestWorld();
        var gone = w.Structure("StructureCableStraight", Small);
        var swap = w.Place(new Intent.Swap(gone.Ref, w.Placement("StructureCableStraightH", Small), Option<Finish>.None));
        w.World.Load(w.Structure("StructureCableStraightH", Small));
        w.T.Events.Flush();
        var sets = w.Recorded();

        w.Run();
        w.T.Events.Flush();

        Assert.IsType<RemovalCause.Done>(Assert.Single(sets.SelectMany(s => s.Removed)).Cause);
    }

    [Fact]
    public void After_load_the_world_is_read_only_for_what_an_event_names()
    {
        var w = new TestWorld();
        var frame = w.Structure("StructureFrame", Large, state: Step(0));
        w.World.Load(frame);
        w.Build("StructureFrame", Large);
        w.Build("StructureCableStraight", Small);
        w.Run();
        Assert.Equal((1, 0), (w.Reads.Scans, w.Reads.Reads));

        w.World.Register(w.Structure("StructureCableStraight", Small, state: Step(0)));
        w.World.ChangeState(frame.Ref.Id, new BuildState.Built());
        w.World.Paint(frame.Ref.Id, TestKitIds.Must(PaintColour.Create(2)));
        w.World.Remove(frame.Ref.Id);
        w.T.Events.Flush();
        Assert.Equal((1, 0), (w.Reads.Scans, w.Reads.Reads));

        var missed = w.Structure("StructurePipeStraight", Small.Plus(new CellOffset(0, 50, 0)), state: Step(0));
        w.World.Load(missed);
        w.World.ChangeState(missed.Ref.Id, new BuildState.Built());
        Assert.Equal(new[] { missed.Ref.Id }, w.Reads.ReadIds);
        Assert.Equal(1, w.Reads.Scans);
    }

    [Fact]
    public void A_ghost_placed_where_its_piece_already_stands_is_done_at_the_next_flush()
    {
        var w = new TestWorld();
        w.World.Load(w.Structure("StructureCableStraight", Small));
        w.Run();
        var ghost = w.Build("StructureCableStraight", Small);

        w.T.Events.Flush();

        Assert.True(w.Find(ghost.Id).IsNone);
    }

    // Pieces with no direction

    [Fact]
    public void Pieces_with_no_direction_are_worked_out_from_catalogue_facts()
    {
        var w = new TestWorld(Modded("ModdedCrate", GridSize.Large, new FootprintCell(default, Slot.Centre)));

        var report = w.TurnFree.Report();

        var names = report.Select(piece => w.T.Catalogue.NameOf(piece)).ToHashSet();
        Assert.Contains("StructureFrame", names);
        Assert.Contains("StructureFrameIron", names);
        Assert.Contains("ModdedCrate", names);
        Assert.DoesNotContain("StructureWallIron", names);
        Assert.DoesNotContain("StructureCableStraight", names);
        Assert.DoesNotContain("StructureAreaPowerControl", names);
    }

    // Modded pieces

    [Fact]
    public void A_modded_piece_of_an_unknown_class_matches_by_its_catalogue_facts()
    {
        var bundle = Modded("ModdedPipeBundle", GridSize.Small,
            new FootprintCell(default, Slot.Pipe), new FootprintCell(new CellOffset(5, 0, 0), Slot.Pipe));
        var w = new TestWorld(bundle);
        w.Run();
        var ghost = w.Build("ModdedPipeBundle", Small);
        var pipe = w.Build("StructurePipeStraight", Small.Plus(new CellOffset(0, 5, 0)));

        w.World.Register(w.Structure("StructurePipeStraight", Small.Plus(new CellOffset(5, 0, 0))));
        Assert.Equal("piece", Assert.IsType<GhostState.Stale>(w.StateOf(ghost.Id)).Why.Facts["problem"]);

        w.World.Remove(w.World.All.Single().Ref.Id);
        w.World.Register(w.Structure("ModdedPipeBundle", Small, state: Step(0)));
        Assert.IsType<GhostState.Started>(w.StateOf(ghost.Id));
        Assert.IsType<GhostState.Planned>(w.StateOf(pipe.Id));
    }

    // A ghost's own structure standing as planned decides, whatever else shares its spots and whichever came first:
    // the game accepted both, as in a large real base (Vulcan2_4) where such overlaps stand.

    [Fact]
    public void A_ghost_is_done_when_its_own_structure_stands_though_an_overlapping_neighbour_came_first()
    {
        var w = new TestWorld(Bundle());
        w.Run();
        var ghost = w.Build("ModdedPipeBundle", Small);

        w.World.Register(w.Structure("StructurePipeStraight", Small.Plus(new CellOffset(5, 0, 0))));
        Assert.IsType<GhostState.Stale>(w.StateOf(ghost.Id));
        w.World.Register(w.Structure("ModdedPipeBundle", Small));

        Assert.True(w.Find(ghost.Id).IsNone);
    }

    [Fact]
    public void A_ghost_is_done_when_its_own_structure_stands_and_an_overlapping_neighbour_comes_after()
    {
        var w = new TestWorld(Bundle());
        w.Run();
        var ghost = w.Build("ModdedPipeBundle", Small);

        w.World.Register(w.Structure("ModdedPipeBundle", Small));
        w.World.Register(w.Structure("StructurePipeStraight", Small.Plus(new CellOffset(5, 0, 0))));

        Assert.True(w.Find(ghost.Id).IsNone);
    }

    [Fact]
    public void The_pass_at_load_finds_a_ghost_done_when_its_own_structure_stands_beside_an_overlapping_one()
    {
        var w = new TestWorld(Bundle());
        var ghost = w.Build("ModdedPipeBundle", Small);
        w.World.Load(w.Structure("StructurePipeStraight", Small.Plus(new CellOffset(5, 0, 0))));
        w.World.Load(w.Structure("ModdedPipeBundle", Small));

        var report = w.Run();

        Assert.Equal((1, 0), (report.Done, report.Stale));
        Assert.True(w.Find(ghost.Id).IsNone);
    }

    [Fact]
    public void Its_own_piece_part_built_beside_an_overlapping_neighbour_starts_a_ghost()
    {
        var w = new TestWorld(Bundle());
        w.Run();
        var ghost = w.Build("ModdedPipeBundle", Small);

        w.World.Register(w.Structure("StructurePipeStraight", Small.Plus(new CellOffset(5, 0, 0))));
        w.World.Register(w.Structure("ModdedPipeBundle", Small, state: Step(0)));

        Assert.IsType<GhostState.Started>(w.StateOf(ghost.Id));
    }

    [Fact]
    public void A_structure_of_a_piece_the_catalogue_does_not_hold_counts_in_every_spot_of_its_cell()
    {
        var w = new TestWorld();
        w.Run();
        var ghost = w.Build("StructureCableStraight", Small);

        var unknown = new StructureInfo(new StructureRef(new ReferenceId(9), new PrefabKey(424242), Small), default,
            new BuildState.Built(), Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None);
        w.World.Register(unknown);

        Assert.Equal("piece", Assert.IsType<GhostState.Stale>(w.StateOf(ghost.Id)).Why.Facts["problem"]);
    }

    private static NamedPiece Bundle() => Modded("ModdedPipeBundle", GridSize.Small,
        new FootprintCell(default, Slot.Pipe), new FootprintCell(new CellOffset(5, 0, 0), Slot.Pipe));

    private static NamedPiece Modded(string name, GridSize grid, params FootprintCell[] cells) => new(
        name,
        ValueList.Of("ModdedKit" + name),
        new PieceFacts(
            PrefabHash.Piece(name),
            ValueList.Of(new KitPlace(PrefabHash.Kit("ModdedKit" + name), TestKitIds.Must(KitOption.Create(0)))),
            grid,
            ValueList.From(new[]
            {
                new BuildStep(TestKitIds.Must(StepIndex.Create(0)), Option<ItemKey>.None, Option<ItemQuantity>.None, Option<ItemKey>.None, false),
                new BuildStep(TestKitIds.Must(StepIndex.Create(1)), Option<ItemKey>.None, Option<ItemQuantity>.None, Option<ItemKey>.None, false),
            }),
            ValueList<ConnectionEnd>.Empty,
            new Ratings(Option<Watts>.None, Option<Kilopascals>.None, Option<Kilopascals>.None),
            Option<RuleId>.None)
        {
            Footprint = ValueList.From(cells),
            Collision = grid == GridSize.Small ? SmallCollision.Pipes : SmallCollision.None,
        },
        ValueMap<ItemKey, string>.Empty);
}
