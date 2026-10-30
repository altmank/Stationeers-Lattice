using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>
/// The ways items enter a cell (<see cref="In"/>) and leave it (<see cref="Out"/>) along a run; a way in neither is one
/// whose flow is not known. Only pieces with roled ends care: a chute junction's inputs take items in and its output
/// lets them out, while a plain chute passes them on whichever way they came (decompile
/// <c>Assets.Scripts.Objects.Pipes/Chute.cs</c> <c>GetOutputNeighbor</c>, <c>IsValidInputConnection</c>).
/// </summary>
internal readonly record struct Flow(Directions In, Directions Out)
{
    /// <summary>No way's flow known.</summary>
    public static Flow None => default;

    public bool IsNone => In.IsEmpty && Out.IsEmpty;

    /// <summary>The ways whose flow is known.</summary>
    public Directions Known => In.Union(Out);

    public Flow Entering(Direction way) => this with { In = In.With(way) };

    public Flow Leaving(Direction way) => this with { Out = Out.With(way) };

    public Flow Union(Flow other) => new(In.Union(other.In), Out.Union(other.Out));
}

/// <summary>Which of a piece's ends, under a turn, take items in and which let them out: its ends with input and output roles.</summary>
internal readonly record struct Steering(Directions Ins, Directions Outs)
{
    public bool IsNone => Ins.IsEmpty && Outs.IsEmpty;

    /// <summary>
    /// Whether the piece passes items the way the flow goes: no input where items leave, no output where they enter,
    /// and one output at most. A piece with two outputs chooses between them by its own state (the game's flip-flop
    /// splitter alternates, its overflow sends on when the output is blocked: decompile
    /// <c>Assets.Scripts.Objects.Pipes/ChuteFlipFlopSplitter.cs</c>, <c>Objects.Pipes/ChuteOverflow.cs</c>), so where the
    /// line's items go is the player's to choose, never the run's.
    /// </summary>
    public bool Fits(Flow flow) => Ins.Intersect(flow.Out).IsEmpty && Outs.Intersect(flow.In).IsEmpty && Outs.Count <= 1;

    /// <summary>The ends of a piece under a turn, by role.</summary>
    public static Steering Of(PieceFacts piece, Turn turn, IGrid grid)
    {
        var (ins, outs) = (Directions.None, Directions.None);
        foreach (var end in piece.Ends)
        {
            if (!end.Role.TryGet(out var role))
                continue;
            var facing = grid.Apply(turn, end.Facing);
            if (IsInput(role))
                ins = ins.With(facing);
            else
                outs = outs.With(facing);
        }

        return new Steering(ins, outs);
    }

    /// <summary>Whether an end with this role takes items in.</summary>
    public static bool IsInput(ConnectionRole role) => role is ConnectionRole.Input or ConnectionRole.Input2;
}
