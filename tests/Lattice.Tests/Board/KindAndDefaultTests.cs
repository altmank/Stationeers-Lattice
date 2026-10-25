using System;
using System.Collections.Generic;
using System.Reflection;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.TestKit;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Board;

public sealed class KindAndDefaultTests
{
    private static readonly Cell At = new(100, 100, 100);

    public static TheoryData<string> DefaultCases() => new()
    {
        "GhostId", "PlanId", "Maker", "ModId", "AgentId", "PlanName", "Label", "GroupName", "TeamName", "ThingName",
    };

    /// <summary>
    /// One command per value type that reaches the board's primitive commands. ItemQuantity, TemplateName, DisplayName
    /// and SaveName appear only in module-owned commands and descriptors, which their modules check.
    /// </summary>
    [Theory]
    [MemberData(nameof(DefaultCases))]
    public void A_default_value_in_a_command_is_refused(string type)
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var draft = t.Draft("StructureCableStraight", new Cell(200, 100, 100));
        var finish = TestKitIds.Must(Finish.Create(Option<PaintColour>.None, Option.Some(default(ThingName)), Option<Setting>.None));
        (Command Command, Actor Actor) sent = type switch
        {
            "GhostId" => (new Command.Remove(Ids(default(GhostId))), Alice),
            "PlanId" => (new Command.MoveToPlan(Ids(ghost.Id), default), Alice),
            "Maker" => (new Command.Place(ValueList.Of(draft with { MadeBy = default })), Alice),
            "ModId" => (new Command.Remove(Ids(ghost.Id)), new Actor.Mod(default)),
            "AgentId" => (new Command.Remove(Ids(ghost.Id)), new Actor.Agent(default)),
            "PlanName" => (new Command.EditPlan(GhostBoard.DefaultPlan, new PlanEdit.Rename(default), Option<Revision>.None), Alice),
            "Label" => (new Command.Change(Ids(ghost.Id), new GhostEdit.SetLabel(Option.Some(default(Label))), Option<Revision>.None), Alice),
            "GroupName" => (new Command.Change(Ids(ghost.Id), new GhostEdit.JoinGroup(default), Option<Revision>.None), Alice),
            "TeamName" => (new Command.Place(ValueList.Of(draft with { Visibility = new Visibility.Team(default) })), Alice),
            "ThingName" => (new Command.Change(Ids(ghost.Id), new GhostEdit.SetFinish(Option.Some(finish)), Option<Revision>.None), Alice),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        var refusal = Refused(t.Apply(sent.Command, sent.Actor));

        Assert.Equal(ReasonCodes.DefaultValue, refusal.Why.Code);
        Assert.True(refusal.Why.Facts.TryGetValue("type", out var named) && named == type);
    }

    [Fact]
    public void A_default_command_id_is_refused()
    {
        var t = new TestBoard();

        var outcome = t.Board.Apply(default, new Command.Remove(Ids(new GhostId(1))), Alice);

        Assert.Equal(ReasonCodes.DefaultValue, Refused(outcome).Why.Code);
    }

    [Fact]
    public void A_shared_unknown_kind_refusal_names_the_kind()
    {
        var reason = Unknown.Kind(new object());

        Assert.Equal(ReasonCodes.UnknownKind, reason.Code);
        Assert.True(reason.Facts.TryGetValue("kind", out var kind) && kind == "Object");
    }

    /// <summary>
    /// Unions are closed outside Lattice.Api, so no test can build a case the board does not know; this test lists
    /// every case each union has today and fails when one is added that the board was not taught (it would answer
    /// unknown-kind) or that this test has no sample for.
    /// </summary>
    [Fact]
    public void The_board_answers_every_case_of_the_unions_it_switches_on()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var structure = new StructureRef(new ReferenceId(5), t.Catalogue.Named("StructureFrameIron").Key, new Cell(10, 10, 10));
        var samples = Samples(t, ghost, structure);
        var unanswered = new List<string>();

        foreach (var union in new[] { typeof(Command), typeof(Intent), typeof(GhostEdit), typeof(PlanEdit), typeof(Selector), typeof(GhostState) })
            foreach (var kind in union.GetNestedTypes(BindingFlags.Public))
            {
                if (!kind.IsSubclassOf(union))
                    continue;
                if (!samples.TryGetValue(kind, out var answer))
                {
                    unanswered.Add($"{union.Name}.{kind.Name}: no sample");
                    continue;
                }

                if (answer() is { } code && code == ReasonCodes.UnknownKind)
                    unanswered.Add($"{union.Name}.{kind.Name}: unknown-kind");
            }

