using System;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Commands;
using Lattice.Core.Ports;
using Lattice.Core.Slices;
using Lattice.Core.Swaps;
using Lattice.TestKit;
using Xunit;
using Lattice.Tests.Board;
using Lattice.Tests.Runs;

namespace Lattice.Tests.Swaps;

/// <summary>
/// The run bench (hand catalogue plus the modded conduit kit) with a heavy cable corner and a modded panel kit: a solid
/// panel and a weak pane, both closing one face like the game's wall and window, named nothing like the game's.
/// </summary>
internal sealed class SwapBench
{
    /// <summary>The maker the bench's swap commands name.</summary>
    public static readonly Maker Asker = TestKitIds.Must(Maker.Create(TestKitIds.Mod, "asker"));

    /// <summary>Test values, not game claims: the heavy corner's ends copy the normal corner's; the panel kit's ratings.</summary>
    public const string ExtraText = """
        lattice-catalogue 3
        piece StructureCableCornerH kit=ItemCableCoilHeavy option=1 grid=small
          step material=ItemCableCoilHeavy*1 exit=ItemWireCutters air=no
          end 0 0 0 x- power,data reach=340
          end 0 0 0 z+ power,data reach=400
          rating cable=100000
          cell 0 0 0 cable
          collision 2

        piece ModPanel kit=ItemModPanel option=0 grid=large
          step entry=ItemModPanel material=ItemModPanel*1 exit=ItemWrench air=yes gravity=yes
          rating face=300
          cell 0 0 0 face-z-

        piece ModPane kit=ItemModPanel option=1 grid=large
          step entry=ItemModPanel material=ItemModPanel*1 exit=ItemWrench air=yes gravity=yes
          rating face=20
          cell 0 0 0 face-z-
        """;

    public SwapBench()
    {
        Runs = new RunBench(ExtraText);
        Planner = new SwapPlanner(Runs.Catalogue, new Shapes(Runs.Catalogue, Runs.Grid), Runs.World);
    }

    public RunBench Runs { get; }

    public SwapPlanner Planner { get; }

    /// <summary>A large cell's centre: large cells sit every 20 decimetres.</summary>
    public static Cell Large(int x, int y, int z) => new(110 + 20 * x, 110 + 20 * y, 110 + 20 * z);

    public PrefabKey Piece(string name) => Runs.Catalogue.Named(name).Key;

    public KitKey Kit(string piece) => Runs.KitOf(piece);

    public string Name(PrefabKey piece) => Runs.NameOf(piece);

    public StructureRef Real(string piece, Cell at, Turn turn = default) => Runs.Real(piece, at, turn);

    public StructureInfo Info(StructureRef structure) =>
        Runs.World.Structure(structure.Id).TryGet(out var info) ? info : throw new InvalidOperationException("gone");

    public ValueList<SwapOption> Options(StructureRef structure) => TestKitIds.Must(Planner.Options(Info(structure)));

    /// <summary>The face the board keys a large piece's z- face by: the cell below on z, its z+ face.</summary>
    public (Cell Cell, Axis Face) FaceZMinusOf(Cell large) => (Runs.Grid.Neighbour(large, Direction.ZMinus, GridSize.Large), Axis.Z);

    public void SetDifference(Cell large, double kpa)
    {
        var (cell, _) = FaceZMinusOf(large);
        Runs.World.SetPressureDifference(cell, Direction.ZPlus, new Kilopascals(kpa));
    }

    /// <summary>Runs the expansion to the end; returns what it found and how many steps it took.</summary>
    public (Result<Command, Reason> Found, int Steps) Expand(StructureSelector which, SwapTo to, Option<PlanId> into = default)
    {
        var work = new SwapExpander(Planner).Start(new ExpansionInput<Command.PlanSwap>(
            new Command.PlanSwap(which, to, into, Asker), new CommandId(Guid.NewGuid()), TestBoard.Alice, Runs.Boards.View, Runs.Query));
        var steps = 1;
        while (work.Step() == StepOutcome.More)
            steps++;
        return (work.Found, steps);
    }

    public ValueList<GhostDraft> Drafts(StructureSelector which, SwapTo to, Option<PlanId> into = default) =>
        Assert.IsType<Command.Place>(TestKitIds.Must(Expand(which, to, into).Found)).Ghosts;

    public Reason Refusal(StructureSelector which, SwapTo to) =>
        Expand(which, to).Found.TryGetError(out var why) ? why : throw new InvalidOperationException("expanded");

    public static StructureSelector Refs(params StructureRef[] structures) => new StructureSelector.Refs(ValueList.From(structures));
}
