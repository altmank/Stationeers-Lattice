using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Analysis;
using Lattice.Core.Ports;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.World;
using Lattice.Tests.Board;

namespace Lattice.Tests.Analysis;

/// <summary>A fake world with the network temperatures, loads and ratings the fake does not derive, set by hand.</summary>
internal sealed class ReadWorld(FakeWorld world) : IWorldView
{
    public Dictionary<NetworkRef, Kelvin> Heat { get; } = [];

    public Dictionary<NetworkRef, (Watts Load, Watts Rating)> Loads { get; } = [];

    public Option<StructureInfo> Structure(ReferenceId id) => world.Structure(id);

    public ValueList<StructureInfo> At(Cell cell) => world.At(cell);

    public Option<NetworkRef> NetworkOf(ReferenceId id) => world.NetworkOf(id);

    public Option<NetworkInfo> Network(NetworkRef network) => world.Network(network).Map(info =>
    {
        if (Heat.TryGetValue(network, out var kelvin))
            info = info with { Temperature = Option.Some(kelvin) };
        if (Loads.TryGetValue(network, out var load))
            info = info with { Load = Option.Some(load.Load), Rating = Option.Some(load.Rating) };
        return info;
    });

    public Option<RoomRef> RoomAt(Cell cell) => world.RoomAt(cell);

    public Option<RoomInfo> Room(RoomRef room) => world.Room(room);

    public Kilopascals PressureDifference(Cell cell, Direction face) => world.PressureDifference(cell, face);

    public bool Terrain(Cell large) => world.Terrain(large);
}

/// <summary>
/// A world and a board of modded pieces only: a wall panel on its cell's x+ face, a block filling its cell (both block
/// air), a power wire and a conduit of a kind the game does not have, a gas tube and a vent (vents=yes). Large cells
/// centre on odd metres; small cells are 5 dm apart from the centre of large cell (0, 0, 0).
/// </summary>
internal sealed class EffectsBench
{
    public const string Text = """
        lattice-catalogue 3
        piece ModPanel kit=ItemModPanelKit option=0 grid=large
          step material=ItemModPanelKit*1 exit=ItemWrench air=yes gravity=yes
          cell 0 0 0 face-x+

        piece ModBlock kit=ItemModBlockKit option=0 grid=large
          step material=ItemModBlockKit*1 exit=ItemWrench air=yes gravity=yes
          cell 0 0 0 centre

        piece ModWire kit=ItemModWire option=0 grid=small
          step material=ItemModWire*1 exit=ItemWireCutters air=no
          end 0 0 0 x+ power
          end 0 0 0 x- power
          cell 0 0 0 cable
          collision 2

        piece ModConduit kit=ItemModConduit option=0 grid=small
          step material=ItemModConduit*1 exit=ItemWireCutters air=no
          end 0 0 0 x+ mod:ModConduitNetwork:4096
          end 0 0 0 x- mod:ModConduitNetwork:4096
          cell 0 0 0 cable
          collision 2

        piece ModTube kit=ItemModTube option=0 grid=small pipe=gas
          step material=ItemModTube*1 exit=ItemWrench air=no
          end 0 0 0 x+ gas
          end 0 0 0 x- gas
          cell 0 0 0 pipe
          collision 1

        piece ModVent kit=ItemModVentKit option=0 grid=small pipe=gas vents=yes
          step material=ItemModVentKit*1 exit=ItemWrench air=no
          end 0 0 0 x- gas
          cell 0 0 0 pipe
          collision 1
        """;

    private long _nextStructure = 1000;
    private long _nextRoom;

    public EffectsBench()
    {
        var modded = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(Text), "effects.lcat")).Pieces.ToArray();
        Boards = new TestBoard(default, modded);
        Fake = new FakeWorld(Pieces);
        World = new ReadWorld(Fake);
    }

    public TestBoard Boards { get; }

    public FakeCatalogue Pieces => Boards.Catalogue;

    public FakeWorld Fake { get; }

    public ReadWorld World { get; }

    public List<GhostId> Plan { get; } = [];

    public static Cell Large(int i, int j, int k) => new(110 + 20 * i, 110 + 20 * j, 110 + 20 * k);

    public static Cell Small(int x) => new(110 + 5 * x, 110, 110);

    public static GasKind Gas(string name) => TestKitIds.Must(GasKind.Create(name));

    public static ValueList<GasHeld> Holding(string gas, double moles) =>
        ValueList.Of(new GasHeld(Gas(gas), new Moles(moles), new Joules(moles * 6000.0)));

    public PrefabKey Key(string piece) => Pieces.Named(piece).Key;

    public StructureRef Real(string piece, Cell at)
    {
        var structure = new StructureRef(new ReferenceId(++_nextStructure), Key(piece), at);
        Fake.Load(new StructureInfo(structure, default, new BuildState.Built(), Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None));
        return structure;
    }

    /// <summary>Blocks filling every large cell next to the cell but the ones the given ways.</summary>
    public void WallIn(Cell large, params Direction[] open)
    {
        for (var d = Direction.XPlus; d <= Direction.ZMinus; d++)
            if (!open.Contains(d))
                Real("ModBlock", Boards.Grid.Neighbour(large, d, GridSize.Large));
    }

    public RoomRef Room(params Cell[] cells)
    {
        var room = new RoomRef(++_nextRoom);
        Fake.SetRoom(new RoomInfo(room, ValueList.From(cells), true, Option.Some(Holding("oxygen", 10))));
        return room;
    }

    public NetworkRef NetworkOf(StructureRef structure) => Fake.NetworkOf(structure.Id).TryGet(out var network)
        ? network
        : throw new Xunit.Sdk.XunitException("no network");

    public GhostId Build(string piece, Cell at) => Place(new Intent.Build(new Placement(Key(piece), at, default), Option<Finish>.None));

    public GhostId Remove(StructureRef target) => Place(new Intent.Remove(target));

    public GhostId Paint(StructureRef target) =>
        Place(new Intent.FinishOnly(target, TestKitIds.Must(Finish.Create(Option.Some(TestKitIds.Must(PaintColour.Create(3))), Option<ThingName>.None, Option<Setting>.None))));

    public List<Ghost> Ghosts()
    {
        var ghosts = new List<Ghost>();
        foreach (var id in Plan)
            if (Boards.View.Find(id).TryGet(out var ghost))
                ghosts.Add(ghost);
        return ghosts;
    }

    public AnalysisReport Effects() => Effects(GameRooms.Default);

    public AnalysisReport Effects(GameRooms rooms) =>
        new PlanEffects(Pieces, Boards.Grid, rooms).Of(Ghosts(), Boards.View, World, ValueList<AnalyzerFact>.Empty);

    private GhostId Place(Intent intent)
    {
        var id = TestBoard.Applied(Boards.Place(TestBoard.Draft(intent))).Added[0].Id;
        Plan.Add(id);
        return id;
    }
}
