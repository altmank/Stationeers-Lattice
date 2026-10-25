using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Ports;
using Lattice.Tests.Matching;
using Lattice.TestKit;
using Lattice.TestKit.Allocations;
using Lattice.TestKit.World;
using static Lattice.Tests.Matching.TestWorld;

namespace Lattice.Tests.BuiltWorld;

public sealed class WorldMirrorTests
{
    [Fact]
    public void Start_subscribes_before_it_scans_so_a_structure_registered_during_the_scan_is_held_once()
    {
        var w = new TestWorld();
        var during = w.Structure("StructureFrame", Large);
        var scan = new RegisteringScan(w.World, during);
        var changes = Changes(w.Mirror);

        w.Mirror.Start(w.World, scan);

        Assert.Equal(1, w.Mirror.Count);
        Assert.Equal(Option.Some(during), w.Mirror.Structure(during.Ref.Id));
        Assert.Equal(new MirrorChange.Appeared(during), Assert.Single(changes));
    }

    [Fact]
    public void A_registration_of_a_structure_the_scan_found_changes_nothing()
    {
        var w = new TestWorld();
        var frame = w.Structure("StructureFrame", Large);
        var changes = Changes(w.Mirror);
        w.Mirror.Start(w.World, new FixedScan(frame));
        var revision = w.Mirror.Revision;

        w.World.Register(frame);

        Assert.Empty(changes);
        Assert.Equal(revision, w.Mirror.Revision);
        Assert.Equal(1, w.Mirror.Count);
    }

    [Fact]
    public void Signals_after_the_scan_keep_the_mirror_as_the_world_stands()
    {
        var w = new TestWorld();
        var cable = w.Structure("StructureCableStraight", Small, state: Step(0));
        w.World.Load(cable);
        w.Mirror.Start(w.World, w.Reads);
        var blue = TestKitIds.Must(PaintColour.Create(3));
        var name = TestKitIds.Must(ThingName.Create("Main"));
        var moved = Small.Plus(new CellOffset(0, 5, 0));

        w.World.ChangeState(cable.Ref.Id, new BuildState.Built());
        w.World.Paint(cable.Ref.Id, blue);
        w.World.Rename(cable.Ref.Id, name);
        w.World.Move(ValueList.Of(new StructureMove(cable.Ref, moved, TurnOf(2))));

        var expected = cable with
        {
            Ref = cable.Ref with { At = moved },
            Turn = TurnOf(2),
            State = new BuildState.Built(),
            Colour = Option.Some(blue),
            Name = Option.Some(name),
        };
        Assert.Equal(Option.Some(expected), w.Mirror.Structure(cable.Ref.Id));
        Assert.Empty(w.Mirror.At(Small));
        Assert.Equal(ValueList.Of(expected), w.Mirror.At(moved));

        w.World.Remove(cable.Ref.Id);
        Assert.Equal(0, w.Mirror.Count);
        Assert.Empty(w.Mirror.At(moved));
        Assert.Equal(0, w.Reads.Reads);
    }

    [Fact]
    public void A_signal_naming_a_structure_the_mirror_does_not_hold_reads_that_structure_once()
    {
        var w = new TestWorld();
        w.Mirror.Start(w.World, w.Reads);
        var missed = w.Structure("StructureFrame", Large, state: Step(0));
        w.World.Load(missed);
        var changes = Changes(w.Mirror);

        w.World.ChangeState(missed.Ref.Id, new BuildState.Built());

        Assert.Equal(new[] { missed.Ref.Id }, w.Reads.ReadIds);
        var appeared = Assert.IsType<MirrorChange.Appeared>(Assert.Single(changes));
        Assert.Equal(new BuildState.Built(), appeared.Now.State);
    }

    [Fact]
    public void A_removal_of_a_structure_the_mirror_does_not_hold_is_dropped()
    {
        var w = new TestWorld();
        w.Mirror.Start(w.World, w.Reads);
        var missed = w.Structure("StructureFrame", Large);
        w.World.Load(missed);
        var changes = Changes(w.Mirror);

        w.World.Remove(missed.Ref.Id);

        Assert.Empty(changes);
        Assert.Equal(0, w.Reads.Reads);
    }

