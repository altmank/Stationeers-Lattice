using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Executors;
using Lattice.Tests.Board;
using Lattice.TestKit;

namespace Lattice.Tests.Executors;

/// <summary>The builders' shared parts: a bench with plans and builders, and short ways to make what they say.</summary>
internal static class Builders
{
    public static readonly RuleId Dock = TestKitIds.Must(RuleId.Create(TestKitIds.Mod, "dock"));

    public static ExecutorId Id(string name) => TestKitIds.Must(ExecutorId.Create(TestKitIds.Mod, name));

    public static PlanId NewPlan(ExecutorsBench b, string name = "Hall", Option<Tint> colour = default)
    {
        var draft = new PlanDraft(TestKitIds.Must(PlanName.Create(name)), TestBoard.TestMaker, new Visibility.Everyone(), ValueMap<AttachmentKey, Attachment>.Empty) { Colour = colour };
        return TestBoard.Applied(b.T.Apply(new Command.CreatePlan(draft))).PlansOpened[0].Id;
    }

    public static GhostId GhostIn(ExecutorsBench b, PlanId plan, int at)
    {
        var draft = b.T.Draft("StructureFrame", new Cell(40 + 2 * at, 10, 10), plan: Option.Some(plan));
        return TestBoard.Applied(b.T.Place(draft)).Added[0].Id;
    }

    public static Outcome Edit(ExecutorsBench b, PlanId plan, PlanEdit edit) =>
        b.T.Apply(new Command.EditPlan(plan, edit, Option<Revision>.None));

    public static void Assign(ExecutorsBench b, PlanId plan, ExecutorId builder) => TestBoard.Applied(Edit(b, plan, new PlanEdit.Assign(builder)));

    public static ItemQuantity Item(int hash, int count) => TestKitIds.Must(ItemQuantity.Create(new ItemKey(hash), count));

    public static SupplyReport Supply(ValueList<ItemQuantity> reaches, params (PlanId Plan, ItemQuantity[] Items)[] toward)
    {
        var map = new Dictionary<PlanId, ValueList<ItemQuantity>>();
        foreach (var (plan, items) in toward)
            map[plan] = ValueList.From(items);
        return new SupplyReport(reaches, ValueMap<PlanId, ValueList<ItemQuantity>>.From(map));
    }

    public static Builder Read(ExecutorsBench b, ExecutorId id) =>
        b.Executors.Find(id).Reduce(() => throw new Xunit.Sdk.XunitException($"{id} is not registered"));

    public static void Complete(ExecutorsBench b, GhostId ghost) =>
        Assert.True(b.T.Board.Complete(ghost, new Completion(Option<StructureRef>.None, b.T.Time.Now)).IsOk);
}

/// <summary>
/// A builder describes itself; Lattice keeps the word and never checks it. Identity is fixed at registration; area,
/// word, supply and answers come only from its handle, go with the handle, and an equal report changes nothing. Its
/// state comes from its claims first: a claim reported working (or not reported on) makes it working over a stuck
/// word; waiting and paused claims do not.
/// </summary>
public sealed class BuilderWordTests
{
    private static readonly Reason NoPower = Reason.NoPower(Builders.Dock);

    [Fact]
    public void A_registered_builder_is_listed_with_who_it_is_idle_and_nowhere()
    {
        var b = new ExecutorsBench();
        var owner = TestBoard.Alice;
        var info = ExecutorsBench.Info("dock") with { Owner = Option.Some(owner), Builds = new BuildScope.Kits(ValueSet.Of(new KitKey(7))) };
        TestKitIds.Must(b.Executors.Register(info));

        var dock = Assert.Single(b.Executors.All);

        Assert.Equal(info, dock.Info);
        Assert.Equal(TestKitIds.Mod, dock.Info.Id.Mod);
        Assert.IsType<BuilderState.Idle>(dock.State);
        Assert.True(dock.Area.IsNone);
        Assert.True(dock.Supply.IsNone);
    }

