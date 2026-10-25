using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Commands;
using Lattice.Core.Saving;
using Lattice.Core.Slices;

namespace Lattice.Core.FinishedPlans;

/// <summary>Who gives the finished-plans module's reasons.</summary>
internal static class FinishedCodes
{
    public static RuleId Rule => RuleId.Known("lattice.finished-plans");

    public static ModuleName Module { get; } = ModuleName.Create("finished-plans")
        .Match(static name => name, static reason => throw new InvalidOperationException(reason.ToString()));
}

/// <summary>A finished plan's file as an actor may read it, through the saving the board has installed.</summary>
internal static class FinishedPlanFiles
{
    /// <summary>
    /// The finished plan's contents. Refused module-missing (<c>module</c> = <c>saving</c>) until saving is installed,
    /// not-allowed (<c>problem</c> = <c>not-finished</c>) for a plan on the board, not-found for a plan the actor may
    /// not see; else saving's answer. Linear in the plan's lines.
    /// </summary>
    public static Result<FinishedContents, Reason> Read(GhostBoard board, PlanId plan, Actor actor)
    {
        if (board.Saved is not { } saved)
            return Result<FinishedContents, Reason>.Error(Reason.Of(ReasonCodes.ModuleMissing, FinishedCodes.Rule, ValueMap.Of("module", "saving")));
        if (board.Index.Plans.ContainsKey(plan))
            return Result<FinishedContents, Reason>.Error(Reason.Of(ReasonCodes.NotAllowed, FinishedCodes.Rule, PlanFact(plan).With("problem", "not-finished")));
        var read = saved.FinishedContents(plan);
        return read.TryGet(out var contents) && !board.Policies.MaySee(actor, contents.Record)
            ? Result<FinishedContents, Reason>.Error(Reason.Of(ReasonCodes.NotFound, FinishedCodes.Rule, PlanFact(plan)))
            : read;
    }

    public static ValueMap<string, string> PlanFact(PlanId plan) => ValueMap.Of("plan", plan.Value.ToString());
}

/// <summary>
/// What repair makes for one piece a finished plan built, compared with the world mirror now: ghosts for
/// whatever has gone or changed since:
/// <list type="bullet">
/// <item>The structure stands, built, with the finish the ghost asked: nothing.</item>
/// <item>It stands, built, and a part of that finish differs: a finish ghost on it.</item>
/// <item>It stands part-built or broken: a build ghost for the piece (steps continue or report it).</item>
/// <item>It is gone and one other structure holds its spots: a swap ghost from that structure back to the piece.</item>
/// <item>It is gone otherwise: a build ghost for the piece.</item>
/// </list>
/// A piece a finish ghost only painted, named or set is checked for its finish alone. The structure is found by its id,
/// else by piece and cell (rebuilt by hand). Constant cost per piece, plus its spots when it is gone.
/// </summary>
internal sealed class RepairCheck(WorldMirror mirror, Shapes shapes, Maker madeBy)
{
    private readonly List<StructureInfo> _holders = new(4);

    public Option<GhostDraft> For(BuiltPiece piece)
    {
        var ghost = piece.Ghost;
        switch (ghost.Intent)
        {
            case Intent.Build build:
                return Rebuild(piece, build.Target, build.Finish);
            case Intent.Swap swap:
                return Rebuild(piece, swap.New, swap.Finish);
            case Intent.FinishOnly finishOnly:
                return mirror.Find(piece.Structure).TryGet(out var finished) && Differs(finishOnly.Finish, finished)
                    ? Draft(ghost, new Intent.FinishOnly(finished.Ref, finishOnly.Finish))
                    : Option<GhostDraft>.None;
            default:
                return Option<GhostDraft>.None;
        }
    }

    private Option<GhostDraft> Rebuild(BuiltPiece piece, Placement placement, Option<Finish> finish)
    {
        if (mirror.Find(piece.Structure).TryGet(out var standing))
        {
            if (standing.State is not BuildState.Built)
                return Draft(piece.Ghost, new Intent.Build(placement, finish));
            return finish.TryGet(out var asked) && Differs(asked, standing)
                ? Draft(piece.Ghost, new Intent.FinishOnly(standing.Ref, asked))
                : Option<GhostDraft>.None;
        }

        _holders.Clear();
        if (shapes.Of(placement).TryGet(out var shape))
            mirror.InSpots(shape.Spots, _holders);
        return _holders.Count == 1
            ? Draft(piece.Ghost, new Intent.Swap(_holders[0].Ref, placement, finish))
            : Draft(piece.Ghost, new Intent.Build(placement, finish));
    }

    private static bool Differs(Finish finish, StructureInfo structure) =>
        (finish.Colour.TryGet(out var colour) && structure.Colour != Option.Some(colour))
        || (finish.Name.TryGet(out var name) && structure.Name != Option.Some(name))
        || (finish.Setting.TryGet(out var setting) && structure.Setting != Option.Some(setting));

    private Option<GhostDraft> Draft(Ghost ghost, Intent intent) => Option.Some(new GhostDraft(
        intent, Option<PlanId>.None, madeBy, ghost.Visibility, ghost.Label, ghost.Tint, ghost.Groups, ghost.Attached));
}

