using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Runs;

/// <summary>
/// The expander for <see cref="Command.PlanSplit"/>, in the runs module: each long straight among the targets as its
/// cells' single pieces, by the split planner (<see cref="SplitPlanner"/>), the one splitting rule runs use. A ghost
/// long straight is removed and its singles placed with its plan, visibility, label, tint, groups, data and finish,
/// made by the command's maker; a built one gets a removal ghost and a build ghost per cell in the command's plan, as a
/// run joining it plans it. A long straight is a run piece of one of its kits that spans several cells
/// (<see cref="KitShapes.Longs"/>). Built pieces a ghost already removes or swaps are left alone. Cost: linear in the
/// targets.
/// </summary>
internal sealed class SplitExpander(ICatalogue catalogue, IGrid grid)
{
    private static readonly IReadOnlyDictionary<Cell, Directions> NoJoins = new Dictionary<Cell, Directions>();

    private readonly Shapes _shapes = new(catalogue, grid);
    private readonly PieceChooser _chooser = new(catalogue, grid);

    /// <summary>The descriptor to register.</summary>
    public CommandExpander<Command.PlanSplit> Descriptor => new(RunExpander.Module, Expand);

    /// <summary>The removals and single pieces; refused <c>nothing-to-split</c> when no target is a long straight.</summary>
    public Result<Command, Reason> Expand(ExpansionInput<Command.PlanSplit> input)
    {
        var command = input.Command;
        var removed = new List<GhostId>();
        var drafts = new List<GhostDraft>();
        switch (command.Which)
        {
            case SplitTargets.Ghosts ghosts:
            {
                var picked = input.Board.Select(ghosts.Which);
                if (!picked.TryGet(out var found))
                    return Result<Command, Reason>.Error(picked.ErrorOf());
                foreach (var ghost in found)
                {
                    if (ghost.Intent is not Intent.Build build || Singles(build.Target) is not { } pieces)
                        continue;
                    removed.Add(ghost.Id);
                    foreach (var piece in pieces)
                        drafts.Add(new GhostDraft(
                            new Intent.Build(piece, build.Finish), Option.Some(ghost.Plan), command.MadeBy, ghost.Visibility,
                            ghost.Label, ghost.Tint, ghost.Groups, ghost.Attached));
                }

                break;
            }

            case SplitTargets.Structures structures:
            {
                var picked = input.World.Select(structures.Which);
                if (!picked.TryGet(out var found))
                    return Result<Command, Reason>.Error(picked.ErrorOf());
                foreach (var structure in found)
                {
                    if (Taken(input.Board, structure) || !input.World.Structure(structure.Id).TryGet(out var info)
                        || Singles(new Placement(structure.Piece, structure.At, info.Turn)) is not { } pieces)
                        continue;
                    drafts.Add(Draft(new Intent.Remove(structure), command));
                    foreach (var piece in pieces)
                        drafts.Add(Draft(new Intent.Build(piece, Option<Finish>.None), command));
                }

                break;
            }

            default:
                return Result<Command, Reason>.Error(Reason.Of(ReasonCodes.UnknownKind, RunCodes.Runs, ValueMap.Of("targets", command.Which.GetType().Name)));
        }

        if (drafts.Count == 0)
            return Result<Command, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RunCodes.Runs, ValueMap.Of("problem", "nothing-to-split")));
        var place = new Command.Place(ValueList.From(drafts));
        return Result<Command, Reason>.Ok(removed.Count == 0
            ? place
            : new Command.Batch(ValueList.From(new Command[] { new Command.Remove(new Selector.Ids(ValueList.From(removed))), place })));
    }

    // The single pieces of a long straight, in the order of its cells; none for any other piece.
    private List<Placement>? Singles(Placement placement)
    {
        if (!_shapes.Of(placement).TryGet(out var shape))
            return null;
        foreach (var place in shape.Piece.Kits)
        {
            if (!_chooser.Kit(place.Kit).TryGet(out var kit) || !IsLong(kit, shape.Piece.Key))
                continue;
            var occupant = new Occupant(shape, new Holder.Planned(default, Option<StructureRef>.None), PieceCells.Of(shape, grid));
            if (!SplitPlanner.Split(kit, occupant, NoJoins, grid).TryGet(out var choices))
                return null;
            var pieces = new List<Placement>(choices.Count);
            foreach (var choice in choices)
                pieces.Add(choice.Placement);
            return pieces;
        }

        return null;
    }

    private static bool IsLong(KitShapes kit, PrefabKey piece)
    {
        foreach (var line in kit.Longs)
            if (line.Piece == piece)
                return true;
        return false;
    }

    private static bool Taken(IGhostBoard board, StructureRef structure)
    {
        foreach (var ghost in board.On(structure))
            if (ghost.Intent is Intent.Remove or Intent.Swap)
                return true;
        return false;
    }

    private static GhostDraft Draft(Intent intent, Command.PlanSplit command) => new(
        intent, command.Into, command.MadeBy, new Visibility.Everyone(), Option<Label>.None, Option<Tint>.None,
        ValueSet<GroupName>.Empty, ValueMap<AttachmentKey, Attachment>.Empty);
}
