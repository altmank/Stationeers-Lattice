using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Commands;
using Lattice.Core.FinishedPlans;
using Lattice.Core.Saving;
using Lattice.TestKit;
using Lattice.Tests.Board;
using static Lattice.Tests.FinishedPlans.FinishedBench;

namespace Lattice.Tests.FinishedPlans;

/// <summary>What a finished plan offers: repair, take-down, save as template, reopen.</summary>
public sealed class FinishedPlanTests
{
    private static Finish Painted(int colour) =>
        TestKitIds.Must(Finish.Create(Option.Some(TestKitIds.Must(PaintColour.Create(colour))), Option<ThingName>.None, Option<Setting>.None));

    /// <summary>A plan of three walls and a truss, all built, then finished.</summary>
    private static (PlanId Plan, StructureRef[] Walls, StructureRef Truss) BuiltAndFinished(FinishedBench b)
    {
        var plan = b.NewPlan("Hall");
        var walls = new[] { 0, 1, 2 }.Select(i => b.Construct(b.Plan(plan, "ModPanel", Large(i)))).ToArray();
        var truss = b.Construct(b.Plan(plan, "ModTruss", Large(5)));
        b.Finish(plan);
        return (plan, walls, truss);
    }

    [Fact]
    public void Repair_after_one_wall_is_removed_makes_exactly_one_ghost()
    {
        using var b = new FinishedBench();
        var (plan, walls, _) = BuiltAndFinished(b);
        b.TakeApart(walls[1]);

        var draft = Assert.Single(Drafts(b.Repair(plan).Found));

        Assert.Equal(new Intent.Build(b.At("ModPanel", Large(1)), Option<Finish>.None), draft.Intent);
        Assert.Equal(FinishedBench.Asker, draft.MadeBy);
    }

    [Fact]
    public void Repair_makes_a_new_plan_named_after_the_finished_one_and_applies_as_ordinary_ghosts()
    {
        using var b = new FinishedBench();
        var (plan, walls, _) = BuiltAndFinished(b);
        b.TakeApart(walls[0]);

        var create = Assert.IsType<Command.CreatePlan>(TestKitIds.Must(b.Repair(plan).Found));
        var set = TestBoard.Applied(b.Apply(create));

        Assert.Equal("Hall repair", create.Draft.Name.Value);
        Assert.Equal(FinishedBench.Asker, create.Draft.MadeBy);
        var repairPlan = Assert.Single(set.PlansOpened).Id;
        Assert.NotEqual(plan, repairPlan);
        Assert.Equal(repairPlan, Assert.Single(set.Added).Plan);
    }

    [Fact]
    public void Repair_of_a_repainted_wall_is_a_finish_ghost_on_it()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Hall");
        var wall = b.Construct(b.Plan(plan, "ModPanel", Large(0), Option.Some(Painted(3))));
        b.Finish(plan);
        b.World.Paint(wall.Id, TestKitIds.Must(PaintColour.Create(5)));
        b.Flush();

        var draft = Assert.Single(Drafts(b.Repair(plan).Found));