    [Fact]
    public void A_snapshot_stays_as_it_was_taken_and_is_the_same_until_the_mirror_changes()
    {
        var w = new TestWorld();
        var frame = w.Structure("StructureFrame", Large);
        w.World.Load(frame);
        w.Mirror.Start(w.World, w.Reads);

        var first = w.Mirror.Snapshot();
        Assert.Same(first, w.Mirror.Snapshot());

        w.World.Remove(frame.Ref.Id);
        var second = w.Mirror.Snapshot();

        Assert.Equal(Option.Some(frame), first.Structure(frame.Ref.Id));
        Assert.Equal(ValueList.Of(frame), first.At(Large));
        Assert.Equal(0, second.Count);
        Assert.True(second.Revision > first.Revision);
    }

    [Fact]
    public void A_structure_is_held_in_every_cell_its_catalogue_footprint_takes()
    {
        var w = new TestWorld();
        var straight = w.Structure("StructureCableStraight3", Small);
        w.World.Load(straight);
        w.Mirror.Start(w.World, w.Reads);

        Assert.True(w.Mirror.SpotsOf(straight).TryGet(out var spots));
        foreach (var spot in spots)
            Assert.Contains(straight, w.Mirror.At(spot.Cell));
    }

    [Fact]
    public void A_target_is_found_by_piece_and_cell_when_its_id_changed()
    {
        var w = new TestWorld();
        var frame = w.Structure("StructureFrame", Large);
        w.World.Load(frame);
        w.Mirror.Start(w.World, w.Reads);

        var found = w.Mirror.Find(frame.Ref with { Id = new ReferenceId(1) });

        Assert.Equal(Option.Some(frame), found);
    }

    [Fact]
    public void Repeated_reads_of_an_unchanged_mirror_allocate_nothing()
    {
        var w = new TestWorld();
        var frame = w.Structure("StructureFrame", Large);
        w.World.Load(frame);
        w.Mirror.Start(w.World, w.Reads);
        var snapshot = w.Mirror.Snapshot();

        Allocations.AssertNone(() => Read(w.Mirror, snapshot, frame), "mirror reads");
    }

    [Fact]
    public void End_stops_following_the_world()
    {
        var w = new TestWorld();
        w.Mirror.Start(w.World, w.Reads);

        w.Mirror.End();
        w.World.Register(w.Structure("StructureFrame", Large));

        Assert.Equal(0, w.Mirror.Count);
    }

    [Fact]
    public void The_spots_of_a_held_structure_are_the_spots_it_is_held_in_and_reading_them_allocates_nothing()
    {
        var w = new TestWorld();
        var straight = w.Structure("StructureCableStraight3", Small);
        w.World.Load(straight);
        w.Mirror.Start(w.World, w.Reads);

        var first = Spots(w.Mirror, straight);

        Assert.Same(first, Spots(w.Mirror, straight));
        Assert.Equal(Fresh(w, straight), first);
        Allocations.AssertNone(() => _ = w.Mirror.SpotsOf(straight), "spots of a held structure");
    }

    [Fact]
    public void A_state_paint_or_name_change_keeps_the_spots_and_both_sides_of_the_change_read_them()
    {
        var w = new TestWorld();
        var straight = w.Structure("StructureCableStraight3", Small, state: Step(0));
        w.World.Load(straight);
        w.Mirror.Start(w.World, w.Reads);
        var held = Spots(w.Mirror, straight);
        var changes = Changes(w.Mirror);

        w.World.ChangeState(straight.Ref.Id, new BuildState.Built());
        w.World.Paint(straight.Ref.Id, TestKitIds.Must(PaintColour.Create(3)));
        w.World.Rename(straight.Ref.Id, TestKitIds.Must(ThingName.Create("Main")));

        Assert.Equal(3, changes.Count);
        foreach (var change in changes)
        {
            var altered = Assert.IsType<MirrorChange.Altered>(change);
            Assert.Same(held, Spots(w.Mirror, altered.Before));
            Assert.Same(held, Spots(w.Mirror, altered.Now));
        }
    }

