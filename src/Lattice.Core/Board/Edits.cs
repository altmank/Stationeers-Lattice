using System;
using Lattice.Api;

namespace Lattice.Core.Board;

/// <summary>Ghost and plan edits as pure functions: the edited value at a revision, or why the edit cannot apply.</summary>
internal static class Edits
{
    public static Result<Ghost, Reason> Apply(Ghost ghost, GhostEdit edit, Revision at) => edit switch
    {
        GhostEdit.SetLabel set => Ok(ghost with { Label = set.To, Revision = at }),
        GhostEdit.SetTint set => Ok(ghost with { Tint = set.To, Revision = at }),
        GhostEdit.JoinGroup join => Ok(ghost with { Groups = With(ghost.Groups, join.Group), Revision = at }),
        GhostEdit.LeaveGroup leave => Ok(ghost with { Groups = Without(ghost.Groups, leave.Group), Revision = at }),
        GhostEdit.SetVisibility set => Ok(ghost with { Visibility = set.To, Revision = at }),
        GhostEdit.Attach attach => Ok(ghost with { Attached = ghost.Attached.With(attach.Key, attach.Value), Revision = at }),
        GhostEdit.Detach detach => Ok(ghost with { Attached = ghost.Attached.Without(detach.Key), Revision = at }),
        GhostEdit.SetFinish set => Finish(ghost.Intent, set.To).Map(intent => ghost with { Intent = intent, Revision = at }),
        _ => Result<Ghost, Reason>.Error(Unknown.Kind(edit)),
    };

    /// <summary>
    /// What <see cref="GhostEdit.Swap"/> does to one ghost: a build or swap ghost not part-built takes the piece
    /// <paramref name="swaps"/> picks, everything else about it kept; the same piece in the same turn leaves it as it is;
    /// any other ghost, or a piece with nothing that fits, is left out with the reason. One target lookup; it allocates
    /// only the edited ghost and its intent.
    /// </summary>
    public static PieceSwap Swap(Ghost ghost, SwapTo to, IPieceSwaps swaps, Revision at)
    {
        if (ghost.State is GhostState.Started)
            return PieceSwap.Out(PartBuilt);
        switch (ghost.Intent)
        {
            case Intent.Build build:
            {
                var target = swaps.Target(build.Target, Option<StructureRef>.None, to);
                if (!target.TryGet(out var placement))
                    return PieceSwap.Out(target.ErrorOf());
                return placement == build.Target
                    ? PieceSwap.Unchanged
                    : PieceSwap.To(ghost with { Intent = build with { Target = placement }, Revision = at });
            }
            case Intent.Swap swap:
            {
                var target = swaps.Target(swap.New, Option.Some(swap.Old), to);
                if (!target.TryGet(out var placement))
                    return PieceSwap.Out(target.ErrorOf());
                return placement == swap.New
                    ? PieceSwap.Unchanged
                    : PieceSwap.To(ghost with { Intent = swap with { New = placement }, Revision = at });
            }
            case Intent.Remove:
                return PieceSwap.Out(RemovalHasNoPiece);
            case Intent.FinishOnly:
                return PieceSwap.Out(FinishHasNoPiece);
            default:
                return PieceSwap.Out(Unknown.Kind(ghost.Intent));
        }
    }

    private static readonly Reason PartBuilt = Reason.InvalidValue(nameof(Ghost.State), nameof(GhostState.Started), "part-built");
    private static readonly Reason RemovalHasNoPiece = Reason.InvalidValue(nameof(Intent), "remove", "no-piece");
    private static readonly Reason FinishHasNoPiece = Reason.InvalidValue(nameof(Intent), "finish", "no-piece");

    public static Result<Plan, Reason> Apply(Plan plan, PlanEdit edit, Revision at) => edit switch
    {
        PlanEdit.Rename rename => OkPlan(plan with { Name = rename.To, Revision = at }),
        PlanEdit.ChangeOwner owner => OkPlan(plan with { Owner = owner.To, Revision = at }),
        PlanEdit.ChangeVisibility visibility => OkPlan(plan with { Visibility = visibility.To, Revision = at }),
        PlanEdit.SetData set => OkPlan(plan with { HigherLayerData = plan.HigherLayerData.With(set.Key, set.Value), Revision = at }),
        PlanEdit.ClearData clear => OkPlan(plan with { HigherLayerData = plan.HigherLayerData.Without(clear.Key), Revision = at }),
        _ => Result<Plan, Reason>.Error(Unknown.Kind(edit)),
    };

    /// <summary>A build or swap ghost takes or drops a finish; a finish ghost only takes a new one; a remove ghost has none.</summary>
    private static Result<Intent, Reason> Finish(Intent intent, Option<Finish> to) => intent switch
    {
        Intent.Build build => Result<Intent, Reason>.Ok(build with { Finish = to }),
        Intent.Swap swap => Result<Intent, Reason>.Ok(swap with { Finish = to }),
        Intent.FinishOnly finish => to.TryGet(out var given)
            ? Result<Intent, Reason>.Ok(finish with { Finish = given })
            : Result<Intent, Reason>.Error(Reason.InvalidValue(nameof(Finish), "none", "finish-ghost-needs-a-finish")),
        Intent.Remove => Result<Intent, Reason>.Error(Reason.InvalidValue(nameof(Finish), "remove", "no-finish-on-a-removal")),
        _ => Result<Intent, Reason>.Error(Unknown.Kind(intent)),
    };

    private static ValueSet<GroupName> With(ValueSet<GroupName> groups, GroupName group)
    {
        if (groups.Contains(group))
            return groups;
        var items = new System.Collections.Generic.List<GroupName>(groups.Count + 1);
        foreach (var g in groups)
            items.Add(g);
        items.Add(group);
        return ValueSet.From(items);
    }

    private static ValueSet<GroupName> Without(ValueSet<GroupName> groups, GroupName group)
    {
        if (!groups.Contains(group))
            return groups;
        var items = new System.Collections.Generic.List<GroupName>(groups.Count);
        foreach (var g in groups)
            if (g != group)
                items.Add(g);
        return ValueSet.From(items);
    }

    private static Result<Ghost, Reason> Ok(Ghost ghost) => Result<Ghost, Reason>.Ok(ghost);

    private static Result<Plan, Reason> OkPlan(Plan plan) => Result<Plan, Reason>.Ok(plan);
}

/// <summary>
/// What a swap edit does to one ghost, as a value: the ghost with its new piece (<see cref="After"/>), left out with a
/// reason (<see cref="Why"/>), or neither, <see cref="Unchanged"/>: it already is that piece in that turn.
/// </summary>
internal readonly record struct PieceSwap(Ghost? After, Reason? Why)
{
    /// <summary>Swappable, nothing to change.</summary>
    public static PieceSwap Unchanged => default;

    /// <summary>The ghost with its new piece.</summary>
    public static PieceSwap To(Ghost after) => new(after, null);

    /// <summary>The ghost cannot take the swap and is left as it is.</summary>
    public static PieceSwap Out(Reason why) => new(null, why);
}

/// <summary>Reading the error of a result already known to be one.</summary>
internal static class ResultErrors
{
    public static TError ErrorOf<T, TError>(this Result<T, TError> result) where T : notnull where TError : notnull =>
        result.TryGetError(out var error) ? error : throw new InvalidOperationException("the result is not an error");
}

/// <summary>Taking an array the caller built and no one else holds as a list, without copying it.</summary>
internal static class Lists
{
    public static ValueList<T> Own<T>(T[] items) => ValueList<T>.Own(items);
}