        Assert.Equal(new Intent.FinishOnly(wall, Painted(3)), draft.Intent);
    }

    [Fact]
    public void Repair_of_a_wall_swapped_for_another_piece_is_a_swap_back()
    {
        using var b = new FinishedBench();
        var (plan, walls, _) = BuiltAndFinished(b);
        b.TakeApart(walls[2]);
        var pane = b.Stray("ModPane", Large(2));

        var draft = Assert.Single(Drafts(b.Repair(plan).Found));

        Assert.Equal(new Intent.Swap(pane, b.At("ModPanel", Large(2)), Option<Finish>.None), draft.Intent);
    }

    [Fact]
    public void Repair_with_nothing_gone_or_changed_is_refused_naming_the_plan()
    {
        using var b = new FinishedBench();
        var (plan, _, _) = BuiltAndFinished(b);

        var why = Refusal(b.Repair(plan).Found);

        Assert.Equal(ReasonCodes.NotFound, why.Code);
        Assert.Equal("nothing-to-repair", why.Facts["problem"]);
        Assert.Equal(plan.Value.ToString(), why.Facts["plan"]);
    }

    [Fact]
    public void Take_down_skips_pieces_already_gone()
    {
        using var b = new FinishedBench();
        var (plan, walls, truss) = BuiltAndFinished(b);
        b.TakeApart(walls[0]);

        var drafts = Drafts(b.TakeDown(plan).Found);

        Assert.Equal(
            new Intent[] { new Intent.Remove(walls[1]), new Intent.Remove(walls[2]), new Intent.Remove(truss) },
            drafts.Select(d => d.Intent).ToArray());
    }

    [Fact]
    public void Take_down_of_a_plan_whose_pieces_are_all_gone_is_refused()
    {
        using var b = new FinishedBench();
        var (plan, walls, truss) = BuiltAndFinished(b);
        foreach (var wall in walls)
            b.TakeApart(wall);
        b.TakeApart(truss);

        var why = Refusal(b.TakeDown(plan).Found);

        Assert.Equal("nothing-stands", why.Facts["problem"]);
    }

    [Fact]
    public void Take_down_is_a_plan_of_remove_ghosts_named_after_the_finished_one()
    {
        using var b = new FinishedBench();
        var (plan, _, _) = BuiltAndFinished(b);

        var create = Assert.IsType<Command.CreatePlan>(TestKitIds.Must(b.TakeDown(plan).Found));
        var set = TestBoard.Applied(b.Apply(create));

        Assert.Equal("Hall take-down", create.Draft.Name.Value);
        Assert.Equal(4, set.Added.Count);
        Assert.All(set.Added, g => Assert.IsType<Intent.Remove>(g.Intent));
    }

    [Fact]
    public void A_large_plan_is_checked_in_slices()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Long hall");
        for (var i = 0; i < FinishedPlanWork.PerStep + 6; i++)
            b.Construct(b.Plan(plan, "ModTruss", Large(i)));
        b.Finish(plan);

        var (found, steps) = b.TakeDown(plan);

        Assert.Equal(FinishedPlanWork.PerStep + 6, Drafts(found).Count);
        Assert.Equal(3, steps);
    }

    [Fact]
    public void Repair_and_take_down_of_an_active_plan_are_refused_not_finished()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Hall");

        Assert.Equal("not-finished", Refusal(b.Repair(plan).Found).Facts["problem"]);
        Assert.Equal(ReasonCodes.NotAllowed, Refusal(b.TakeDown(plan).Found).Code);
    }

    [Fact]
    public void A_finished_plan_the_actor_may_not_see_is_not_found()
    {
        using var b = new FinishedBench();
        var (plan, walls, _) = BuiltAndFinished(b);
        b.TakeApart(walls[0]);
        Assert.True(b.T.Policies.Install(new AccessPolicy(
            static (_, _) => true, static (viewer, p) => viewer == TestBoard.Alice, static _ => Result<Unit, Reason>.Ok(Unit.Value))).IsOk);

        Assert.Equal(ReasonCodes.NotFound, Refusal(b.Repair(plan, TestBoard.Bob).Found).Code);
        Assert.Single(Drafts(b.Repair(plan, TestBoard.Alice).Found));
    }

    [Fact]
    public void A_finished_plan_as_a_template_holds_what_it_built()
    {
        using var b = new FinishedBench();
        var (plan, _, _) = BuiltAndFinished(b);
        var name = TestKitIds.Must(TemplateName.Create("Hall"));

        var template = TestKitIds.Must(new PlansView(b.Board, TestBoard.Alice).AsTemplate(plan, name));

        Assert.Equal(4, template.Ghosts.Count);
        Assert.Equal(3, template.Ghosts.Count(g => g.Intent is Intent.Build { Target.Piece: var piece } && piece == b.Key("ModPanel")));
    }

    [Fact]
    public void Reopen_keeps_every_line_of_history()
    {
        using var b = new FinishedBench();
        var (plan, _, _) = BuiltAndFinished(b);
        var before = b.PlanText(plan);
        var finishLine = PlanTree.Read(plan, before).Tree.Newest;

        var set = TestBoard.Applied(b.Apply(new Command.Reopen(plan)));

        var after = b.PlanText(plan);
        Assert.StartsWith(before, after);
        var tree = PlanTree.Read(plan, after).Tree;
        Assert.Equal(PlanTree.Read(plan, before).Tree.Count + 1, tree.Count);
        Assert.Equal(finishLine, tree.Line(tree.Newest).Follows);
        Assert.Equal(plan, Assert.Single(set.PlansOpened).Id);
    }

    [Fact]
    public void A_reopened_plan_is_active_again_with_its_file_back_and_out_of_the_index()
    {
        using var b = new FinishedBench();
        var (plan, _, _) = BuiltAndFinished(b);
        Assert.Contains(b.Saving.Finished, f => f.Id == plan);

        TestBoard.Applied(b.Apply(new Command.Reopen(plan)));

        Assert.True(File.Exists(b.Folder.PlanPath(plan)));
        Assert.False(File.Exists(b.Folder.ArchivedPath(plan)));
        Assert.DoesNotContain(b.Saving.Finished, f => f.Id == plan);
        Assert.Equal("Hall", new PlansView(b.Board, TestBoard.Alice).Find(plan).TryGet(out var found) ? found.Name.Value : "");

        b.Reload(Option.Some(b.Save()));
        Assert.True(new PlansView(b.Board, TestBoard.Alice).Find(plan).IsSome);
    }

    [Fact]
    public void Reopen_gives_back_the_ghosts_the_plan_held_when_it_finished_with_their_ids()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Hall");
        b.Construct(b.Plan(plan, "ModPanel", Large(0)));
        var unbuilt = b.Plan(plan, "ModTruss", Large(1));
        b.Finish(plan);

        var set = TestBoard.Applied(b.Apply(new Command.Reopen(plan)));

        var back = Assert.Single(set.Added);
        Assert.Equal(unbuilt.Id, back.Id);
        Assert.Equal(unbuilt.Intent, back.Intent);
        Assert.Equal(plan, back.Plan);
    }

    [Fact]
    public void Reopen_after_a_load_follows_the_finishing_line_in_the_archived_file()
    {
        using var b = new FinishedBench();
        var (plan, _, _) = BuiltAndFinished(b);
        b.Reload(Option.Some(b.Save()));
        var before = b.PlanText(plan);
        var finishLine = PlanTree.Read(plan, before).Tree.Newest;

        TestBoard.Applied(b.Apply(new Command.Reopen(plan)));

        var tree = PlanTree.Read(plan, b.PlanText(plan)).Tree;
        Assert.StartsWith(before, b.PlanText(plan));
        Assert.Equal(finishLine, tree.Line(tree.Newest).Follows);
        Assert.True(File.Exists(b.Folder.PlanPath(plan)));
    }

    [Fact]
    public void Reopen_of_an_active_or_unknown_plan_is_refused_and_without_saving_is_module_missing()
    {
        using var b = new FinishedBench();
        var active = b.NewPlan("Hall");

        Assert.Equal("not-finished", TestBoard.Refused(b.Apply(new Command.Reopen(active))).Why.Facts["problem"]);
        Assert.Equal(ReasonCodes.NotFound, TestBoard.Refused(b.Apply(new Command.Reopen(new PlanId(77)))).Why.Code);
        Assert.Equal(ReasonCodes.ModuleMissing, TestBoard.Refused(new TestBoard().Apply(new Command.Reopen(active))).Why.Code);
    }

    [Fact]
    public void Without_saving_repair_is_module_missing()
    {
        var t = new TestBoard();
        var expander = new FinishedPlansExpander(t.Board, null!, null!);
        var work = expander.Repair.Start(new ExpansionInput<Command.Repair>(
            new Command.Repair(new PlanId(5), FinishedBench.Asker), new CommandId(System.Guid.NewGuid()), TestBoard.Alice, t.View, null!));
        work.Step();

        Assert.Equal(ReasonCodes.ModuleMissing, Refusal(work.Found).Code);
    }
}