    [Fact]
    public void What_it_says_is_told_once_per_burst_and_an_equal_word_changes_nothing()
    {
        var b = new ExecutorsBench();
        var told = new List<ExecutorId>();
        b.T.Events.BuilderChanged += told.Add;
        var dock = b.Register("dock");
        var area = new BuilderArea.Sphere(new WorldPoint(1, 2, 3), TestKitIds.Must(Metres.Create(10)), Option.Some(TestKitIds.Must(Metres.Create(20))));
        b.Flush();
        told.Clear();

        dock.Locate(Option.Some<BuilderArea>(area));
        dock.Say(new BuilderWord.Stuck(NoPower));
        b.Flush();
        Assert.Equal(new[] { Builders.Id("dock") }, told);

        var all = b.Executors.All;
        dock.Locate(Option.Some<BuilderArea>(area with { }));
        dock.Say(new BuilderWord.Stuck(NoPower));
        b.Flush();
        Assert.Single(told);
        Assert.Same(all, b.Executors.All);
        Assert.Equal(new BuilderState.Stuck(NoPower), Builders.Read(b, Builders.Id("dock")).State);
    }

    [Fact]
    public void A_builder_goes_with_its_handle_and_its_word_with_it()
    {
        var b = new ExecutorsBench();
        var told = new List<ExecutorId>();
        b.T.Events.BuilderChanged += told.Add;
        var dock = b.Register("dock");
        dock.Say(new BuilderWord.Stuck(NoPower));
        b.Flush();
        told.Clear();

        dock.Dispose();
        b.Flush();

        Assert.Empty(b.Executors.All);
        Assert.True(b.Executors.Find(Builders.Id("dock")).IsNone);
        Assert.Equal(new[] { Builders.Id("dock") }, told);
        var again = b.Register("dock");
        Assert.IsType<BuilderState.Idle>(Builders.Read(b, Builders.Id("dock")).State);
        again.Dispose();
    }

    [Fact]
    public void Live_work_beats_a_stuck_word_and_waiting_claims_are_not_work()
    {
        var b = new ExecutorsBench();
        var dock = b.Register("dock");
        var first = b.Ghost();
        var second = b.Ghost();
        dock.Say(new BuilderWord.Stuck(Reason.NeedsItems(Builders.Dock, Builders.Item(1, 1))));
        var id = Builders.Id("dock");

        TestKitIds.Must(dock.Claim(first, ExecutorsBench.Seconds(30)).Match(static c => Result<Claim, Reason>.Ok(c), static r => Result<Claim, Reason>.Error(r.Why)));
        Assert.IsType<BuilderState.Working>(Builders.Read(b, id).State);

        TestKitIds.Must(dock.Report(first, new ExecutorStatus.Waiting(NoPower)));
        Assert.IsType<BuilderState.Stuck>(Builders.Read(b, id).State);

        TestKitIds.Must(dock.Claim(second, ExecutorsBench.Seconds(30)).Match(static c => Result<Claim, Reason>.Ok(c), static r => Result<Claim, Reason>.Error(r.Why)));
        TestKitIds.Must(dock.Report(second, new ExecutorStatus.Working(1, 3)));
        Assert.IsType<BuilderState.Working>(Builders.Read(b, id).State);

        TestKitIds.Must(dock.Report(second, new ExecutorStatus.Paused()));
        Assert.IsType<BuilderState.Stuck>(Builders.Read(b, id).State);

        dock.Say(new BuilderWord.Ready());
        Assert.IsType<BuilderState.Idle>(Builders.Read(b, id).State);
    }

    [Fact]
    public void A_steady_read_allocates_nothing()
    {
        var b = new ExecutorsBench();
        b.Register("dock");
        var plan = Builders.NewPlan(b);
        var id = Builders.Id("dock");
        Builders.Assign(b, plan, id);
        void ReadAll() =>
            _ = (b.Executors.All, b.Executors.AssignedTo(plan), b.Executors.PlansOf(id), b.Executors.SupplyToward(plan),
                b.Executors.ProgressOf(plan), b.Executors.ProgressOf(plan, id), b.Executors.UnclaimedIn(plan), b.Executors.Find(id));
        ReadAll();

        var before = System.GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            ReadAll();

        Assert.Equal(before, System.GC.GetAllocatedBytesForCurrentThread());
    }
}

