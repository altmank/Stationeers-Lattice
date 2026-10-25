using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Commands;
using Lattice.Core.Slices;

namespace Lattice.Core.Transforms;

/// <summary>The problems found so far, by kind, with the ghosts or drafts each stops; reported in the enum's order.</summary>
internal sealed class Problems(string listName)
{
    private readonly SortedDictionary<TransformProblem, (List<long> Which, List<PrefabKey> Pieces)> _found = new();

    public bool Any => _found.Count > 0;

    public void Add(TransformProblem problem, long which, PrefabKey piece)
    {
        if (!_found.TryGetValue(problem, out var lists))
            _found[problem] = lists = (new List<long>(), new List<PrefabKey>());
        lists.Which.Add(which);
        lists.Pieces.Add(piece);
    }

    public void Clear() => _found.Clear();

    /// <summary>The refusal for the first kind of problem found, naming everything it stops.</summary>
    public Reason First()
    {
        foreach (var pair in _found)
            return TransformCodes.Refusal(pair.Key, listName, pair.Value.Which, pair.Value.Pieces);
        throw new InvalidOperationException("no problem found");
    }
}

/// <summary>
/// One <see cref="Command.Transform"/> being expanded in slices of <see cref="PerStep"/> ghosts: the ghosts the
/// selector picks for the submitting actor, each build ghost's placement transformed (<see cref="Placements"/>).
/// <see cref="TransformMode.Copy"/> places the images as ordinary ghosts in their originals' plans, made by the copy's
/// maker, with the originals' looks, groups and attached data; <see cref="TransformMode.Move"/> moves the ghosts
/// themselves, ids and makers kept, all at once (<see cref="Command.Move"/>). All or nothing: when any picked ghost cannot be transformed (a remove, swap or finish ghost,
/// an unknown piece, a piece with no mirror image, a result off its grid) the command is refused with one reason naming
/// every ghost of the first such problem (<see cref="TransformCodes.Refusal"/>), so the caller can narrow the selector;
/// a selection of no ghosts is refused not-found.
/// If the board moved while the work ran, the step that would finish works the whole selection out again on the board
/// as it stands, so the expansion always matches the board it is applied to.
/// </summary>
internal sealed class TransformWork(Placements placements, Command.Transform command, IGhostBoard board) : IExpansionWork
{
    /// <summary>Ghosts transformed per step.</summary>
    public const int PerStep = 64;

    private readonly List<GhostDraft> _drafts = new();
    private readonly Problems _problems = new("ghosts");
    private Option<ValueList<Ghost>> _ghosts;
    private Option<Result<Command, Reason>> _found;
    private Revision _at;
    private int _next;

    /// <summary>The expanded command, or why the ghosts cannot be transformed; read once a step has returned done.</summary>
    public Result<Command, Reason> Found => _found.TryGet(out var found)
        ? found
        : Result<Command, Reason>.Error(Reason.Of(ReasonCodes.UnknownKind, TransformCodes.Transforms, ValueMap.Of("problem", "not-done")));

    public StepOutcome Step()
    {
        if (_found.IsSome)
            return StepOutcome.Done;
        if (!_ghosts.TryGet(out var ghosts) && !Begin(out ghosts))
            return StepOutcome.Done;

        var end = Math.Min(ghosts.Count, _next + PerStep);
        while (_next < end)
            Add(ghosts[_next++]);
        if (_next < ghosts.Count)
            return StepOutcome.More;

        if (board.Revision != _at)
        {
            if (!Begin(out ghosts))
                return StepOutcome.Done;
            while (_next < ghosts.Count)
                Add(ghosts[_next++]);
        }

        _found = Option.Some(Finish(ghosts));
        return StepOutcome.Done;
    }

