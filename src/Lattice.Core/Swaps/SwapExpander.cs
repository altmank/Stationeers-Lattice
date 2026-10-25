using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Commands;
using Lattice.Core.Slices;

namespace Lattice.Core.Swaps;

/// <summary>
/// One <see cref="Command.PlanSwap"/> being expanded in slices: the selector's structures on the first step, then
/// <see cref="PerStep"/> structures a step, each given the piece <see cref="SwapPlanner.Target"/> picks; the swap ghosts
/// go out as one <see cref="Command.Place"/>. A structure that is gone, already is that piece in that turn, or has no
/// piece to become is left out. Refused with the first structure's reason when structures were picked and none of them
/// can swap (a kit with no piece of their shape, an unknown kit); the selector's refusal passes through; a selector that
/// picks nothing gives an empty <see cref="Command.Place"/>.
/// </summary>
internal sealed class SwapExpansion(SwapPlanner planner, ExpansionInput<Command.PlanSwap> input) : IExpansionWork
{
    /// <summary>Structures looked at per step: each costs a lookup once its piece pair is known, up to 24 shapes the first time.</summary>
    public const int PerStep = 64;

    private ValueList<StructureRef>? _picked;
    private readonly List<GhostDraft> _drafts = new();
    private int _next;
    private int _swappable;
    private Option<Reason> _firstRefusal;
    private Option<Result<Command, Reason>> _found;

    /// <summary>The expanded command, or why there is none; read once a step has returned done.</summary>
    public Result<Command, Reason> Found => _found.TryGet(out var found)
        ? found
        : Result<Command, Reason>.Error(Reason.Of(ReasonCodes.UnknownKind, SwapCodes.Swaps, ValueMap.Of("problem", "not-done")));

    public StepOutcome Step()
    {
        if (_found.IsSome)
            return StepOutcome.Done;
        if (_picked is null)
            return Select();

        var end = Math.Min(_next + PerStep, _picked.Count);
        for (; _next < end; _next++)
            Swap(_picked[_next]);
        return _next < _picked.Count ? StepOutcome.More : Done();
    }

    private StepOutcome Select()
    {
        var picked = input.World.Select(input.Command.Which);
        if (picked.TryGetError(out var why))
        {
            _found = Option.Some(Result<Command, Reason>.Error(why));
            return StepOutcome.Done;
        }

        _picked = picked.TryGet(out var refs) ? refs : ValueList<StructureRef>.Empty;
        return _picked.Count > 0 ? StepOutcome.More : Done();
    }

    private StepOutcome Done()
    {
        _found = Option.Some(Finish());
        return StepOutcome.Done;
    }

    private void Swap(StructureRef structure)
    {
        if (!input.World.Structure(structure.Id).TryGet(out var info))
            return;
        var target = planner.Target(info, input.Command.To);
        if (target.TryGet(out var placement))
        {
            _swappable++;
            if (placement.Piece != info.Ref.Piece || placement.Turn != info.Turn)
                _drafts.Add(new GhostDraft(
                    new Intent.Swap(info.Ref, placement, Option<Finish>.None), input.Command.Into, input.Command.MadeBy,
                    new Visibility.Everyone(), Option<Label>.None, Option<Tint>.None, ValueSet<GroupName>.Empty,
                    ValueMap<AttachmentKey, Attachment>.Empty));
            return;
        }

        if (_firstRefusal.IsNone && target.TryGetError(out var why))
            _firstRefusal = Option.Some(why);
    }

    private Result<Command, Reason> Finish()
    {
        if (_swappable == 0 && _firstRefusal.TryGet(out var why))
            return Result<Command, Reason>.Error(why with { Facts = why.Facts.With("picked", _picked!.Count.ToString()) });
        return Result<Command, Reason>.Ok(new Command.Place(ValueList.From(_drafts)));
    }
}

/// <summary>
/// The expander for <see cref="Command.PlanSwap"/> ("swap all"), registered sliced
/// (<see cref="CommandExpanders.RegisterSliced{TCommand}"/>): a selector may pick a whole station, so the swaps are
/// worked out in the checking budget and the host answers the command once they are. Reads the world as the expander
/// is given it.
/// </summary>
internal sealed class SwapExpander(SwapPlanner planner)
{
    /// <summary>The module that owns swaps.</summary>
    public static ModuleName Module { get; } = ModuleName.Create("swaps")
        .Match(static name => name, static reason => throw new InvalidOperationException(reason.ToString()));

    /// <summary>The descriptor to register.</summary>
    public SlicedExpander<Command.PlanSwap> Descriptor => new(Module, Start);

    /// <summary>Sets up the work for one command; selects nothing yet.</summary>
    public IExpansionWork Start(ExpansionInput<Command.PlanSwap> input) => new SwapExpansion(planner, input);
}
