using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.PlacementCheck;
using Lattice.TestKit.World;
using Lattice.Tests.Board;

namespace Lattice.Tests.Runs;

/// <summary>
/// A board, a world and a placement check over the hand catalogue plus a modded kit: a conduit no base game has, its
/// pieces named nothing like the game's, its straight along local y (the game's run along z) and a 4-cell long
/// straight the game has no length for.
/// </summary>
internal sealed class RunBench
{
    public const string ModdedText = """
        lattice-catalogue 3
        piece ModConduitLine kit=ItemModConduit option=0 grid=small
          step material=ItemModConduit*1 exit=ItemWireCutters air=no
          end 0 0 0 y+ power,data
          end 0 0 0 y- power,data
          cell 0 0 0 cable
          collision 2

        piece ModConduitBend kit=ItemModConduit option=1 grid=small
          step material=ItemModConduit*1 exit=ItemWireCutters air=no
          end 0 0 0 x+ power,data
          end 0 0 0 y+ power,data
          cell 0 0 0 cable
          collision 2

        piece ModConduitTee kit=ItemModConduit option=2 grid=small
          step material=ItemModConduit*2 exit=ItemWireCutters air=no
          end 0 0 0 x+ power,data
          end 0 0 0 y+ power,data
          end 0 0 0 y- power,data
          cell 0 0 0 cable
          collision 2

        piece ModConduitPlus kit=ItemModConduit option=3 grid=small
          step material=ItemModConduit*2 exit=ItemWireCutters air=no
          end 0 0 0 x+ power,data
          end 0 0 0 x- power,data
          end 0 0 0 y+ power,data
          end 0 0 0 y- power,data
          cell 0 0 0 cable
          collision 2

        piece ModConduitLong4 kit=ItemModConduit option=4 grid=small
          step material=ItemModConduit*3 exit=ItemWireCutters air=no
          end 0 0 0 y+ power,data
          end 0 -15 0 y- power,data
          cell 0 0 0 cable
          cell 0 -5 0 cable
          cell 0 -10 0 cable
          cell 0 -15 0 cable
          collision 2
        """;

    private long _nextStructure = 1000;

    /// <param name="extraText">More pieces in the catalogue file format (with its header), for benches built on this one.</param>
    public RunBench(string? extraText = null)
    {
        var modded = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(ModdedText), "runs.lcat")).Pieces.ToList();
        if (extraText is not null)
            modded.AddRange(TestKitIds.MustRead(CatalogueFile.Read(new StringReader(extraText), "extra.lcat")).Pieces);
        Boards = new TestBoard(default, modded.ToArray());
        World = new FakeWorld(Catalogue);
        Planner = new RunPlanner(Catalogue, Grid, Check);
    }

    /// <summary>A small cell well inside the world: small cells sit every 5 decimetres.</summary>
    public static Cell Origin => new(100, 100, 100);

    public TestBoard Boards { get; }

    public FakeCatalogue Catalogue => Boards.Catalogue;

    public CubeGrid Grid => Boards.Grid;

    public FakeWorld World { get; }

    public IWorldQuery Query => new FakeWorldQuery(World);

    public FakePlacementCheck Check { get; } = new();

    public RunPlanner Planner { get; }

    public static Cell At(int x, int y, int z) => new(Origin.X + 5 * x, Origin.Y + 5 * y, Origin.Z + 5 * z);

    public static Cell Step(Cell cell, Direction direction, int cells = 1) => cell.Plus(direction.Unit().Times(5 * cells));

    public KitKey KitOf(string piece) => Catalogue.Named(piece).Kits[0].Kit;

    public KitKey Cable => KitOf("StructureCableStraight");

    public KitKey Conduit => KitOf("ModConduitLine");

    public KitShapes Shapes(KitKey kit) => TestKitIds.Must(new PieceChooser(Catalogue, Grid).Kit(kit));

    public Neighbourhood Around() => new(new Shapes(Catalogue, Grid), Grid, Boards.View, Query);

    public Turn Sending(Direction from, Direction to) => TestBoard.TurnSending(from, to, Grid);

    /// <summary>A built structure; returns its reference.</summary>
    public StructureRef Real(string piece, Cell at, Turn turn = default)
    {
        var structure = new StructureRef(new ReferenceId(++_nextStructure), Catalogue.Named(piece).Key, at);
        World.Load(new StructureInfo(structure, turn, new BuildState.Built(), Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None));
        return structure;
    }

    /// <summary>A real straight next to <paramref name="cell"/> on the given side, one end pointing into it; the game's straights run along local z.</summary>
    public StructureRef RealPointingInto(Cell cell, Direction side, string piece = "StructureCableStraight", Direction along = Direction.ZPlus) =>
        Real(piece, Step(cell, side), Sending(along, side));

    /// <summary>A real modded conduit straight (local y) next to <paramref name="cell"/>, one end pointing into it.</summary>
    public StructureRef ConduitPointingInto(Cell cell, Direction side) => RealPointingInto(cell, side, "ModConduitLine", Direction.YPlus);

    public void Ghost(string piece, Cell at, Turn turn = default) => Boards.Placed(Boards.Draft(piece, at, turn));

    public string NameOf(PrefabKey piece) => Catalogue.NameOf(piece);

    /// <summary>The ways a placed piece leaves a cell, read back from its catalogue ends under its turn.</summary>
    public Directions WaysOf(Placement placement, Cell cell)
    {
        var shape = TestKitIds.Must(new Shapes(Catalogue, Grid).Of(placement));
        var occupant = new Occupant(shape, new Holder.Planned(new GhostId(1), Option<StructureRef>.None), PieceCells.Of(shape, Grid));
        return PieceCells.At(occupant, cell, Grid);
    }

    public ValueList<Cell> CellsOf(Placement placement) =>
        PieceCells.Of(TestKitIds.Must(new Shapes(Catalogue, Grid).Of(placement)), Grid);

    public RunRequest Run(KitKey kit, Cell from, Cell to, params Cell[] via) =>
        new(kit, from, ValueList.From(via), to, Option<PlanId>.None, TestBoard.TestMaker);

    public RunPlan Plan(RunRequest request) => TestKitIds.Must(Planner.Plan(request, Boards.View, Query));

    public Reason Refusal(RunRequest request) =>
        Planner.Plan(request, Boards.View, Query).TryGetError(out var why) ? why : throw new InvalidOperationException("planned");

    /// <summary>Parses <c>x+,z-</c> into a direction set.</summary>
    public static Directions Ways(string text)
    {
        var ways = Directions.None;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            ways = ways.With(part switch
            {
                "x+" => Direction.XPlus,
                "x-" => Direction.XMinus,
                "y+" => Direction.YPlus,
                "y-" => Direction.YMinus,
                "z+" => Direction.ZPlus,
                "z-" => Direction.ZMinus,
                _ => throw new ArgumentException(part),
            });
        return ways;
    }

    public static IEnumerable<Direction> Each(Directions ways)
    {
        for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
            if (ways.Contains(direction))
                yield return direction;
    }
}
