using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.World;
using Lattice.Tests.Runs;

namespace Lattice.Tests.EntryPoint;

/// <summary>
/// The world's changes are raised inside Lattice's frame, and completion judges them in its checking slice, so every
/// cost they bring is in the frame and its budgets. A save first catches up, and a world that ends judges what it has
/// waiting before its last events, so neither loses a judgement the world already shows.
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class WorldChangesInFrameTests : IDisposable
{
    private readonly WorldBench _bench = new();

    [Fact]
    public void The_frame_raises_the_changes_the_bridge_reported_and_completion_judges_them_in_it()
    {
        var relay = _bench.Relay = new WorldChangeRelay();
        var world = _bench.Start();
        var ghost = PlacedAndSettled();

        relay.Report(new WorldChange.Registered(BuiltUnder(ghost)));
        Assert.True(world.Board.Find(ghost.Id).IsSome);

        world.Frame();

        Assert.True(world.Board.Find(ghost.Id).IsNone);
        Assert.Equal(0, relay.Waiting);
    }

    [Fact]
    public void Catching_up_before_a_save_raises_the_reported_changes_judges_them_and_raises_the_board_events()
    {
        var relay = _bench.Relay = new WorldChangeRelay();
        var world = _bench.Start();
        var ghost = PlacedAndSettled();
        var removed = new List<GhostId>();
        world.Events.Changed += changes => Removals(changes, removed);

        relay.Report(new WorldChange.Registered(BuiltUnder(ghost)));
        world.CatchUp();

        Assert.True(world.Board.Find(ghost.Id).IsNone);
        Assert.Equal(new[] { ghost.Id }, removed);
    }

    [Fact]
    public void A_world_that_ends_judges_what_completion_has_waiting_before_its_last_events()
    {
        var world = _bench.Start();
        var ghost = PlacedAndSettled();
        var seen = new List<string>();
        world.Events.Changed += changes =>
        {
            foreach (var removal in changes.Removed)
                seen.Add("removed " + removal.Ghost.Id.Value);
        };
        world.Events.BoardReset += () => seen.Add("reset");

        _bench.Runs.World.Register(BuiltUnder(ghost));
        world.End();

        Assert.Equal(new[] { "removed " + ghost.Id.Value, "reset" }, seen);
    }

    public void Dispose() => _bench.Dispose();

    private Ghost PlacedAndSettled()
    {
        var outcome = _bench.Submit(new Command.Place(ValueList.Of(_bench.Draft("ModConduitLine", RunBench.At(0, 0, 0)))));
        var ghost = Assert.Single(Assert.IsType<Outcome.Applied>(outcome).Changes.Added);
        _bench.Settle();
        return ghost;
    }

    private static StructureInfo BuiltUnder(Ghost ghost)
    {
        var target = Assert.IsType<Intent.Build>(ghost.Intent).Target;
        return new StructureInfo(new StructureRef(new ReferenceId(9000 + ghost.Id.Value), target.Piece, target.At), target.Turn,
            new BuildState.Built(), Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None);
    }

    private static void Removals(ChangeSet changes, List<GhostId> removed)
    {
        foreach (var removal in changes.Removed)
            removed.Add(removal.Ghost.Id);
    }
}