        Assert.Empty(unanswered);
    }

    private static Dictionary<Type, Func<ReasonCode?>> Samples(TestBoard t, Ghost ghost, StructureRef structure)
    {
        var group = TestKitIds.Must(GroupName.Create("Hydro"));
        var key = TestKitIds.Must(AttachmentKey.Create(TestKitIds.Mod, "tag"));
        var finish = TestKitIds.Must(Finish.Create(Option.Some(TestKitIds.Must(PaintColour.Create(1))), Option<ThingName>.None, Option<Setting>.None));
        var name = TestKitIds.Must(PlanName.Create("Plan"));
        var placement = new Placement(t.Catalogue.Named("StructureCableCorner").Key, At, default);
        var ids = Ids(ghost.Id);

        ReasonCode? Code(Outcome outcome) => outcome is Outcome.Refused refused ? refused.Why[0].Why.Code : null;
        ReasonCode? Run(Command command) => Code(t.Apply(command));
        ReasonCode? Edit(GhostEdit edit) => Run(new Command.Change(ids, edit, Option<Revision>.None));
        ReasonCode? EditPlan(PlanEdit edit) => Run(new Command.EditPlan(GhostBoard.DefaultPlan, edit, Option<Revision>.None));
        ReasonCode? Pick(Selector selector) => t.View.Select(selector).TryGetError(out var why) ? why.Code : null;
        ReasonCode? Place(Intent intent) => Run(new Command.Place(ValueList.Of(Draft(intent))));
        ReasonCode? Mark(Result<ChangeSet, Reason> marked) => marked.TryGetError(out var why) ? why.Code : null;
        var draftPlan = new PlanDraft(name, TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);
        var structures = new StructureSelector.Refs(ValueList.Of(structure));

        return new Dictionary<Type, Func<ReasonCode?>>
        {
            [typeof(Command.Place)] = () => Place(new Intent.Build(placement, Option<Finish>.None)),
            [typeof(Command.Remove)] = () => Run(new Command.Remove(Ids(new GhostId(999)))),
            [typeof(Command.Change)] = () => Edit(new GhostEdit.SetTint(Option<Tint>.None)),
            [typeof(Command.Move)] = () => Run(new Command.Move(ValueMap.Of(ghost.Id, placement with { At = new Cell(400, 100, 100) }), Option<Revision>.None)),
            [typeof(Command.CreatePlan)] = () => Run(new Command.CreatePlan(draftPlan)),
            [typeof(Command.EditPlan)] = () => EditPlan(new PlanEdit.Rename(name)),
            [typeof(Command.MoveToPlan)] = () => Run(new Command.MoveToPlan(ids, GhostBoard.DefaultPlan)),
            [typeof(Command.Revert)] = () => Run(new Command.Revert(new ChangeSetId(1))),
            [typeof(Command.Batch)] = () => Run(new Command.Batch(ValueList<Command>.Empty)),
            [typeof(Command.FinishPlan)] = () => Run(new Command.FinishPlan(GhostBoard.DefaultPlan)),
            [typeof(Command.CatchUp)] = () => Run(new Command.CatchUp(GhostBoard.DefaultPlan)),
            [typeof(Command.MarkRemoval)] = () => Run(new Command.MarkRemoval(structures, Option<PlanId>.None, TestMaker)),
            [typeof(Command.PlanSwap)] = () => Run(new Command.PlanSwap(structures, new SwapTo.Exactly(placement.Piece), Option<PlanId>.None, TestMaker)),
            [typeof(Command.PlanFinish)] = () => Run(new Command.PlanFinish(structures, finish, Option<PlanId>.None, TestMaker)),
            [typeof(Command.PlanRun)] = () => Run(new Command.PlanRun(new RunRequest(default, At, ValueList<Cell>.Empty, At, Option<PlanId>.None, TestMaker))),
            [typeof(Command.PlanRoute)] = () => Run(new Command.PlanRoute(new RouteRequest(default, At, At, new RouteToggles(false, false, false, false, false), Option<PlanId>.None, TestMaker))),
            [typeof(Command.Transform)] = () => Run(new Command.Transform(ids, new Transformation.Move(default), new TransformMode.Move())),
            [typeof(Command.PlaceTemplate)] = () => Run(new Command.PlaceTemplate(new Template(TestKitIds.Must(TemplateName.Create("T")), ValueList<GhostDraft>.Empty), At, default, draftPlan)),
            [typeof(Command.Repair)] = () => Run(new Command.Repair(GhostBoard.DefaultPlan, TestMaker)),
            [typeof(Command.TakeDown)] = () => Run(new Command.TakeDown(GhostBoard.DefaultPlan, TestMaker)),
            [typeof(Command.PlaceTemplateIn)] = () => Run(new Command.PlaceTemplateIn(new Template(TestKitIds.Must(TemplateName.Create("t")), ValueList<GhostDraft>.Empty), At, Turn.None, Option<PlanId>.None)),
            [typeof(Command.PlanSplit)] = () => Run(new Command.PlanSplit(new SplitTargets.Ghosts(ids), Option<PlanId>.None, TestMaker)),
            [typeof(Command.Reopen)] = () => Run(new Command.Reopen(GhostBoard.DefaultPlan)),

            [typeof(Intent.Build)] = () => Place(new Intent.Build(placement, Option<Finish>.None)),
            [typeof(Intent.Remove)] = () => Place(new Intent.Remove(structure)),
            [typeof(Intent.Swap)] = () => Place(new Intent.Swap(structure, placement with { At = new Cell(300, 100, 100) }, Option<Finish>.None)),
            [typeof(Intent.FinishOnly)] = () => Place(new Intent.FinishOnly(structure with { Id = new ReferenceId(6) }, finish)),

            [typeof(GhostEdit.SetLabel)] = () => Edit(new GhostEdit.SetLabel(Option<Label>.None)),
            [typeof(GhostEdit.SetTint)] = () => Edit(new GhostEdit.SetTint(Option<Tint>.None)),
            [typeof(GhostEdit.JoinGroup)] = () => Edit(new GhostEdit.JoinGroup(group)),
            [typeof(GhostEdit.LeaveGroup)] = () => Edit(new GhostEdit.LeaveGroup(group)),
            [typeof(GhostEdit.SetFinish)] = () => Edit(new GhostEdit.SetFinish(Option.Some(finish))),
            [typeof(GhostEdit.SetVisibility)] = () => Edit(new GhostEdit.SetVisibility(new Visibility.OwnerOnly())),
            [typeof(GhostEdit.Attach)] = () => Edit(new GhostEdit.Attach(key, default)),
            [typeof(GhostEdit.Detach)] = () => Edit(new GhostEdit.Detach(key)),

            [typeof(PlanEdit.Rename)] = () => EditPlan(new PlanEdit.Rename(name)),
            [typeof(PlanEdit.ChangeOwner)] = () => EditPlan(new PlanEdit.ChangeOwner(Bob)),
            [typeof(PlanEdit.ChangeVisibility)] = () => EditPlan(new PlanEdit.ChangeVisibility(new Visibility.Everyone())),
            [typeof(PlanEdit.SetData)] = () => EditPlan(new PlanEdit.SetData(key, default)),
            [typeof(PlanEdit.ClearData)] = () => EditPlan(new PlanEdit.ClearData(key)),

            [typeof(Selector.Everything)] = () => Pick(new Selector.Everything()),
            [typeof(Selector.Ids)] = () => Pick(ids),
            [typeof(Selector.Box)] = () => Pick(new Selector.Box(At, At)),
            [typeof(Selector.RunThrough)] = () => Pick(new Selector.RunThrough(At)),
            [typeof(Selector.Network)] = () => Pick(new Selector.Network(default)),
            [typeof(Selector.Room)] = () => Pick(new Selector.Room(default)),
            [typeof(Selector.Group)] = () => Pick(new Selector.Group(group)),
            [typeof(Selector.Owner)] = () => Pick(new Selector.Owner(Alice)),
            [typeof(Selector.MadeBy)] = () => Pick(new Selector.MadeBy(TestMaker)),
            [typeof(Selector.InPlan)] = () => Pick(new Selector.InPlan(GhostBoard.DefaultPlan)),
            [typeof(Selector.Tagged)] = () => Pick(new Selector.Tagged(key, Option<Attachment>.None)),
            [typeof(Selector.OfPiece)] = () => Pick(new Selector.OfPiece(placement.Piece)),
            [typeof(Selector.All)] = () => Pick(new Selector.All(ValueList.Of<Selector>(ids))),
            [typeof(Selector.Any)] = () => Pick(new Selector.Any(ValueList.Of<Selector>(ids))),

            [typeof(GhostState.Planned)] = () => Mark(t.Board.MarkPlanned(ghost.Id)),
            [typeof(GhostState.Started)] = () => Mark(t.Board.MarkStarted(ghost.Id, structure)),
            [typeof(GhostState.Stale)] = () => Mark(t.Board.MarkStale(ghost.Id, Reason.Of(ReasonCodes.Different, RuleId.Contract))),
        };
    }
}
