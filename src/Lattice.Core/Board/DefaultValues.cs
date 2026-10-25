using Lattice.Api;

namespace Lattice.Core.Board;

/// <summary>
/// Finds a <c>default</c> id or name inside a primitive command, which the contract says the board refuses
/// (<c>Values/*.cs</c>: ids, makers, mod and agent ids, every name type). Linear in the command's values.
/// A kind this check does not know passes here; the board's dispatch refuses it.
/// </summary>
internal static class DefaultValues
{
    public static Option<Reason> In(Command command, Actor actor) => First(Actor(actor), Command(command));

    private static Option<Reason> Command(Command command) => command switch
    {
        Api.Command.Place place => Drafts(place.Ghosts),
        Api.Command.Remove remove => Selector(remove.Which),
        Api.Command.Change change => First(Selector(change.Which), Edit(change.Edit)),
        Api.Command.Move move => Moved(move.To),
        Api.Command.CreatePlan create => First(
            First(Check(create.Draft.Name == default, nameof(PlanName)), Drafts(create.Ghosts)),
            First(Check(create.Draft.MadeBy == default, nameof(Maker)), Visibility(create.Draft.Visibility))),
        Api.Command.EditPlan edit => First(Check(edit.Plan == default, nameof(PlanId)), PlanEdit(edit.Edit)),
        Api.Command.MoveToPlan move => First(Selector(move.Which), Check(move.To == default, nameof(PlanId))),
        Api.Command.Revert revert => Check(revert.ChangeSet == default, nameof(ChangeSetId)),
        Api.Command.Batch batch => Steps(batch.Steps),
        _ => Option<Reason>.None,
    };

    private static Option<Reason> Steps(ValueList<Api.Command> steps)
    {
        foreach (var step in steps)
            if (Command(step).TryGet(out var found))
                return Option.Some(found);
        return Option<Reason>.None;
    }

    private static Option<Reason> Drafts(ValueList<GhostDraft> drafts)
    {
        foreach (var draft in drafts)
        {
            var found = First(
                First(Check(draft.MadeBy == default, nameof(Maker)), Visibility(draft.Visibility)),
                First(Intent(draft.Intent), First(Groups(draft.Groups), Draft(draft))));
            if (found.IsSome)
                return found;
        }

        return Option<Reason>.None;
    }

    private static Option<Reason> Draft(GhostDraft draft) => First(
        Check(draft.Plan.TryGet(out var plan) && plan == default, nameof(PlanId)),
        Check(draft.Label.TryGet(out var label) && label == default, nameof(Label)));

    private static Option<Reason> Groups(ValueSet<GroupName> groups)
    {
        foreach (var group in groups)
            if (group == default)
                return Fail(nameof(GroupName));
        return Option<Reason>.None;
    }

    private static Option<Reason> Intent(Intent intent) => intent switch
    {
        Api.Intent.Build build => Finish(build.Finish),
        Api.Intent.Swap swap => Finish(swap.Finish),
        Api.Intent.FinishOnly finish => Finish(Option.Some(finish.Finish)),
        _ => Option<Reason>.None,
    };

    private static Option<Reason> Finish(Option<Finish> finish) =>
        Check(finish.TryGet(out var f) && f.Name.TryGet(out var name) && name == default, nameof(ThingName));

    private static Option<Reason> Edit(GhostEdit edit) => edit switch
    {
        GhostEdit.SetLabel set => Check(set.To.TryGet(out var label) && label == default, nameof(Label)),
        GhostEdit.JoinGroup join => Check(join.Group == default, nameof(GroupName)),
        GhostEdit.LeaveGroup leave => Check(leave.Group == default, nameof(GroupName)),
        GhostEdit.SetFinish set => Finish(set.To),
        GhostEdit.SetVisibility set => Visibility(set.To),
        _ => Option<Reason>.None,
    };

    private static Option<Reason> PlanEdit(PlanEdit edit) => edit switch
    {
        Api.PlanEdit.Rename rename => Check(rename.To == default, nameof(PlanName)),
        Api.PlanEdit.ChangeOwner owner => Actor(owner.To),
        Api.PlanEdit.ChangeVisibility visibility => Visibility(visibility.To),
        _ => Option<Reason>.None,
    };

    private static Option<Reason> Selector(Selector selector)
    {
        switch (selector)
        {
            case Api.Selector.Ids ids:
                foreach (var id in ids.Values)
                    if (id == default)
                        return Fail(nameof(GhostId));
                return Option<Reason>.None;
            case Api.Selector.Group group:
                return Check(group.Name == default, nameof(GroupName));
            case Api.Selector.Owner owner:
                return Actor(owner.Actor);
            case Api.Selector.MadeBy madeBy:
                return Check(madeBy.Maker == default, nameof(Maker));
            case Api.Selector.InPlan inPlan:
                return Check(inPlan.Plan == default, nameof(PlanId));
            case Api.Selector.All all:
                return Selectors(all.Of);
            case Api.Selector.Any any:
                return Selectors(any.Of);
            default:
                return Option<Reason>.None;
        }
    }

    private static Option<Reason> Moved(ValueMap<GhostId, Placement> to)
    {
        foreach (var pair in to)
            if (pair.Key == default)
                return Fail(nameof(GhostId));
        return Option<Reason>.None;
    }

    private static Option<Reason> Selectors(ValueList<Selector> selectors)
    {
        foreach (var selector in selectors)
            if (Selector(selector) is { IsSome: true } found)
                return found;
        return Option<Reason>.None;
    }

    private static Option<Reason> Actor(Actor actor) => actor switch
    {
        Api.Actor.Agent agent => Check(agent.Id == default, nameof(AgentId)),
        Api.Actor.Mod mod => Check(mod.Id == default, nameof(ModId)),
        _ => Option<Reason>.None,
    };

    private static Option<Reason> Visibility(Visibility visibility) =>
        Check(visibility is Api.Visibility.Team team && team.Name == default, nameof(TeamName));

    private static Option<Reason> First(Option<Reason> a, Option<Reason> b) => a.IsSome ? a : b;

    private static Option<Reason> Check(bool isDefault, string type) => isDefault ? Fail(type) : Option<Reason>.None;

    private static Option<Reason> Fail(string type) =>
        Option.Some(Reason.Of(ReasonCodes.DefaultValue, LatticeIdentity.Board, ValueMap.Of("type", type)));
}
