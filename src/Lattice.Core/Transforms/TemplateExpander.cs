using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Commands;
using Lattice.Core.Slices;

namespace Lattice.Core.Transforms;

/// <summary>
/// One <see cref="Command.PlaceTemplate"/> being expanded in slices of <see cref="TransformWork.PerStep"/> drafts:
/// one <see cref="Command.CreatePlan"/> that creates the new plan with every draft placed in it, at the origin and turned
/// (placing makes ordinary ghosts; copies are not linked). The drafts keep their looks, maker, groups and attached data;
/// their plan fields are ignored. Nothing on the board refers to the template. All or nothing: a draft that is not a
/// build (a template is about pieces, not standing structures), an unknown piece or a result off its grid refuses the
/// command, naming the drafts by index (fact <c>drafts</c>). The board names the new plan when it applies the command,
/// so a template placed anywhere in a <see cref="Command.Batch"/> lands in its own plan.
/// </summary>
internal sealed class TemplateWork(Placements placements, Command.PlaceTemplate command) : IExpansionWork
{
    private readonly List<GhostDraft> _drafts = new(command.Template.Ghosts.Count);
    private readonly Problems _problems = new("drafts");
    private Option<Result<Command, Reason>> _found;
    private int _next;

    /// <summary>The expanded command, or why the template cannot be placed; read once a step has returned done.</summary>
    public Result<Command, Reason> Found => _found.TryGet(out var found)
        ? found
        : Result<Command, Reason>.Error(Reason.Of(ReasonCodes.UnknownKind, TransformCodes.Transforms, ValueMap.Of("problem", "not-done")));

    public StepOutcome Step()
    {
        if (_found.IsSome)
            return StepOutcome.Done;
        var ghosts = command.Template.Ghosts;
        var end = Math.Min(ghosts.Count, _next + TransformWork.PerStep);
        while (_next < end)
            Add(_next, ghosts[_next++]);
        if (_next < ghosts.Count)
            return StepOutcome.More;

        _found = Option.Some(Finish());
        return StepOutcome.Done;
    }

    private void Add(int index, GhostDraft draft)
    {
        if (draft.Intent is not Intent.Build build)
        {
            _problems.Add(draft.Intent is Intent.Remove or Intent.Swap or Intent.FinishOnly ? TransformProblem.TargetsAStructure : TransformProblem.UnknownKind, index, default);
            return;
        }

        var placed = placements.Place(build.Target, command.Origin, command.Turn);
        if (placed.TryGet(out var placement))
            _drafts.Add(draft with { Intent = new Intent.Build(placement, build.Finish) });
        else if (placed.TryGetError(out var problem))
            _problems.Add(problem, index, build.Target.Piece);
    }

    private Result<Command, Reason> Finish()
    {
        if (_problems.Any)
            return Result<Command, Reason>.Error(_problems.First());
        return Result<Command, Reason>.Ok(new Command.CreatePlan(command.As) { Ghosts = Lists.Own(_drafts.ToArray()) });
    }
}

/// <summary>
/// The expander for <see cref="Command.PlaceTemplate"/>, registered sliced
/// (<see cref="CommandExpanders.RegisterSliced{TCommand}"/>) so a large template is worked out over several frames in
/// the checking budget.
/// </summary>
internal sealed class TemplateExpander(Placements placements)
{
    /// <summary>The module that owns templates.</summary>
    public static ModuleName Module { get; } = ModuleName.Create("templates")
        .Match(static name => name, static reason => throw new InvalidOperationException(reason.ToString()));

    /// <summary>The descriptor to register.</summary>
    public SlicedExpander<Command.PlaceTemplate> Descriptor => new(Module, Start);

    /// <summary>Sets up the work for one command; places nothing yet.</summary>
    public IExpansionWork Start(ExpansionInput<Command.PlaceTemplate> input) => new TemplateWork(placements, input.Command);
}