    [Fact]
    public void A_moved_structure_reads_its_new_spots_and_its_old_self_the_old_ones()
    {
        var w = new TestWorld();
        var straight = w.Structure("StructureCableStraight3", Small);
        w.World.Load(straight);
        w.Mirror.Start(w.World, w.Reads);
        var to = Small.Plus(new CellOffset(0, 5, 0));
        var changes = Changes(w.Mirror);

        w.World.Move(ValueList.Of(new StructureMove(straight.Ref, to, TurnOf(1))));

        var altered = Assert.IsType<MirrorChange.Altered>(Assert.Single(changes));
        Assert.Equal(Fresh(w, altered.Now), Spots(w.Mirror, altered.Now));
        Assert.Equal(Fresh(w, straight), Spots(w.Mirror, altered.Before));
        Assert.NotEqual(Fresh(w, straight), Fresh(w, altered.Now));
    }

    [Fact]
    public void A_removed_structure_still_reads_its_spots()
    {
        var w = new TestWorld();
        var straight = w.Structure("StructureCableStraight3", Small);
        w.World.Load(straight);
        w.Mirror.Start(w.World, w.Reads);
        var held = Spots(w.Mirror, straight);
        var changes = Changes(w.Mirror);

        w.World.Remove(straight.Ref.Id);

        var gone = Assert.IsType<MirrorChange.Gone>(Assert.Single(changes));
        Assert.Same(held, Spots(w.Mirror, gone.Before));
    }

    [Fact]
    public void A_structure_the_mirror_holds_otherwise_reads_the_spots_of_its_own_place_and_turn()
    {
        var w = new TestWorld();
        var straight = w.Structure("StructureCableStraight3", Small);
        w.World.Load(straight);
        w.Mirror.Start(w.World, w.Reads);
        var turned = straight with { Turn = TurnOf(1) };
        var elsewhere = straight with { Ref = straight.Ref with { At = Small.Plus(new CellOffset(0, 5, 0)) } };

        Assert.Equal(Fresh(w, turned), Spots(w.Mirror, turned));
        Assert.Equal(Fresh(w, elsewhere), Spots(w.Mirror, elsewhere));
        Assert.NotEqual(Fresh(w, straight), Fresh(w, turned));
    }

    [Fact]
    public void A_modded_piece_the_catalogue_does_not_hold_has_no_spots_held_or_not()
    {
        var w = new TestWorld();
        var modded = new StructureInfo(
            new StructureRef(new ReferenceId(77), new PrefabKey(987654321), Small), Turn.None, new BuildState.Built(),
            Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None);
        w.World.Load(modded);
        w.Mirror.Start(w.World, w.Reads);

        Assert.False(w.Mirror.SpotsOf(modded).TryGet(out _));
        Assert.False(w.Mirror.SpotsOf(modded with { Ref = modded.Ref with { Id = new ReferenceId(78) } }).TryGet(out _));
    }

    private static IReadOnlyList<SpotKey> Spots(WorldMirror mirror, StructureInfo info) =>
        mirror.SpotsOf(info).TryGet(out var spots) ? spots : throw new Xunit.Sdk.XunitException("no spots");

    private static IReadOnlyList<SpotKey> Fresh(TestWorld w, StructureInfo info) =>
        new Shapes(w.T.Catalogue, w.T.Grid).Of(new Placement(info.Ref.Piece, info.Ref.At, info.Turn))
            .Match(static shape => shape.Spots, static _ => throw new Xunit.Sdk.XunitException("no shape"));

    private static void Read(WorldMirror mirror, MirrorSnapshot snapshot, StructureInfo frame)
    {
        _ = mirror.Structure(frame.Ref.Id);
        _ = mirror.At(Large);
        _ = mirror.Find(frame.Ref);
        _ = snapshot.Structure(frame.Ref.Id);
        _ = snapshot.At(Large);
    }

    private static List<MirrorChange> Changes(WorldMirror mirror)
    {
        var changes = new List<MirrorChange>();
        mirror.Changed += changes.Add;
        return changes;
    }

    /// <summary>A scan that finds the given structures, whatever the world holds.</summary>
    private sealed class FixedScan(params StructureInfo[] found) : IWorldScan
    {
        public ValueList<StructureInfo> Structures() => ValueList.From(found);
    }

    /// <summary>A scan during which the game registers one more structure.</summary>
    private sealed class RegisteringScan(FakeWorld world, StructureInfo during) : IWorldScan
    {
        public ValueList<StructureInfo> Structures()
        {
            world.Register(during);
            return ValueList.From(new List<StructureInfo>(world.All));
        }
    }
}
