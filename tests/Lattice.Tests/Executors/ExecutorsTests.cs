using System;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Executors;
using Lattice.Tests.Board;
using Lattice.TestKit;
using Lattice.TestKit.Allocations;
using Xunit;
using static Lattice.Tests.Executors.ExecutorsBench;

namespace Lattice.Tests.Executors;

/// <summary>Executors claim ghosts with leases on game time, report status while they hold them, and makers hear of blocked ghosts.</summary>
public sealed class ExecutorsTests
{
    private static readonly Reason NeedsSteel = Reason.Of(ReasonCodes.NotFound, TestKitIds.Catalogue, ValueMap.Of("item", "ItemSteelSheets"));

    // Claims

    [Fact]
    public void Two_executors_cannot_hold_one_ghost()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var first = bench.Register("first");
        var second = bench.Register("second");

        var held = Held(first.Claim(ghost, Seconds(30)));
        var refused = second.Claim(ghost, Seconds(30));

        Assert.True(refused.TryGetError(out var why));
        Assert.Equal(ReasonCodes.Claimed, why.Why.Code);
        Assert.Equal(Option.Some(held.By), why.HeldBy);
        Assert.Equal(Option.Some(held), bench.Executors.ClaimOn(ghost));
    }

    [Fact]
    public void A_claim_runs_for_its_lease_in_game_time()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();

        var claim = Held(bench.Register("drone").Claim(ghost, Seconds(30)));

        Assert.Equal(new GameTime(bench.Clock.Now.Seconds + 30), claim.Until);
    }

    [Fact]
    public void An_expired_lease_frees_the_ghost()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var first = bench.Register("first");
        Held(first.Claim(ghost, Seconds(30)));

        bench.Clock.Advance(30);

        Assert.True(bench.Executors.ClaimOn(ghost).IsNone);
        var taken = Held(bench.Register("second").Claim(ghost, Seconds(10)));
        Assert.Equal(ghost, taken.Ghost);
    }

    [Fact]
    public void A_lease_does_not_lapse_while_game_time_stands_still()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var claim = Held(bench.Register("first").Claim(ghost, Seconds(30)));

        bench.Executors.Lapse();
        bench.Executors.Lapse();

        Assert.Equal(Option.Some(claim), bench.Executors.ClaimOn(ghost));
    }

    [Fact]
    public void Renewing_extends_a_claim_by_its_lease_from_now()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var drone = bench.Register("drone");
        var claim = Held(drone.Claim(ghost, Seconds(30)));
        bench.Clock.Advance(20);

        var renewed = Held(drone.Renew(claim));
        bench.Clock.Advance(20);

        Assert.Equal(new GameTime(bench.Clock.Now.Seconds + 10), renewed.Until);
        Assert.Equal(Option.Some(renewed), bench.Executors.ClaimOn(ghost));
    }

    [Fact]
    public void Renewing_a_lapsed_claim_is_refused_lease_expired()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var drone = bench.Register("drone");
        var claim = Held(drone.Claim(ghost, Seconds(30)));
        bench.Clock.Advance(31);

        Assert.True(drone.Renew(claim).TryGetError(out var why));
        Assert.Equal(ReasonCodes.LeaseExpired, why.Why.Code);
        Assert.True(why.HeldBy.IsNone);
    }

    [Fact]
    public void Renewing_a_lapsed_claim_another_executor_took_names_the_holder()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var first = bench.Register("first");
        var claim = Held(first.Claim(ghost, Seconds(30)));
        bench.Clock.Advance(31);
        var taken = Held(bench.Register("second").Claim(ghost, Seconds(30)));

        Assert.True(first.Renew(claim).TryGetError(out var why));
        Assert.Equal(ReasonCodes.Claimed, why.Why.Code);
        Assert.Equal(Option.Some(taken.By), why.HeldBy);
    }

    [Fact]
    public void An_executor_cannot_renew_another_executors_claim()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var claim = Held(bench.Register("first").Claim(ghost, Seconds(30)));

        Assert.True(bench.Register("second").Renew(claim).TryGetError(out var why));
        Assert.Equal(ReasonCodes.NotAllowed, why.Why.Code);
    }

    [Fact]
    public void Claiming_a_ghost_it_holds_again_restarts_the_lease()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var drone = bench.Register("drone");
        Held(drone.Claim(ghost, Seconds(30)));
        bench.Clock.Advance(25);

        var again = Held(drone.Claim(ghost, Seconds(60)));

        Assert.Equal(new GameTime(bench.Clock.Now.Seconds + 60), again.Until);
    }

    [Fact]
    public void Releasing_frees_the_ghost_and_releasing_again_does_nothing()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var first = bench.Register("first");
        var claim = Held(first.Claim(ghost, Seconds(30)));

        first.Release(claim);
        first.Release(claim);

        Assert.True(bench.Executors.ClaimOn(ghost).IsNone);
        Held(bench.Register("second").Claim(ghost, Seconds(30)));
    }

    [Fact]
    public void Releasing_another_executors_claim_does_nothing()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var claim = Held(bench.Register("first").Claim(ghost, Seconds(30)));

        bench.Register("second").Release(claim);

        Assert.Equal(Option.Some(claim), bench.Executors.ClaimOn(ghost));
    }

    [Fact]
    public void Releasing_a_lapsed_claim_another_executor_took_leaves_that_claim()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var first = bench.Register("first");
        var old = Held(first.Claim(ghost, Seconds(30)));
        bench.Clock.Advance(31);
        var taken = Held(bench.Register("second").Claim(ghost, Seconds(30)));

        first.Release(old);

        Assert.Equal(Option.Some(taken), bench.Executors.ClaimOn(ghost));
    }

    [Fact]
    public void Disposing_an_executor_releases_every_claim_it_holds_and_frees_its_id()
    {
        var bench = new ExecutorsBench();
        var a = bench.Ghost();
        var b = bench.Ghost();
        var drone = bench.Register("drone");
        Held(drone.Claim(a, Seconds(30)));
        Held(drone.Claim(b, Seconds(30)));

        drone.Dispose();
        drone.Dispose();

        Assert.True(bench.Executors.ClaimOn(a).IsNone);
        Assert.True(bench.Executors.ClaimOn(b).IsNone);
        var again = bench.Register("drone");
        Held(again.Claim(a, Seconds(30)));
    }

    [Fact]
    public void A_disposed_executor_cannot_claim()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var drone = bench.Register("drone");
        drone.Dispose();

        Assert.True(drone.Claim(ghost, Seconds(30)).TryGetError(out var why));
        Assert.Equal(ReasonCodes.NotFound, why.Why.Code);
        Assert.Equal("executor", why.Why.Facts["problem"]);
    }

    [Fact]
    public void A_default_lease_is_refused()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();

        Assert.True(bench.Register("drone").Claim(ghost, default).TryGetError(out var why));
        Assert.Equal(ReasonCodes.DefaultValue, why.Why.Code);
    }

    [Fact]
    public void A_ghost_not_on_the_board_cannot_be_claimed()
    {
        var bench = new ExecutorsBench();

        Assert.True(bench.Register("drone").Claim(new GhostId(999), Seconds(30)).TryGetError(out var why));
        Assert.Equal(ReasonCodes.NotFound, why.Why.Code);
        Assert.Equal("999", why.Why.Facts["ghost"]);
    }

    [Fact]
    public void A_ghost_leaving_the_board_ends_its_claim()
    {
        var bench = new ExecutorsBench();
        var removed = bench.Ghost();
        var done = bench.Ghost();
        var drone = bench.Register("drone");
        Held(drone.Claim(removed, Seconds(30)));
        Held(drone.Claim(done, Seconds(30)));

        TestBoard.Applied(bench.T.Apply(new Command.Remove(TestBoard.Ids(removed))));
        TestKitIds.Must(bench.T.Board.Complete(done, new Completion(Option<StructureRef>.None, bench.T.Time.Now)));
        bench.Flush();

        Assert.True(bench.Executors.ClaimOn(removed).IsNone);
        Assert.True(bench.Executors.ClaimOn(done).IsNone);
    }

    [Fact]
    public void A_ghost_of_a_modded_piece_is_claimed_like_any_other()
    {
        var bench = new ExecutorsBench(WorldSeat.Host, Pieces(
            "piece ModBeam kit=ItemModBeamKit option=0 grid=large\n" +
            "  step material=ItemModBeamKit*1 exit=ItemModSpanner air=no\n"));
        var ghost = bench.Ghost("ModBeam");
        var drone = bench.Register("drone");

        Held(drone.Claim(ghost, Seconds(30)));
        TestKitIds.Must(drone.Report(ghost, new ExecutorStatus.Working(1, 1)));

        Assert.Equal(new ExecutorStatus.Working(1, 1), Assert.Single(bench.Executors.StatusOf(ghost)).Status);
    }

    // Registering

    [Fact]
    public void An_id_registered_twice_is_refused()
    {
        var bench = new ExecutorsBench();
        bench.Register("drone");

        Assert.True(bench.Executors.Register(Info("drone")).TryGetError(out var why));
        Assert.Equal(ReasonCodes.AlreadyRegistered, why.Code);
    }

    [Fact]
    public void A_client_cannot_register_an_executor()
    {
        var bench = new ExecutorsBench(WorldSeat.Client);

        Assert.True(bench.Executors.Register(Info("drone")).TryGetError(out var why));
        Assert.Equal(ReasonCodes.HostOnly, why.Code);
    }

    [Fact]
    public void An_executor_without_an_id_or_name_is_refused()
    {
        var bench = new ExecutorsBench();

        Assert.Equal(ReasonCodes.DefaultValue, Refusal(bench.Executors.Register(new ExecutorInfo(default, Info("x").Name))).Code);
        Assert.Equal(ReasonCodes.DefaultValue, Refusal(bench.Executors.Register(new ExecutorInfo(Info("x").Id, default))).Code);
        Assert.Equal(ReasonCodes.InvalidValue, Refusal(bench.Executors.Register(null!)).Code);
    }

    // Status

    [Fact]
    public void Status_is_what_the_holder_last_reported()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var drone = bench.Register("drone");
        Held(drone.Claim(ghost, Seconds(30)));

        TestKitIds.Must(drone.Report(ghost, new ExecutorStatus.Working(1, 3)));
        TestKitIds.Must(drone.Report(ghost, new ExecutorStatus.Working(2, 3)));

        var report = Assert.Single(bench.Executors.StatusOf(ghost));
        Assert.Equal(Info("drone"), report.By);
        Assert.Equal(new ExecutorStatus.Working(2, 3), report.Status);
    }

    [Fact]
    public void A_ghost_no_one_works_on_has_no_status()
    {
        var bench = new ExecutorsBench();

        Assert.Empty(bench.Executors.StatusOf(bench.Ghost()));
        Assert.Empty(bench.Executors.StatusOf(new GhostId(999)));
    }

    [Fact]
    public void Reporting_on_a_ghost_this_executor_does_not_hold_is_refused()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        Held(bench.Register("first").Claim(ghost, Seconds(30)));
        var second = bench.Register("second");

        Assert.Equal(ReasonCodes.Claimed, Refusal(second.Report(ghost, new ExecutorStatus.Paused())).Code);
        Assert.Equal(ReasonCodes.NotFound, Refusal(second.Report(bench.Ghost(), new ExecutorStatus.Paused())).Code);
        Assert.Empty(bench.Executors.StatusOf(ghost));
    }

    [Fact]
    public void Status_ends_with_the_claim()
    {
        var bench = new ExecutorsBench();
        var released = bench.Ghost();
        var lapsed = bench.Ghost();
        var drone = bench.Register("drone");
        var claim = Held(drone.Claim(released, Seconds(30)));
        Held(drone.Claim(lapsed, Seconds(10)));
        TestKitIds.Must(drone.Report(released, new ExecutorStatus.Working(1, 2)));
        TestKitIds.Must(drone.Report(lapsed, new ExecutorStatus.Working(1, 2)));

        drone.Release(claim);
        bench.Clock.Advance(10);

        Assert.Empty(bench.Executors.StatusOf(released));
        Assert.Empty(bench.Executors.StatusOf(lapsed));
    }

    [Fact]
    public void Each_status_change_raises_one_status_changed_per_ghost_in_the_next_burst()
    {
        var bench = new ExecutorsBench();
        var a = bench.Ghost();
        var b = bench.Ghost();
        var drone = bench.Register("drone");
        Held(drone.Claim(a, Seconds(30)));
        Held(drone.Claim(b, Seconds(30)));
        bench.Flush();
        bench.StatusChanges.Clear();

        TestKitIds.Must(drone.Report(a, new ExecutorStatus.Working(1, 2)));
        TestKitIds.Must(drone.Report(a, new ExecutorStatus.Working(2, 2)));
        TestKitIds.Must(drone.Report(b, new ExecutorStatus.Paused()));
        TestKitIds.Must(drone.Report(b, new ExecutorStatus.Paused()));
        Assert.Empty(bench.StatusChanges);
        bench.Flush();

        Assert.Equal([a, b], bench.StatusChanges);
    }

    [Fact]
    public void A_lapse_swept_at_frame_start_raises_status_changed_for_a_ghost_that_had_status()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var drone = bench.Register("drone");
        Held(drone.Claim(ghost, Seconds(10)));
        TestKitIds.Must(drone.Report(ghost, new ExecutorStatus.Working(1, 2)));
        bench.Flush();
        bench.StatusChanges.Clear();

        bench.Clock.Advance(10);
        bench.Executors.Lapse();
        bench.Flush();
        bench.Executors.Lapse();
        bench.Flush();

        Assert.Equal([ghost], bench.StatusChanges);
    }

    [Fact]
    public void Repeated_status_reads_of_an_unchanged_board_allocate_nothing()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var drone = bench.Register("drone");
        Held(drone.Claim(ghost, Seconds(30)));
        TestKitIds.Must(drone.Report(ghost, new ExecutorStatus.Working(1, 2)));

        Allocations.AssertNone(() =>
        {
            bench.Executors.StatusOf(ghost);
            bench.Executors.ClaimOn(ghost);
            bench.Executors.Lapse();
        }, "status reads");
    }

    // Blocked notices

    [Fact]
    public void A_stale_ghost_raises_one_notice_to_its_maker()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        bench.Flush();
        var why = Reason.Of(ReasonCodes.Different, TestKitIds.Catalogue);

        TestKitIds.Must(bench.T.Board.MarkStale(ghost, why));
        bench.Flush();
        bench.Flush();
        bench.Flush();

        var (maker, ghosts) = Assert.Single(bench.Notices);
        Assert.Equal(TestBoard.TestMaker, maker);
        Assert.Equal(new BlockedGhost(ghost, why), Assert.Single(ghosts));
    }

    [Fact]
    public void A_stale_ghost_marked_stale_again_for_the_same_reason_raises_no_second_notice()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var why = Reason.Of(ReasonCodes.Different, TestKitIds.Catalogue);
        TestKitIds.Must(bench.T.Board.MarkStale(ghost, why));
        bench.Flush();
        bench.Flush();

        bench.T.Board.MarkStale(ghost, why);
        TestBoard.Applied(bench.T.Apply(new Command.Change(TestBoard.Ids(ghost), new GhostEdit.SetLabel(Option.Some(TestKitIds.Must(Label.Create("x")))), Option<Revision>.None)));
        bench.Flush();
        bench.Flush();

        Assert.Single(bench.Notices);
    }

    [Fact]
    public void A_new_stale_reason_raises_a_new_notice()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        TestKitIds.Must(bench.T.Board.MarkStale(ghost, Reason.Of(ReasonCodes.Different, TestKitIds.Catalogue)));
        bench.Flush();
        bench.Flush();

        var later = Reason.Of(ReasonCodes.TargetGone, TestKitIds.Catalogue);
        TestKitIds.Must(bench.T.Board.MarkStale(ghost, later));
        bench.Flush();
        bench.Flush();

        Assert.Equal(2, bench.Notices.Count);
        Assert.Equal(later, Assert.Single(bench.Notices[1].Ghosts).Why);
    }

    [Fact]
    public void Ghosts_blocked_in_one_frame_reach_each_maker_as_one_notice()
    {
        var bench = new ExecutorsBench();
        var a = bench.Ghost();
        var b = bench.Ghost();
        var other = bench.Ghost(maker: OtherMaker);
        var why = Reason.Of(ReasonCodes.Different, TestKitIds.Catalogue);

        TestKitIds.Must(bench.T.Board.MarkStale(a, why));
        TestKitIds.Must(bench.T.Board.MarkStale(other, why));
        TestKitIds.Must(bench.T.Board.MarkStale(b, why));
        bench.Flush();
        bench.Flush();

        Assert.Equal(2, bench.Notices.Count);
        var mine = bench.Notices.Single(n => n.Maker == TestBoard.TestMaker);
        Assert.Equal([a, b], mine.Ghosts.Select(g => g.Ghost));
        Assert.Equal([other], bench.Notices.Single(n => n.Maker == OtherMaker).Ghosts.Select(g => g.Ghost));
    }

    [Fact]
    public void An_executor_waiting_tells_the_maker_once_per_reason()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var drone = bench.Register("drone");
        Held(drone.Claim(ghost, Seconds(30)));

        TestKitIds.Must(drone.Report(ghost, new ExecutorStatus.Waiting(NeedsSteel)));
        TestKitIds.Must(drone.Report(ghost, new ExecutorStatus.Waiting(NeedsSteel)));
        bench.Flush();
        TestKitIds.Must(drone.Report(ghost, new ExecutorStatus.Working(1, 2)));
        bench.Flush();

        var (maker, ghosts) = Assert.Single(bench.Notices);
        Assert.Equal(TestBoard.TestMaker, maker);
        Assert.Equal(new BlockedGhost(ghost, NeedsSteel), Assert.Single(ghosts));
    }

    [Fact]
    public void A_ghost_that_left_the_board_before_the_burst_is_not_told()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var drone = bench.Register("drone");
        Held(drone.Claim(ghost, Seconds(30)));
        TestKitIds.Must(drone.Report(ghost, new ExecutorStatus.Waiting(NeedsSteel)));

        TestBoard.Applied(bench.T.Apply(new Command.Remove(TestBoard.Ids(ghost))));
        bench.Flush();
        bench.Flush();

        Assert.Empty(bench.Notices);
    }

    [Fact]
    public void A_handler_that_throws_leaves_the_burst_and_the_rest_is_raised_by_the_next()
    {
        var bench = new ExecutorsBench();
        var mine = bench.Ghost();
        var other = bench.Ghost(maker: OtherMaker);
        var why = Reason.Of(ReasonCodes.Different, TestKitIds.Catalogue);
        var thrown = false;
        bench.T.Events.Blocked += (_, _) =>
        {
            if (thrown)
                return;
            thrown = true;
            throw new InvalidOperationException("consumer bug");
        };
        TestKitIds.Must(bench.T.Board.MarkStale(mine, why));
        TestKitIds.Must(bench.T.Board.MarkStale(other, why));
        bench.Flush();

        Assert.Throws<InvalidOperationException>(bench.Flush);
        bench.Flush();

        Assert.Equal([TestBoard.TestMaker, OtherMaker], bench.Notices.Select(n => n.Maker));
    }

    [Fact]
    public void At_world_end_every_executor_is_disposed()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var drone = bench.Register("drone");
        Held(drone.Claim(ghost, Seconds(30)));

        bench.Executors.Dispose();

        Assert.True(bench.Executors.ClaimOn(ghost).IsNone);
        Assert.True(drone.Claim(ghost, Seconds(30)).IsError);
    }

    private static Claim Held(Result<Claim, ClaimRefused> result) =>
        result.TryGet(out var claim) ? claim : throw new InvalidOperationException(result.ToString());

    private static Reason Refusal<T>(Result<T, Reason> result) where T : notnull =>
        result.TryGetError(out var why) ? why : throw new InvalidOperationException("not refused");
}
