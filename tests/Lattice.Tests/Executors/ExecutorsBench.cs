using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using Lattice.Core.Executors;
using Lattice.Core.Ports;
using Lattice.Tests.Board;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;

namespace Lattice.Tests.Executors;

/// <summary>Game time a test moves by hand.</summary>
internal sealed class ManualGameClock : IGameClock
{
    public GameTime Now { get; private set; } = new(100);

    public void Advance(double seconds) => Now = new GameTime(Now.Seconds + seconds);
}

/// <summary>
/// The executors module over a board (<see cref="TestBoard"/>) and a hand-moved game clock, recording every
/// <see cref="IGhostEvents.Blocked"/> and <see cref="IGhostEvents.StatusChanged"/> the board's flushes raise.
/// </summary>
internal sealed class ExecutorsBench
{
    public static readonly Maker OtherMaker = TestKitIds.Must(Maker.Create(TestKitIds.Mod, "other-maker"));

    private int _nextCell;

    public ExecutorsBench(WorldSeat seat = WorldSeat.Host, params NamedPiece[] extraPieces)
    {
        T = new TestBoard(default, extraPieces);
        Executors = new WorldExecutors(T.Board, Clock, seat);
        T.Events.Blocked += (maker, ghosts) => Notices.Add((maker, ghosts));
        T.Events.StatusChanged += ghost => StatusChanges.Add(ghost);
    }

    public TestBoard T { get; }

    public ManualGameClock Clock { get; } = new();

    public WorldExecutors Executors { get; }

    public List<(Maker Maker, ValueList<BlockedGhost> Ghosts)> Notices { get; } = new();

    public List<GhostId> StatusChanges { get; } = new();

    /// <summary>A build ghost of the piece in a fresh large cell, made by the maker given (the board test maker by default).</summary>
    public GhostId Ghost(string piece = "StructureFrame", Maker? maker = null)
    {
        var at = new Cell(10 + 2 * _nextCell++, 10, 10);
        var draft = T.Draft(piece, at) with { MadeBy = maker ?? TestBoard.TestMaker };
        return TestBoard.Applied(T.Place(draft)).Added[0].Id;
    }

    public IExecutorHandle Register(string name) => TestKitIds.Must(Executors.Register(Info(name)));

    public static ExecutorInfo Info(string name) =>
        new(TestKitIds.Must(ExecutorId.Create(TestKitIds.Mod, name)), TestKitIds.Must(DisplayName.Create(name)));

    public static Lease Seconds(double seconds) => TestKitIds.Must(Lease.Create(seconds));

    public void Flush() => T.Events.Flush();

    public static NamedPiece[] Pieces(string text) =>
        [.. TestKitIds.MustRead(CatalogueFile.Read(new StringReader("lattice-catalogue 3\n" + text), "inline.lcat")).Pieces];
}
