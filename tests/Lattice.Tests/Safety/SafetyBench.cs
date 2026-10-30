using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Openings;
using Lattice.Core.Ports;
using Lattice.Core.Safety;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.World;
using Lattice.Tests.Board;

namespace Lattice.Tests.Safety;

/// <summary>Build safety facts set by hand, as the catalogue and the world view will read them.</summary>
internal sealed class TestSafetyFacts : ISafetyFacts
{
    public Dictionary<PrefabKey, Keeps> Keeping { get; } = [];

    public HashSet<PrefabKey> Suppliers { get; } = [];

    public Dictionary<PrefabKey, Direction> Controls { get; } = [];

    public HashSet<PrefabKey> Venting { get; } = [];

    public Dictionary<RoomRef, Kelvin> RoomHeat { get; } = [];

    public Dictionary<NetworkRef, Kelvin> NetworkHeat { get; } = [];

    public HashSet<Cell> TerrainCells { get; } = [];

    public Keeps Keeps(PrefabKey piece) => Keeping.TryGetValue(piece, out var keeps) ? keeps : Lattice.Core.Ports.Keeps.None;

    public bool Supplies(PrefabKey piece) => Suppliers.Contains(piece);

    public Option<Direction> ControlFace(PrefabKey piece) =>
        Controls.TryGetValue(piece, out var side) ? Option.Some(side) : Option<Direction>.None;

    public bool Vents(PrefabKey piece) => Venting.Contains(piece);

    public Option<Kelvin> Temperature(RoomRef room) =>
        RoomHeat.TryGetValue(room, out var kelvin) ? Option.Some(kelvin) : Option<Kelvin>.None;

    public Option<Kelvin> Temperature(NetworkRef network) =>
        NetworkHeat.TryGetValue(network, out var kelvin) ? Option.Some(kelvin) : Option<Kelvin>.None;

    public bool Terrain(Cell large) => TerrainCells.Contains(large);
}

/// <summary>Opening kinds read from the catalogue entry (<see cref="PieceFacts.Opening"/>).</summary>
internal sealed class PieceOpeningFacts(ICatalogue catalogue) : IOpeningFacts
{
    public Option<OpeningKind> Of(PrefabKey piece) =>
        catalogue.Piece(piece).Match(static facts => facts.Opening, static _ => Option<OpeningKind>.None);
}

/// <summary>
/// A world and a board of modded pieces only (no base game has them): a wall panel, a window pane, a gate (a door), a
/// truss (a frame), a console (a large device with controls), a power wire, a power cell (a supplier), a scrubber (a
/// small device that keeps air), a gas tube and a vent. Every face piece takes its cell's x+ face. Large cells centre
/// on odd metres: <see cref="Large"/> gives a large cell's centre; small cells are 5 dm apart from there.
/// </summary>
internal sealed class SafetyBench
{
    public const string Text = """
        lattice-catalogue 3
        piece ModPanel kit=ItemModPanelKit option=0 grid=large
          step material=ItemModPanelKit*1 exit=ItemWrench air=yes gravity=yes
          cell 0 0 0 face-x+

        piece ModPane kit=ItemModPaneKit option=0 grid=large opening=window
          step material=ItemModPaneKit*1 exit=ItemWrench air=yes gravity=yes
          cell 0 0 0 face-x+

        piece ModGate kit=ItemModGateKit option=0 grid=large opening=door
          step material=ItemModGateKit*1 exit=ItemWrench air=yes gravity=yes
          cell 0 0 0 face-x+

        piece ModTruss kit=ItemModTrussKit option=0 grid=large
          step material=ItemModTrussKit*1 exit=ItemWrench air=no
          cell 0 0 0 centre

        piece ModConsole kit=ItemModConsoleKit option=0 grid=large
          step material=ItemModConsoleKit*1 exit=ItemWrench air=no
          cell 0 0 0 centre

        piece ModWire kit=ItemModWire option=0 grid=small
          step material=ItemModWire*1 exit=ItemWireCutters air=no
          end 0 0 0 x+ power
          end 0 0 0 x- power
          cell 0 0 0 cable
          collision 2

        piece ModCell kit=ItemModCellKit option=0 grid=small
          step material=ItemModCellKit*1 exit=ItemWrench air=no
          end 0 0 0 x+ power role=output
          cell 0 0 0 device
          collision 4

        piece ModScrubber kit=ItemModScrubberKit option=0 grid=small
          step material=ItemModScrubberKit*1 exit=ItemWrench air=no
          end 0 0 0 x- power role=input
          cell 0 0 0 device
          collision 4

        piece ModTube kit=ItemModTube option=0 grid=small pipe=gas
          step material=ItemModTube*1 exit=ItemWrench air=no
          end 0 0 0 x+ gas
          end 0 0 0 x- gas
          cell 0 0 0 pipe
          collision 1

        piece ModVent kit=ItemModVentKit option=0 grid=small pipe=gas
          step material=ItemModVentKit*1 exit=ItemWrench air=no
          end 0 0 0 x- gas
          cell 0 0 0 pipe
          collision 1
        """;