/// <summary>
/// StatusChanged is raised for every claim taken, released, lapsed or dropped and every
/// changed report, never a renewal; a completion names the executor that held the claim.
/// </summary>
public sealed class ClaimEventTests
{
    [Fact]
    public void Taking_releasing_and_lapsing_a_claim_each_raise_one_status_change_and_a_renewal_none()
    {
        var b = new ExecutorsBench();
        var dock = b.Register("dock");
        var ghost = b.Ghost();
        var other = b.Ghost();

        var claim = TestKitIds.Must(dock.Claim(ghost, ExecutorsBench.Seconds(30)).Match(static c => Result<Claim, Reason>.Ok(c), static r => Result<Claim, Reason>.Error(r.Why)));
        b.Flush();
        Assert.Equal(new[] { ghost }, b.StatusChanges);

        TestKitIds.Must(dock.Renew(claim).Match(static c => Result<Claim, Reason>.Ok(c), static r => Result<Claim, Reason>.Error(r.Why)));
        b.Flush();
        Assert.Single(b.StatusChanges);

        dock.Release(claim);
        b.Flush();
        Assert.Equal(new[] { ghost, ghost }, b.StatusChanges);

        dock.Claim(other, ExecutorsBench.Seconds(5));
        b.Flush();
        b.Clock.Advance(6);
        b.Executors.Lapse();
        b.Flush();
        Assert.Equal(new[] { ghost, ghost, other, other }, b.StatusChanges);
    }

    [Fact]
    public void A_completion_names_the_executor_holding_the_claim_and_none_for_an_unclaimed_ghost()
    {
        var b = new ExecutorsBench();
        var dock = b.Register("dock");
        var claimed = b.Ghost();
        var free = b.Ghost();
        dock.Claim(claimed, ExecutorsBench.Seconds(30));
        var done = new Dictionary<GhostId, Completion>();
        b.T.Events.Completed += (ghost, how) => done[ghost] = how;
        var removed = new List<GhostRemoval>();
        b.T.Events.Changed += set => removed.AddRange(set.Removed);

        Builders.Complete(b, claimed);
        Builders.Complete(b, free);
        b.Flush();

        Assert.Equal(Option.Some(Builders.Id("dock")), done[claimed].By);
        Assert.True(done[free].By.IsNone);
        Assert.Contains(removed, r => r.Cause is RemovalCause.Done { How.By: var by } && by == Option.Some(Builders.Id("dock")));
    }
}

