using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Commands;
using Lattice.Core.Slices;

namespace Lattice.Core.Transforms;

/// <summary>
/// A template being placed, in slices of <see cref="TransformWork.PerStep"/> drafts: every draft at the origin and turned
/// (placing makes ordinary ghosts; copies are not linked), then handed to <paramref name="finish"/>, which makes the
/// command that places them. For <see cref="Command.PlaceTemplate"/>, one <see cref="Command.CreatePlan"/> that creates
/// the new plan with every draft in it; the board names the new plan when it applies the command, so a template placed
/// anywhere in a <see cref="Command.Batch"/> lands in its own plan. For <see cref="Command.PlaceTemplateIn"/>, one
/// <see cref="Command.Place"/> into the named plan. The drafts keep their looks, maker, groups and attached data; their
/// plan fields are ignored. Nothing on the board refers to the template. All or nothing: a draft that is not a build (a
/// template is about pieces, not standing structures), an unknown piece or a result off its grid refuses the command,
/// naming the drafts by index (fact <c>drafts</c>).
/// </summary>
internal sealed class TemplateWork(
    Placements placements, Template template, Cell origin, Turn turn, Func<GhostDraft[], Result<Command, Reason>> finish) : IExpansionWork
{
    private readonly List<GhostDraft> _drafts = new(template.Ghosts.Count);
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
        var ghosts = template.Ghosts;
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

        var placed = placements.Place(build.Target, origin, turn);
        if (placed.TryGet(out var placement))
            _drafts.Add(draft with { Intent = new Intent.Build(placement, build.Finish) });
        else if (placed.TryGetError(out var problem))
            _problems.Add(problem, index, build.Target.Piece);
    }

    private Result<Command, Reason> Finish()
    {
        if (_problems.Any)
            return Result<Command, Reason>.Error(_problems.First());
        return finish(_drafts.ToArray());
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
    public IExpansionWork Start(ExpansionInput<Command.PlaceTemplate> input)
    {
        var command = input.Command;
        return new TemplateWork(placements, command.Template, command.Origin, command.Turn,
            drafts => Result<Command, Reason>.Ok(new Command.CreatePlan(command.As) { Ghosts = Lists.Own(drafts) }));
    }
}

/// <summary>
/// The expander for <see cref="Command.PlaceTemplateIn"/>, in the templates module and registered sliced as
/// <see cref="TemplateExpander"/> is: the same work, finished as one <see cref="Command.Place"/> with every draft in the
/// named plan (the default plan when none). The plan is looked for when the work finishes, against the board as the
/// actor sees it (a batch's earlier steps staged): one the board does not hold, or the actor may not see, is refused
/// <see cref="ReasonCodes.NotFound"/> (fact <c>plan</c>), alike, so the refusal tells nothing about hidden plans. The
/// plan size cap and the access asks are the board's, as for any place.
/// </summary>
internal sealed class TemplateInExpander(Placements placements, GhostBoard board)
{
    /// <summary>The descriptor to register.</summary>
    public SlicedExpander<Command.PlaceTemplateIn> Descriptor => new(TemplateExpander.Module, Start);

    /// <summary>Sets up the work for one command; places nothing yet.</summary>
    public IExpansionWork Start(ExpansionInput<Command.PlaceTemplateIn> input)
    {
        var command = input.Command;
        var actor = input.Actor;
        return new TemplateWork(placements, command.Template, command.Origin, command.Turn, drafts => Into(command, actor, drafts));
    }

    private Result<Command, Reason> Into(Command.PlaceTemplateIn command, Actor actor, GhostDraft[] drafts)
    {
        var plan = command.Into.Reduce(GhostBoard.DefaultPlan);
        if (new PlansView(board, actor).Find(plan).IsNone)
            return Result<Command, Reason>.Error(Reason.Of(ReasonCodes.NotFound, TransformCodes.Transforms,
                ValueMap.Of("plan", plan.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = drafts[i] with { Plan = Option.Some(plan) };
        return Result<Command, Reason>.Ok(new Command.Place(Lists.Own(drafts)));
    }
}
