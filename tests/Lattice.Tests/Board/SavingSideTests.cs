using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Saving;
using Lattice.TestKit;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Board;

/// <summary>The board's side of saving: finishing a plan, catching a plan up to its file, and the plans view's saving reads.</summary>
public sealed class SavingSideTests
{
    private static readonly PlanName Hall = TestKitIds.Must(PlanName.Create("Hall"));

    private static PlanDraft Draft() => new(Hall, TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);

    private static PlanId NewPlan(TestBoard t) => Applied(t.Apply(new Command.CreatePlan(Draft()))).PlansOpened[0].Id;

    private static GhostDraft Frame(TestBoard t, int x, PlanId plan) => t.Draft("StructureFrame", new Cell(x, 10, 10), plan: Option.Some(plan));

    [Fact]
    public void Finishing_a_plan_takes_it_and_its_ghosts_off_the_board_in_one_change_set()
    {
        var t = new TestBoard();
        var plan = NewPlan(t);
        var ghost = t.Placed(Frame(t, 10, plan)).Added[0].Id;

        var set = Applied(t.Apply(new Command.FinishPlan(plan)));

        Assert.Equal(ValueList.Of(plan), set.PlansFinished);
        Assert.Equal(ghost, Assert.Single(set.Removed).Ghost.Id);
        Assert.True(new PlansView(t.Board, Alice).Find(plan).IsNone);
        Assert.True(t.View.Find(ghost).IsNone);
    }

    [Fact]
    public void The_default_plan_is_never_finished_and_an_unknown_plan_is_not_found()
    {
        var t = new TestBoard();

        Assert.Equal(ReasonCodes.NotAllowed, Refused(t.Apply(new Command.FinishPlan(GhostBoard.DefaultPlan))).Why.Code);
        Assert.Equal(ReasonCodes.NotFound, Refused(t.Apply(new Command.FinishPlan(new PlanId(99)))).Why.Code);
    }

    [Fact]
    public void A_refused_batch_after_a_finish_leaves_the_plan_and_its_ghosts_on_the_board()
    {
        var t = new TestBoard();
        var plan = NewPlan(t);
        var ghost = t.Placed(Frame(t, 10, plan)).Added[0].Id;

        var refused = Refused(t.Apply(new Command.Batch(ValueList.From(new Command[]
        {
            new Command.FinishPlan(plan),
            new Command.Place(ValueList.Of(Frame(t, 30, plan))),
        }))));

        Assert.Equal(ReasonCodes.NotFound, refused.Why.Code);
        Assert.True(t.View.Find(ghost).IsSome);
        Assert.True(new PlansView(t.Board, Alice).Find(plan).IsSome);
    }

    [Fact]
    public void A_finish_cannot_be_reverted()
    {
        var t = new TestBoard();
        var plan = NewPlan(t);
        var set = Applied(t.Apply(new Command.FinishPlan(plan)));

        Assert.Equal(ReasonCodes.NotAllowed, Refused(t.Apply(new Command.Revert(set.Id))).Why.Code);
    }

    [Fact]
    public void Without_saving_catch_up_is_module_missing_and_the_saving_reads_are_empty()
    {
        var t = new TestBoard();
        var plans = new PlansView(t.Board, Alice);

        Assert.Equal(ReasonCodes.ModuleMissing, Refused(t.Apply(new Command.CatchUp(GhostBoard.DefaultPlan))).Why.Code);
        Assert.True(plans.LastLoad.IsNone);
        Assert.Empty(plans.Finished);
        Assert.True(plans.AheadOfSave(GhostBoard.DefaultPlan).TryGetError(out var why) && why.Code == ReasonCodes.ModuleMissing);
    }

    [Fact]
    public void The_plans_view_reads_what_saving_answers()
    {
        var t = new TestBoard();
        var saved = new FakeSaved { Ahead = 3 };
        saved.FinishedPlans.Add(new FinishedPlan(new PlanId(7), Hall, Alice, t.Time.Now, 4, new Cell(10, 10, 10), new Cell(30, 10, 10)));
        t.Board.InstallSaving(saved);
        var plans = new PlansView(t.Board, Alice);

        Assert.Equal(new PlanId(7), Assert.Single(plans.Finished).Id);
        Assert.Equal(Result<int, Reason>.Ok(3), plans.AheadOfSave(GhostBoard.DefaultPlan));
        Assert.Equal(saved.LastLoad, plans.LastLoad);
    }