/// <summary>
/// Supply toward a plan is exclusive; the sum is exact by contract. A builder's Toward is what it holds or has
/// reserved for a plan; Lattice sums the assigned builders' entries per item and cannot verify them. Reaches is per
/// builder and never summed. Past the caps a report is refused too-large, never cut down.
/// </summary>
public sealed class SupplyTests
{
    [Fact]
    public void A_plan_s_supply_is_its_assigned_builders_toward_entries_summed_per_item()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);
        var other = Builders.NewPlan(b, "Store");
        var a = b.Register("dock-a");
        var c = b.Register("dock-c");
        var helper = b.Register("helper");
        Builders.Assign(b, plan, Builders.Id("dock-a"));
        Builders.Assign(b, plan, Builders.Id("dock-c"));

        TestKitIds.Must(a.Offer(Builders.Supply(ValueList.Of(Builders.Item(1, 500)), (plan, new[] { Builders.Item(1, 10), Builders.Item(2, 3) }), (other, new[] { Builders.Item(1, 99) }))));
        TestKitIds.Must(c.Offer(Builders.Supply(ValueList.Of(Builders.Item(1, 500)), (plan, new[] { Builders.Item(1, 5) }))));
        TestKitIds.Must(helper.Offer(Builders.Supply(ValueList<ItemQuantity>.Empty, (plan, new[] { Builders.Item(1, 1000) }))));

        Assert.Equal(new[] { Builders.Item(1, 15), Builders.Item(2, 3) }, b.Executors.SupplyToward(plan));
        Assert.Empty(b.Executors.SupplyToward(other));
        Assert.Equal(ValueList.Of(Builders.Item(1, 500)), Builders.Read(b, Builders.Id("dock-a")).Supply.Reduce(() => throw new Xunit.Sdk.XunitException("no supply")).Report.Reaches);
    }

    [Fact]
    public void A_report_past_a_cap_is_refused_too_large_and_the_last_one_stands()
    {
        var b = new ExecutorsBench();
        var dock = b.Register("dock");
        var plan = Builders.NewPlan(b);
        var fits = Builders.Supply(ValueList.Of(Builders.Item(1, 1)));
        TestKitIds.Must(dock.Offer(fits));
        var many = new ItemQuantity[BuilderBook.MostItems + 1];
        for (var i = 0; i < many.Length; i++)
            many[i] = Builders.Item(i + 1, 1);

        Assert.True(dock.Offer(Builders.Supply(ValueList.From(many))).TryGetError(out var reaches));
        Assert.True(dock.Offer(Builders.Supply(ValueList<ItemQuantity>.Empty, (plan, many))).TryGetError(out var toward));

        Assert.Equal(ReasonCodes.TooLarge, reaches.Code);
        Assert.Equal("reaches", reaches.Facts["list"]);
        Assert.Equal(ReasonCodes.TooLarge, toward.Code);
        Assert.Equal(plan.Value.ToString(), toward.Facts["plan"]);
        Assert.Equal(fits, Builders.Read(b, Builders.Id("dock")).Supply.Reduce(() => throw new Xunit.Sdk.XunitException("no supply")).Report);
    }

    [Fact]
    public void Lattice_stamps_a_report_with_the_game_time_and_an_equal_report_keeps_its_stamp()
    {
        var b = new ExecutorsBench();
        var dock = b.Register("dock");
        var report = Builders.Supply(ValueList.Of(Builders.Item(1, 2)));
        TestKitIds.Must(dock.Offer(report));
        b.Clock.Advance(30);

        TestKitIds.Must(dock.Offer(report with { }));

        Assert.Equal(100, Builders.Read(b, Builders.Id("dock")).Supply.Reduce(() => throw new Xunit.Sdk.XunitException("no supply")).At.Seconds);
    }

    [Fact]
    public void A_toward_entry_for_a_plan_the_viewer_may_not_see_is_left_out_on_that_seat()
    {
        var b = new ExecutorsBench();
        var plan = Builders.NewPlan(b);
        var hidden = Builders.NewPlan(b, "Secret");
        var dock = b.Register("dock");
        Builders.Assign(b, hidden, Builders.Id("dock"));
        TestKitIds.Must(dock.Offer(Builders.Supply(ValueList<ItemQuantity>.Empty, (plan, new[] { Builders.Item(1, 1) }), (hidden, new[] { Builders.Item(2, 2) }))));
        using var policy = TestKitIds.Must(b.T.Policies.Install(new AccessPolicy(
            static (_, _) => true,
            (_, seen) => seen.Id != hidden,
            static _ => Result<Unit, Reason>.Ok(Unit.Value))));
        b.T.Apply(new Command.EditPlan(plan, new PlanEdit.Rename(TestKitIds.Must(PlanName.Create("Hall 2"))), Option<Revision>.None));

        var toward = Builders.Read(b, Builders.Id("dock")).Supply.Reduce(() => throw new Xunit.Sdk.XunitException("no supply")).Report.Toward;

        Assert.True(toward.ContainsKey(plan));
        Assert.False(toward.ContainsKey(hidden));
        Assert.Empty(b.Executors.SupplyToward(hidden));
    }
}
