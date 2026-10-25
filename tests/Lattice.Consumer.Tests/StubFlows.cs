using Lattice.Api;
using static Lattice.Consumer.Tests.Make;

namespace Lattice.Consumer.Tests;

/// <summary>The consumer flows on the stub host: the stub answers as the host would for the ghosts it placed.</summary>
public sealed class StubFlows : ConsumerFlows
{
    private readonly StubSystem _stub = new(isHost: true);
    private long _next;

    public StubFlows()
    {
        _stub.HostAnswer = (id, command, actor) => command is Command.Place place
            ? new Outcome.Applied(Changes(id, actor, Ghosts(place)))
            : new Outcome.Refused(ValueList.Of(new Refusal(Reason.Of(ReasonCodes.UnknownKind, RuleId.Contract), ValueList<GhostId>.Empty)));
        _stub.PreviewAnswer = (command, actor) => command is Command.Place place
            ? Result<Preview, ValueList<Refusal>>.Ok(new Preview(Changes(default, actor, Ghosts(place)), ValueList<Finding>.Empty, EmptyBill(), NoEffects(), Option<RunReport>.None))
            : Result<Preview, ValueList<Refusal>>.Error(ValueList<Refusal>.Empty);
    }

    private protected override IGhostSystem System => _stub;

    private protected override Actor Player => Make.Player();

    private protected override GhostDraft Draft(int spot) =>
        new(Build(2001, 20 * spot), Option<PlanId>.None, Maker("planner", "hand"), new Visibility.Everyone(), Option<Label>.None, Option<Tint>.None, ValueSet<GroupName>.Empty, ValueMap<AttachmentKey, Attachment>.Empty);

    private protected override void Frames()
    {
        _stub.RunDrawingFrame();
        _stub.FinishPendingWork();
    }

    private protected override void Placed(Ghost ghost)
    {
        _stub.Report = new ValidationReport(new Revision(2), ValueList.Of<Finding>(new Finding.Passes(ghost.Id)));
        var build = (Intent.Build)ghost.Intent;
        _stub.StepPlans[ghost.Id] = new StepPlan(
            ghost.Id,
            ValueList.Of<Step>(new Step.PlaceKit(new KitKey(1001), Ok(KitOption.Create(0)), Ok(ItemQuantity.Create(new ItemKey(1001), 1)), build.Target)),
            ValueList<Constraint>.Empty);
    }

    private ValueList<Ghost> Ghosts(Command.Place place)
    {
        var ghosts = new System.Collections.Generic.List<Ghost>(place.Ghosts.Count);
        foreach (var draft in place.Ghosts)
            ghosts.Add(Ghost(++_next, draft.Intent, draft.MadeBy));
        return ValueList.From(ghosts);
    }
}