    private long _nextStructure = 1000;
    private long _nextRoom;

    /// <param name="extraText">More pieces in the catalogue file format (with its header).</param>
    public SafetyBench(string? extraText = null)
    {
        var modded = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(Text), "safety.lcat")).Pieces.ToList();
        if (extraText is not null)
            modded.AddRange(TestKitIds.MustRead(CatalogueFile.Read(new StringReader(extraText), "extra.lcat")).Pieces);
        Boards = new TestBoard(default, modded.ToArray());
        World = new FakeWorld(Pieces);
    }

    public TestBoard Boards { get; }

    public FakeCatalogue Pieces => Boards.Catalogue;

    public FakeWorld World { get; }

    public TestSafetyFacts Facts { get; } = new();

    public List<GhostId> Plan { get; } = [];

    public static Cell Large(int i, int j, int k) => new(110 + 20 * i, 110 + 20 * j, 110 + 20 * k);

    /// <summary>A small cell, <paramref name="x"/> small steps along x from the centre of large cell (0, 0, 0).</summary>
    public static Cell Small(int x, int y = 0, int z = 0) => new(110 + 5 * x, 110 + 5 * y, 110 + 5 * z);

    public static GasKind Gas(string name) => TestKitIds.Must(GasKind.Create(name));

    public static ValueList<GasHeld> Holding(string gas, double moles) =>
        ValueList.Of(new GasHeld(Gas(gas), new Moles(moles), new Joules(moles * 6000.0)));

    public PrefabKey Key(string piece) => Pieces.Named(piece).Key;

    public StructureRef Real(string piece, Cell at)
    {
        var structure = new StructureRef(new ReferenceId(++_nextStructure), Key(piece), at);
        World.Load(new StructureInfo(structure, default, new BuildState.Built(), Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None));
        return structure;
    }

    public RoomRef Room(ValueList<GasHeld> air, params Cell[] cells)
    {
        var room = new RoomRef(++_nextRoom);
        World.SetRoom(new RoomInfo(room, ValueList.From(cells), true, Option.Some(air)));
        return room;
    }

    public NetworkRef NetworkOf(StructureRef structure) => World.NetworkOf(structure.Id).TryGet(out var network)
        ? network
        : throw new InvalidOperationException("no network");

    public GhostId Build(string piece, Cell at, Option<Finish> finish = default) =>
        Place(new Intent.Build(new Placement(Key(piece), at, default), finish));

    public GhostId Remove(StructureRef target) => Place(new Intent.Remove(target));

    public GhostId Swap(StructureRef old, string piece) =>
        Place(new Intent.Swap(old, new Placement(Key(piece), old.At, default), Option<Finish>.None));

    public GhostId Paint(StructureRef target) => Place(new Intent.FinishOnly(target, Colour));

    public static Finish Colour => TestKitIds.Must(Finish.Create(Option.Some(TestKitIds.Must(PaintColour.Create(3))), Option<ThingName>.None, Option<Setting>.None));

    /// <summary>The same check worked <paramref name="units"/> units a step, as plan state runs it.</summary>
    public ValueList<SafetyWarning> CheckInSteps(int units) => CheckInSteps(units, SafetyRun.StepReads, out _);

    /// <summary>The same check worked <paramref name="units"/> units and about <paramref name="reads"/> cell reads a step.</summary>
    public ValueList<SafetyWarning> CheckInSteps(int units, int reads, out int steps)
    {
        var run = Safety(Facts).Start(PlanGhosts(), Boards.View, World);
        steps = 1;
        while (!run.Step(units, reads))
            steps++;
        return run.Warnings();
    }

    private BuildSafety Safety(ISafetyFacts facts) =>
        new(Pieces, Boards.Grid, facts, new PieceOpeningFacts(Pieces), OpeningBands.Default, SafetyLimits.Default);

    private List<Ghost> PlanGhosts()
    {
        var view = Boards.View;
        var ghosts = new List<Ghost>();
        foreach (var id in Plan)
            if (view.Find(id).TryGet(out var ghost))
                ghosts.Add(ghost);
        return ghosts;
    }

    public ValueList<SafetyWarning> Check(ISafetyFacts? facts = null)
    {
        return Safety(facts ?? Facts).Check(PlanGhosts(), Boards.View, World);
    }

    /// <summary>The warnings with the code.</summary>
    public List<SafetyWarning> Warnings(ReasonCode code, ISafetyFacts? facts = null) =>
        Check(facts).Where(w => w.Why.Code == code).ToList();

    /// <summary>The one warning with the code.</summary>
    public SafetyWarning Single(ReasonCode code) => Assert.Single(Warnings(code));

    private GhostId Place(Intent intent)
    {
        var id = TestBoard.Applied(Boards.Place(TestBoard.Draft(intent))).Added[0].Id;
        Plan.Add(id);
        return id;
    }
}