/// <summary>
/// What take-down makes for one piece a finished plan built: a remove ghost for its structure while it stands (found by
/// id, else by piece and cell); nothing for a piece already gone or one the plan only finished. Constant cost per piece.
/// </summary>
internal sealed class TakeDownCheck(WorldMirror mirror, Maker madeBy)
{
    public Option<GhostDraft> For(BuiltPiece piece)
    {
        if (piece.Ghost.Intent is not (Intent.Build or Intent.Swap) || !mirror.Find(piece.Structure).TryGet(out var standing))
            return Option<GhostDraft>.None;
        var ghost = piece.Ghost;
        return Option.Some(new GhostDraft(
            new Intent.Remove(standing.Ref), Option<PlanId>.None, madeBy, ghost.Visibility, Option<Label>.None,
            Option<Tint>.None, ValueSet<GroupName>.Empty, ValueMap<AttachmentKey, Attachment>.Empty));
    }
}

/// <summary>
/// One repair or take-down being expanded: the first step reads the finished plan's file, then each step checks
/// <see cref="PerStep"/> of its built pieces against the world mirror. The expansion is one
/// <see cref="Command.CreatePlan"/>: a new plan named after the finished one ("&lt;name&gt; repair",
/// "&lt;name&gt; take-down"; the finished plan's name alone when that would be too long) with its visibility, made by
/// the command's maker, holding ordinary ghosts made by it. Refused with the read's reason, or not-found
/// (<c>problem</c> = <paramref name="nothing"/>) when no piece needs a ghost.
/// </summary>
internal sealed class FinishedPlanWork(
    Func<Result<FinishedContents, Reason>> read,
    Func<BuiltPiece, Option<GhostDraft>> check,
    PlanId plan,
    Maker madeBy,
    string suffix,
    string nothing) : IExpansionWork
{
    /// <summary>Pieces checked per step.</summary>
    public const int PerStep = 64;

    private readonly List<GhostDraft> _drafts = new();
    private FinishedContents? _contents;
    private Option<Result<Command, Reason>> _found;
    private int _next;

    public Result<Command, Reason> Found => _found.TryGet(out var found)
        ? found
        : Result<Command, Reason>.Error(Reason.Of(ReasonCodes.UnknownKind, FinishedCodes.Rule, ValueMap.Of("problem", "not-done")));

    public StepOutcome Step()
    {
        if (_found.IsSome)
            return StepOutcome.Done;
        if (_contents is null)
        {
            var contents = read();
            if (!contents.TryGet(out var opened))
            {
                _found = Option.Some(Result<Command, Reason>.Error(contents.ErrorOf()));
                return StepOutcome.Done;
            }

            _contents = opened;
            return opened.Built.Count == 0 ? Finish() : StepOutcome.More;
        }

        var built = _contents.Built;
        var end = Math.Min(built.Count, _next + PerStep);
        for (; _next < end; _next++)
            if (check(built[_next]).TryGet(out var draft))
                _drafts.Add(draft);
        return _next < built.Count ? StepOutcome.More : Finish();
    }

    private StepOutcome Finish()
    {
        var record = _contents!.Record;
        _found = Option.Some(_drafts.Count == 0
            ? Result<Command, Reason>.Error(Reason.Of(ReasonCodes.NotFound, FinishedCodes.Rule, FinishedPlanFiles.PlanFact(plan).With("problem", nothing)))
            : Result<Command, Reason>.Ok(new Command.CreatePlan(new PlanDraft(
                PlanName.Create(record.Name.Value + " " + suffix).Match(static name => name, _ => record.Name), madeBy, record.Visibility,
                ValueMap<AttachmentKey, Attachment>.Empty)) { Ghosts = Lists.Own(_drafts.ToArray()) }));
        return StepOutcome.Done;
    }
}

/// <summary>
/// The finished-plans module's expanders for <see cref="Command.Repair"/> and <see cref="Command.TakeDown"/>, registered
/// sliced (<see cref="CommandExpanders.RegisterSliced{TCommand}"/>) so a large plan is checked over several frames.
/// <see cref="Command.Reopen"/> is applied by the board itself (it needs the plan's own id back).
/// </summary>
internal sealed class FinishedPlansExpander(GhostBoard board, WorldMirror mirror, Shapes shapes)
{
    public SlicedExpander<Command.Repair> Repair => new(FinishedCodes.Module, input =>
        new FinishedPlanWork(
            () => FinishedPlanFiles.Read(board, input.Command.Plan, input.Actor),
            new RepairCheck(mirror, shapes, input.Command.MadeBy).For,
            input.Command.Plan,
            input.Command.MadeBy,
            "repair",
            "nothing-to-repair"));

    public SlicedExpander<Command.TakeDown> TakeDown => new(FinishedCodes.Module, input =>
        new FinishedPlanWork(
            () => FinishedPlanFiles.Read(board, input.Command.Plan, input.Actor),
            new TakeDownCheck(mirror, input.Command.MadeBy).For,
            input.Command.Plan,
            input.Command.MadeBy,
            "take-down",
            "nothing-stands"));
}