    [Fact]
    public void Catch_up_sets_the_plan_to_its_files_newest_state_with_ids_kept_and_tells_saving_the_set()
    {
        var t = new TestBoard();
        var kept = t.Placed(t.Draft("StructureFrame", new Cell(10, 10, 10))).Added[0];
        var gone = t.Placed(t.Draft("StructureFrame", new Cell(30, 10, 10))).Added[0];
        var back = kept with { Id = new GhostId(40), Intent = new Intent.Build(new Placement(t.Catalogue.Named("StructureFrame").Key, new Cell(50, 10, 10), default), Option<Finish>.None) };
        var saved = new FakeSaved();
        saved.Newest[GhostBoard.DefaultPlan] = new NewestPlan(Option<Plan>.None, false, ValueList.From(new[] { kept, back }));
        t.Board.InstallSaving(saved);

        var set = Applied(t.Apply(new Command.CatchUp(GhostBoard.DefaultPlan)));

        Assert.Equal(gone.Id, Assert.Single(set.Removed).Ghost.Id);
        Assert.Equal(back.Id, Assert.Single(set.Added).Id);
        Assert.Empty(set.Changed);
        Assert.Equal((GhostBoard.DefaultPlan, set.Id), Assert.Single(saved.CaughtUpSets));
        Assert.True(t.View.Find(new GhostId(40)).IsSome);
    }

    [Fact]
    public void Catch_up_opens_a_plan_the_save_was_older_than_and_finishes_one_finished_in_its_file()
    {
        var t = new TestBoard();
        var hidden = new Plan(new PlanId(5), new Revision(1), Hall, Alice, TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty);
        var finishing = NewPlan(t);
        var saved = new FakeSaved();
        saved.Newest[hidden.Id] = new NewestPlan(Option.Some(hidden), false, ValueList<Ghost>.Empty);
        saved.Newest[finishing] = new NewestPlan(new PlansView(t.Board, Alice).Find(finishing), true, ValueList<Ghost>.Empty);
        t.Board.InstallSaving(saved);

        var opened = Applied(t.Apply(new Command.CatchUp(hidden.Id)));
        var finished = Applied(t.Apply(new Command.CatchUp(finishing)));

        Assert.Equal(hidden.Id, Assert.Single(opened.PlansOpened).Id);
        Assert.Equal(ValueList.Of(finishing), finished.PlansFinished);
        Assert.True(new PlansView(t.Board, Alice).Find(hidden.Id).IsSome);
    }

    [Fact]
    public void Catch_up_replaces_another_plans_ghost_in_a_spot_it_restores_and_is_refused_inside_a_batch()
    {
        var t = new TestBoard();
        var plan = NewPlan(t);
        var other = t.Placed(Frame(t, 10, GhostBoard.DefaultPlan)).Added[0];
        var restored = other with { Id = new GhostId(50), Plan = plan };
        var saved = new FakeSaved();
        saved.Newest[plan] = new NewestPlan(new PlansView(t.Board, Alice).Find(plan), false, ValueList.Of(restored));
        t.Board.InstallSaving(saved);

        var batch = Refused(t.Apply(new Command.Batch(ValueList.Of<Command>(new Command.CatchUp(plan)))));
        var set = Applied(t.Apply(new Command.CatchUp(plan)));

        Assert.Equal(ReasonCodes.InvalidValue, batch.Why.Code);
        Assert.Equal(new RemovalCause.Replaced(restored.Id), Assert.Single(set.Removed).Cause);
        Assert.Equal(other.Id, set.Removed[0].Ghost.Id);
    }

    [Fact]
    public void Catch_up_with_nothing_ahead_is_refused_with_what_saving_says()
    {
        var t = new TestBoard();
        t.Board.InstallSaving(new FakeSaved());

        Assert.Equal(ReasonCodes.NotFound, Refused(t.Apply(new Command.CatchUp(GhostBoard.DefaultPlan))).Why.Code);
    }

    private sealed class FakeSaved : ISavedPlans
    {
        public int Ahead { get; init; }

        public Dictionary<PlanId, NewestPlan> Newest { get; } = new();

        public List<FinishedPlan> FinishedPlans { get; } = new();

        public List<(PlanId, ChangeSetId)> CaughtUpSets { get; } = new();

        public Option<LoadReport> LastLoad { get; } = Option.Some(new LoadReport(new SaveRecord.Found(), ValueList<PlanAhead>.Empty));

        public ValueList<FinishedPlan> Finished => ValueList.From(FinishedPlans);

        public Result<int, Reason> AheadOfSave(PlanId plan) => Result<int, Reason>.Ok(Ahead);

        Result<NewestPlan, Reason> ISavedPlans.Newest(PlanId plan) => Newest.TryGetValue(plan, out var newest)
            ? Result<NewestPlan, Reason>.Ok(newest)
            : Result<NewestPlan, Reason>.Error(Reason.Of(ReasonCodes.NotFound, SavingReasons.Saving));

        public void CaughtUp(PlanId plan, ChangeSetId set) => CaughtUpSets.Add((plan, set));

        public Result<FinishedContents, Reason> FinishedContents(PlanId plan) =>
            Result<FinishedContents, Reason>.Error(Reason.Of(ReasonCodes.NotFound, SavingReasons.Saving));
    }
}
