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
