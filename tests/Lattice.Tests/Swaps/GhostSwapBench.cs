using System;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;
using Lattice.Core.Selectors;
using Lattice.Core.Swaps;
using Lattice.Tests.Board;
using Lattice.Tests.Preview;
using Lattice.Tests.Runs;

namespace Lattice.Tests.Swaps;

/// <summary>
/// The preview bench (board, world, mirror, validation, steps, the command service with the swap expander) with the
/// ghost selectors and the swap planner's fits installed on the board, over the swap bench's pieces plus a heavy tee and
/// three-long, a modded armoured cable of the straight's shape, a modded wide cable of that shape that cannot share a
/// cell with a pipe, and a modded rail and heavy rail that stand on a frame. Test values, not game claims. Load the
/// world, then <see cref="Start"/>.
/// </summary>
internal sealed class GhostSwapBench
{
    public const string MoreText = """
        piece StructureCableJunctionH kit=ItemCableCoilHeavy option=2 grid=small
          step material=ItemCableCoilHeavy*2 exit=ItemWireCutters air=no
          end 0 0 0 x- power,data reach=340
          end 0 0 0 z+ power,data reach=400
          end 0 0 0 z- power,data reach=340
          rating cable=100000
          cell 0 0 0 cable
          collision 2

        piece StructureCableStraight3H kit=ItemCableCoilHeavy option=3 grid=small
          step material=ItemCableCoilHeavy*3 exit=ItemWireCutters air=no
          end 0 0 0 z+ power,data reach=400
          end 0 0 -10 z- power,data reach=340
          rating cable=100000
          cell 0 0 0 cable
          cell 0 0 -5 cable
          cell 0 0 -10 cable
          collision 2

        piece ModArmouredCable kit=ItemModArmouredCable option=0 grid=small
          step material=ItemModArmouredCable*1 exit=ItemWireCutters air=no
          end 0 0 0 z+ power,data reach=400
          end 0 0 0 z- power,data reach=340
          rating cable=50000
          cell 0 0 0 cable
          collision 2

        piece ModWideCable kit=ItemModWideCable option=0 grid=small
          step material=ItemModWideCable*1 exit=ItemWireCutters air=no
          end 0 0 0 z+ power,data reach=400
          end 0 0 0 z- power,data reach=340
          cell 0 0 0 cable
          collision 3

        piece ModRail kit=ItemModRail option=0 grid=small support=lattice.support.frame-below
          step material=ItemModRail*1 exit=ItemWrench air=no
          end 0 0 0 z+ power
          end 0 0 0 z- power
          cell 0 0 0 cable
          collision 2

        piece ModRailHeavy kit=ItemModRailHeavy option=0 grid=small support=lattice.support.frame-below
          step material=ItemModRailHeavy*1 exit=ItemWrench air=no
          end 0 0 0 z+ power
          end 0 0 0 z- power
          cell 0 0 0 cable
          collision 2
        """;

    public GhostSwapBench(bool swaps = true)
    {
        Preview = new PreviewBench(MoreText);
        Board.InstallSelectors(new GhostSelectors(Preview.Shapes, Runs.Grid, Preview.Mirror, Runs.World));
        if (swaps)
            Board.InstallSwaps(new SwapPlanner(Runs.Catalogue, Preview.Shapes, Runs.World));
    }

    public PreviewBench Preview { get; }

    public RunBench Runs => Preview.Runs;

    public TestBoard Boards => Runs.Boards;

    public GhostBoard Board => Preview.Board;

    public IGhostBoard View => Boards.View;

    public Turn AlongX => Runs.Sending(Direction.ZPlus, Direction.XPlus);

    public GhostSwapBench Start()
    {
        Preview.Start();
        return this;
    }

    public PrefabKey Piece(string name) => Runs.Catalogue.Named(name).Key;

    public string Name(PrefabKey piece) => Runs.NameOf(piece);

    public KitKey Kit(string piece) => Runs.KitOf(piece);

    public SwapTo Heavy => new SwapTo.SameShapeFrom(Kit("StructureCableStraightH"));

    public Placement At(string piece, Cell cell, Turn turn = default) => new(Piece(piece), cell, turn);

    /// <summary>The piece a build or swap ghost plans.</summary>
    public static Placement PlacementOf(Ghost ghost) => ghost.Intent switch
    {
        Intent.Build build => build.Target,
        Intent.Swap swap => swap.New,
        _ => throw new InvalidOperationException("no piece"),
    };

    public Ghost Now(GhostId id) => View.Find(id).Match(static g => g, () => throw new InvalidOperationException($"ghost {id.Value} is gone"));

    public Placement ShapeOf(GhostId id) =>
        Board.Index.Ghosts[id].Shape.Match(static s => s.Placement, () => throw new InvalidOperationException("no shape"));

    public Command.Change Swap(Selector which, SwapTo to, Option<Revision> seen = default) => new(which, new GhostEdit.Swap(to), seen);

    /// <summary>Sends a command through the command service, running frames until it is answered.</summary>
    public Outcome Submit(Command command, Actor? actor = null)
    {
        var id = new CommandId(Guid.NewGuid());
        Outcome? answered = null;
        void Answered(Ticket ticket, Outcome outcome)
        {
            if (ticket == new Ticket(id))
                answered = outcome;
        }

        Preview.Events.Answered += Answered;
        try
        {
            if (Preview.Commands.Submit(id, command, actor ?? TestBoard.Alice) is Submission.Answered now)
                return now.Outcome;
            for (var i = 0; i < 200 && answered is null; i++)
                Preview.Frame();
            return answered ?? throw new InvalidOperationException("not answered");
        }
        finally
        {
            Preview.Events.Answered -= Answered;
        }
    }

    public ChangeSet Applied(Command command, Actor? actor = null) => TestBoard.Applied(Submit(command, actor));

    public Refusal Refused(Command command, Actor? actor = null) => TestBoard.Refused(Submit(command, actor));
}
