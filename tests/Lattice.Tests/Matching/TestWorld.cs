using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Matching;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Ports;
using Lattice.Tests.Board;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.World;

namespace Lattice.Tests.Matching;

/// <summary>
/// A board, a fake world, its mirror and the completion watcher over the hand catalogue (plus extra pieces). Every read
/// the mirror makes of the world goes through <see cref="Reads"/>, which counts them.
/// </summary>
internal sealed class TestWorld
{
    public static readonly Cell Small = new(100, 100, 100);
    public static readonly Cell Large = new(10, 10, 10);

    private long _nextId = 1000;

    public TestWorld(params NamedPiece[] extraPieces)
    {
        T = new TestBoard(default, extraPieces);
        World = new FakeWorld(T.Catalogue);
        Reads = new CountingWorld(World);
        var shapes = new Shapes(T.Catalogue, T.Grid);
        Mirror = new WorldMirror(shapes, Reads);
        TurnFree = new TurnFreePieces(T.Catalogue, T.Grid, shapes);
        Watcher = new CompletionWatcher(T.Board, Mirror, TurnFree, T.Time);
    }

    public TestBoard T { get; }

    public FakeWorld World { get; }

    public CountingWorld Reads { get; }

    public WorldMirror Mirror { get; }

    public TurnFreePieces TurnFree { get; }

    public CompletionWatcher Watcher { get; }

    /// <summary>Starts the mirror (subscribe, then scan) and runs the pass at load.</summary>
    public MatchReport Run()
    {
        Mirror.Start(World, Reads);
        return Watcher.Load();
    }

    public Ghost Place(Intent intent) => Assert.Single(T.Placed(TestBoard.Draft(intent)).Added);

    public Ghost Build(string piece, Cell at, Turn turn = default, Option<Finish> finish = default) =>
        Place(new Intent.Build(Placement(piece, at, turn), finish));

    public Placement Placement(string piece, Cell at, Turn turn = default) => new(T.Catalogue.Named(piece).Key, at, turn);

    public StructureInfo Structure(string piece, Cell at, Turn turn = default, BuildState? state = null) =>
        new(new StructureRef(new ReferenceId(++_nextId), T.Catalogue.Named(piece).Key, at), turn, state ?? new BuildState.Built(),
            Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None);

    public static BuildState Step(int index) => new BuildState.Building(TestKitIds.Must(StepIndex.Create(index)));

    public static Turn TurnOf(int index) => TestKitIds.Must(Lattice.Api.Turn.Create(index));

    public Option<Ghost> Find(GhostId id) => T.View.Find(id);

    public GhostState StateOf(GhostId id) => T.View.Find(id).TryGet(out var ghost) ? ghost.State : throw new System.InvalidOperationException($"ghost {id} left the board");

    /// <summary>Every change set the board raises from here on, after each flush.</summary>
    public List<ChangeSet> Recorded()
    {
        var sets = new List<ChangeSet>();
        T.Events.Changed += sets.Add;
        return sets;
    }

    public List<(GhostId Ghost, Completion How)> Completions()
    {
        var done = new List<(GhostId, Completion)>();
        T.Events.Completed += (ghost, how) => done.Add((ghost, how));
        return done;
    }
}

/// <summary>The fake world's reads and scan, counted.</summary>
internal sealed class CountingWorld(FakeWorld world) : IWorldView, IWorldScan
{
    public int Scans { get; private set; }

    public int Reads { get; private set; }

    public List<ReferenceId> ReadIds { get; } = new();

    public ValueList<StructureInfo> Structures()
    {
        Scans++;
        var all = new List<StructureInfo>(world.All);
        all.Sort(static (a, b) => a.Ref.Id.Value.CompareTo(b.Ref.Id.Value));
        return ValueList.From(all);
    }

    public Option<StructureInfo> Structure(ReferenceId id)
    {
        Reads++;
        ReadIds.Add(id);
        return world.Structure(id);
    }

    public ValueList<StructureInfo> At(Cell cell)
    {
        Reads++;
        return world.At(cell);
    }

    public Option<NetworkRef> NetworkOf(ReferenceId id)
    {
        Reads++;
        return world.NetworkOf(id);
    }

    public Option<NetworkInfo> Network(NetworkRef network)
    {
        Reads++;
        return world.Network(network);
    }

    public Option<RoomRef> RoomAt(Cell cell)
    {
        Reads++;
        return world.RoomAt(cell);
    }

    public Option<RoomInfo> Room(RoomRef room)
    {
        Reads++;
        return world.Room(room);
    }

    public Kilopascals PressureDifference(Cell cell, Direction face)
    {
        Reads++;
        return world.PressureDifference(cell, face);
    }

    public bool Terrain(Cell large)
    {
        Reads++;
        return world.Terrain(large);
    }
}