    private bool Begin(out ValueList<Ghost> ghosts)
    {
        ghosts = ValueList<Ghost>.Empty;
        if (command.How is not (Transformation.Move or Transformation.Rotate or Transformation.Mirror))
            return Stop(Unknown.Kind(command.How));
        if (command.Mode is not (TransformMode.Move or TransformMode.Copy))
            return Stop(Unknown.Kind(command.Mode));
        var selected = board.Select(command.Which);
        if (!selected.TryGet(out ghosts))
            return Stop(selected.TryGetError(out var why) ? why : Unknown.Kind(command.Which));
        if (ghosts.Count == 0)
            return Stop(Reason.Of(ReasonCodes.NotFound, TransformCodes.Transforms));

        (_ghosts, _at, _next) = (Option.Some(ghosts), board.Revision, 0);
        _drafts.Clear();
        _problems.Clear();
        return true;
    }

    private bool Stop(Reason why)
    {
        _found = Option.Some(Result<Command, Reason>.Error(why));
        return false;
    }

    private void Add(Ghost ghost)
    {
        switch (ghost.Intent)
        {
            case Intent.Build build:
                var moved = placements.Apply(command.How, build.Target);
                if (moved.TryGet(out var placement))
                    _drafts.Add(new GhostDraft(
                        new Intent.Build(placement, build.Finish), Option.Some(ghost.Plan),
                        command.Mode is TransformMode.Copy copy ? copy.MadeBy : ghost.MadeBy, ghost.Visibility,
                        ghost.Label, ghost.Tint, ghost.Groups, ghost.Attached));
                else if (moved.TryGetError(out var problem))
                    _problems.Add(problem, ghost.Id.Value, build.Target.Piece);
                break;
            case Intent.Remove remove:
                _problems.Add(TransformProblem.TargetsAStructure, ghost.Id.Value, remove.Target.Piece);
                break;
            case Intent.Swap swap:
                _problems.Add(TransformProblem.TargetsAStructure, ghost.Id.Value, swap.Old.Piece);
                break;
            case Intent.FinishOnly finish:
                _problems.Add(TransformProblem.TargetsAStructure, ghost.Id.Value, finish.Target.Piece);
                break;
            default:
                _problems.Add(TransformProblem.UnknownKind, ghost.Id.Value, default);
                break;
        }
    }

    private Result<Command, Reason> Finish(ValueList<Ghost> ghosts)
    {
        if (_problems.Any)
            return Result<Command, Reason>.Error(_problems.First());
        var place = new Command.Place(Lists.Own(_drafts.ToArray()));
        if (command.Mode is TransformMode.Copy)
            return Result<Command, Reason>.Ok(place);

        var to = new Dictionary<GhostId, Placement>(ghosts.Count);
        for (var i = 0; i < ghosts.Count; i++)
            to[ghosts[i].Id] = ((Intent.Build)_drafts[i].Intent).Target;
        return Result<Command, Reason>.Ok(new Command.Move(ValueMap<GhostId, Placement>.From(to), Option<Revision>.None));
    }
}

/// <summary>
/// The expander for <see cref="Command.Transform"/> (move, turn or mirror ghosts, or place transformed copies),
/// registered sliced (<see cref="CommandExpanders.RegisterSliced{TCommand}"/>): a large selection is worked out over
/// several frames in the checking budget and the host answers the command once it is done. Pieces mirror by their own
/// catalogue ends and footprint, modded pieces included.
/// </summary>
internal sealed class TransformExpander(Placements placements)
{
    /// <summary>The module that owns transforms.</summary>
    public static ModuleName Module { get; } = ModuleName.Create("transforms")
        .Match(static name => name, static reason => throw new InvalidOperationException(reason.ToString()));

    /// <summary>The descriptor to register.</summary>
    public SlicedExpander<Command.Transform> Descriptor => new(Module, Start);

    /// <summary>Sets up the work for one command; transforms nothing yet.</summary>
    public IExpansionWork Start(ExpansionInput<Command.Transform> input) => new TransformWork(placements, input.Command, input.Board);
}
