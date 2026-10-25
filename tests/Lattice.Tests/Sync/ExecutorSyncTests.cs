using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Executors;
using Lattice.Core.Sync;
using Lattice.Tests.Board;
using Lattice.Tests.Executors;
using Lattice.TestKit;

namespace Lattice.Tests.Sync;

public sealed class ExecutorSyncTests
{
    private static readonly ExecutorStatus Working25 = new ExecutorStatus.Working(2, 5);

    private static Reason Needs(string item) =>
        Reason.Of(ReasonCodes.NotAllowed, WorldExecutors.Rule, ValueMap.Of("needs", item));

    [Fact]
    public void A_joined_client_reads_the_hosts_claim_and_status_with_the_lease_on_its_own_clock()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1, clock: 5000);
        var ghost = bench.Ghost();
        var builder = bench.Register("builder");
        var claim = ExecutorSyncBench.Claim(builder, ghost, seconds: 30);
        ExecutorSyncBench.Report(builder, ghost, Working25);

        bench.Settle();

        Assert.True(client.Executors.ClaimOn(ghost).TryGet(out var seen));
        Assert.Equal(claim.Ghost, seen.Ghost);
        Assert.Equal(claim.By, seen.By);
        Assert.Equal(5030, seen.Until.Seconds, 3);
        Assert.Equal(bench.Executors.StatusOf(ghost), client.Executors.StatusOf(ghost));
        Assert.Empty(client.Reports);
        Assert.Empty(bench.HostReports);
    }

    [Fact]
    public void A_report_raises_one_status_change_on_the_client_and_an_equal_report_none()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var ghost = bench.Ghost();
        var builder = bench.Register("builder");
        ExecutorSyncBench.Claim(builder, ghost);
        bench.Settle();
        Assert.Empty(client.StatusChanges);

        ExecutorSyncBench.Report(builder, ghost, Working25);
        bench.Settle();
        Assert.Equal(new[] { ghost }, client.StatusChanges);

        ExecutorSyncBench.Report(builder, ghost, new ExecutorStatus.Working(2, 5));
        bench.Settle();
        Assert.Single(client.StatusChanges);
    }

    [Fact]
    public void A_renewal_moves_the_clients_until_and_raises_nothing()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1, clock: 5000);
        var ghost = bench.Ghost();
        var builder = bench.Register("builder");
        var claim = ExecutorSyncBench.Claim(builder, ghost, seconds: 30);
        ExecutorSyncBench.Report(builder, ghost, Working25);
        bench.Settle();
        var changes = client.StatusChanges.Count;

        bench.HostClock.Advance(20);
        client.Clock.Advance(20);
        TestKitIds.Must(builder.Renew(claim).Match(static c => Result<Claim, Reason>.Ok(c), static r => Result<Claim, Reason>.Error(r.Why)));
        bench.Settle();

        Assert.Equal(5050, Until(client, ghost), 3);
        Assert.Equal(changes, client.StatusChanges.Count);
    }

    public static IEnumerable<object[]> Endings() =>
        new[] { "release", "lapse", "dispose", "remove" }.Select(static e => new object[] { e });

    [Theory]
    [MemberData(nameof(Endings))]
    public void A_claim_that_ends_on_the_host_is_gone_on_the_client_with_a_status_change_when_it_had_a_status(string ending)
    {
        foreach (var hadStatus in new[] { true, false })
        {
            var bench = new ExecutorSyncBench();
            var client = bench.Join(1);
            var ghost = bench.Ghost();
            var builder = bench.Register("builder");
            var claim = ExecutorSyncBench.Claim(builder, ghost, seconds: 30);
            if (hadStatus)
                ExecutorSyncBench.Report(builder, ghost, Working25);
            bench.Settle();
            var changes = client.StatusChanges.Count;

            switch (ending)
            {
                case "release":
                    builder.Release(claim);
                    break;
                case "lapse":
                    bench.HostClock.Advance(31);
                    break;
                case "dispose":
                    builder.Dispose();
                    break;
                case "remove":
                    bench.Remove(ghost);
                    break;
            }

            bench.Settle();

            Assert.False(client.Executors.ClaimOn(ghost).IsSome, ending);
            Assert.Empty(client.Executors.StatusOf(ghost));
            var expected = hadStatus && ending != "remove" ? 1 : 0;
            Assert.Equal(changes + expected, client.StatusChanges.Count);
            Assert.Empty(client.Reports);
        }
    }

    [Fact]
    public void Waiting_tells_the_ghosts_maker_on_the_client_once_and_again_only_for_a_new_reason()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var ghost = bench.Ghost(ExecutorsBench.OtherMaker);
        var builder = bench.Register("builder");
        ExecutorSyncBench.Claim(builder, ghost);
        bench.Settle();

        ExecutorSyncBench.Report(builder, ghost, new ExecutorStatus.Waiting(Needs("steel")));
        bench.Settle();
        var blocked = Assert.Single(client.Blocked);
        Assert.Equal(ExecutorsBench.OtherMaker, blocked.Maker);
        Assert.Equal(new BlockedGhost(ghost, Needs("steel")), Assert.Single(blocked.Ghosts));

        ExecutorSyncBench.Report(builder, ghost, new ExecutorStatus.Waiting(Needs("steel")));
        bench.Settle();
        Assert.Single(client.Blocked);

        ExecutorSyncBench.Report(builder, ghost, new ExecutorStatus.Waiting(Needs("glass")));
        bench.Settle();
        Assert.Equal(2, client.Blocked.Count);
        Assert.Equal(Needs("glass"), client.Blocked[1].Ghosts[0].Why);
    }

    [Fact]
    public void A_ghost_placed_claimed_and_reported_in_one_host_frame_is_changed_on_the_client_before_its_status()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        bench.Settle();
        client.Log.Clear();

        var ghost = bench.Ghost();
        var builder = bench.Register("builder");
        ExecutorSyncBench.Claim(builder, ghost);
        ExecutorSyncBench.Report(builder, ghost, Working25);
        bench.Settle();

        Assert.Equal(new[] { "changed " + bench.Host.T.Board.Revision.Value, "status " + ghost.Value }, client.Log);
    }

    [Fact]
    public void A_status_for_a_ghost_removed_in_the_same_frame_raises_nothing_and_reports_nothing_on_the_client()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var ghost = bench.Ghost();
        var builder = bench.Register("builder");
        ExecutorSyncBench.Claim(builder, ghost);
        ExecutorSyncBench.Report(builder, ghost, Working25);
        bench.Settle();
        var changes = client.StatusChanges.Count;

        ExecutorSyncBench.Report(builder, ghost, new ExecutorStatus.Paused());
        bench.Remove(ghost);
        bench.Settle();

        Assert.Equal(changes, client.StatusChanges.Count);
        Assert.False(client.Executors.ClaimOn(ghost).IsSome);
        Assert.Empty(client.Reports);
    }

    [Fact]
    public void Ten_reports_in_a_frame_go_as_one_entry()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var ghost = bench.Ghost();
        var builder = bench.Register("builder");
        ExecutorSyncBench.Claim(builder, ghost);
        bench.Settle();
        bench.Wire.SentByHost.Clear();
        client.StatusChanges.Clear();

        for (var step = 1; step <= 10; step++)
            ExecutorSyncBench.Report(builder, ghost, new ExecutorStatus.Working(step, 10));
        bench.Settle();

        var status = Assert.Single(bench.Wire.SentByHost, static sent => sent.Kind == SyncKind.Status);
        Assert.Single(Lines(status.Text), static line => line.StartsWith("claim ", StringComparison.Ordinal));
        Assert.Equal(new[] { ghost }, client.StatusChanges);
        Assert.Equal(new ExecutorStatus.Working(10, 10), client.Executors.StatusOf(ghost)[0].Status);
    }

    [Fact]
    public void Status_on_both_sides_of_a_change_set_goes_as_two_messages_in_order()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var first = bench.Ghost();
        var builder = bench.Register("builder");
        ExecutorSyncBench.Claim(builder, first);
        bench.Settle();
        bench.Wire.SentByHost.Clear();

        ExecutorSyncBench.Report(builder, first, Working25);
        bench.Host.T.Events.Flush();
        bench.Sync.PlaceStatus();
        var second = bench.Ghost();
        ExecutorSyncBench.Claim(builder, second);
        ExecutorSyncBench.Report(builder, second, Working25);
        bench.Host.T.Events.Flush();
        bench.Sync.PlaceStatus();
        bench.Settle();

        Assert.Equal(new[] { SyncKind.Status, SyncKind.Lines, SyncKind.Status }, bench.SentTo(client.Connection));
        Assert.Equal(bench.Executors.StatusOf(second), client.Executors.StatusOf(second));
        Assert.Empty(client.Reports);
    }

    [Fact]
    public void A_client_never_lapses_a_claim_by_its_own_clock_and_drops_it_when_the_host_says()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1, clock: 5000);
        var ghost = bench.Ghost();
        var builder = bench.Register("builder");
        ExecutorSyncBench.Claim(builder, ghost, seconds: 30);
        bench.Settle();

        client.Clock.Advance(1000);
        bench.Settle();
        Assert.True(client.Executors.ClaimOn(ghost).IsSome);

        bench.HostClock.Advance(31);
        bench.Settle();
        Assert.False(client.Executors.ClaimOn(ghost).IsSome);
    }

    [Fact]
    public void A_paused_host_holds_the_claim_on_the_client_as_last_told()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1, clock: 5000);
        var ghost = bench.Ghost();
        var builder = bench.Register("builder");
        ExecutorSyncBench.Claim(builder, ghost, seconds: 30);
        bench.Settle();
        bench.Wire.SentByHost.Clear();

        client.Clock.Advance(60);
        bench.Frames(20);

        Assert.DoesNotContain(bench.Wire.SentByHost, static sent => sent.Kind == SyncKind.Status);
        Assert.Equal(5030, Until(client, ghost), 3);
    }

    [Fact]
    public void A_client_joining_with_live_claims_gets_them_after_the_ghost_parts_and_after_the_board_reset()
    {
        var bench = new ExecutorSyncBench(SyncLimits.Default with { JoinGhostsPerPart = 1 });
        var builder = bench.Register("builder");
        var ghosts = new[] { bench.Ghost(), bench.Ghost(), bench.Ghost() };
        foreach (var ghost in ghosts)
        {
            ExecutorSyncBench.Claim(builder, ghost);
            ExecutorSyncBench.Report(builder, ghost, Working25);
        }

        bench.Settle();
        var client = bench.Join(1);
        bench.Settle();

        Assert.Equal(new[] { SyncKind.Join, SyncKind.Join, SyncKind.Join, SyncKind.Status }, bench.SentTo(client.Connection));
        foreach (var ghost in ghosts)
            Assert.Equal(bench.Executors.StatusOf(ghost), client.Executors.StatusOf(ghost));
        Assert.Equal("reset", client.Log[0]);
        Assert.Equal(3, client.StatusChanges.Count);
    }

    [Fact]
    public void A_claim_made_while_the_join_parts_go_arrives_after_them()
    {
        var bench = new ExecutorSyncBench(SyncLimits.Default with { JoinGhostsPerPart = 1 });
        var ghosts = new[] { bench.Ghost(), bench.Ghost(), bench.Ghost() };
        bench.Settle();
        var client = bench.Join(1);
        bench.Frame();
        var builder = bench.Register("builder");
        ExecutorSyncBench.Claim(builder, ghosts[2]);
        ExecutorSyncBench.Report(builder, ghosts[2], Working25);
        bench.Settle();

        var sent = bench.SentTo(client.Connection);
        Assert.True(sent.LastIndexOf(SyncKind.Join) < sent.IndexOf(SyncKind.Status));
        Assert.Equal(bench.Executors.StatusOf(ghosts[2]), client.Executors.StatusOf(ghosts[2]));
        Assert.Empty(client.Reports);
    }

    [Fact]
    public void A_rejoin_clears_the_mirror_and_the_join_rebuilds_it()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var kept = bench.Ghost();
        var released = bench.Ghost();
        var builder = bench.Register("builder");
        ExecutorSyncBench.Claim(builder, kept);
        ExecutorSyncBench.Report(builder, kept, Working25);
        var claim = ExecutorSyncBench.Claim(builder, released);
        bench.Settle();

        bench.Sync.Left(client.Connection);
        builder.Release(claim);
        bench.Settle();
        Assert.True(client.Executors.ClaimOn(released).IsSome);

        bench.Sync.Joined(client.Connection);
        bench.Settle();

        Assert.False(client.Executors.ClaimOn(released).IsSome);
        Assert.Equal(bench.Executors.StatusOf(kept), client.Executors.StatusOf(kept));
        Assert.True(client.Executors.ClaimOn(kept).IsSome);
    }

    [Fact]
    public void A_status_style_resolves_the_same_look_on_the_host_and_the_client()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var ghost = bench.Ghost();
        var builder = bench.Register("builder");
        ExecutorSyncBench.Claim(builder, ghost);
        bench.Settle();
        var host = new StyledSeat(bench.Host.T, bench.Executors);
        var seen = new StyledSeat(client.T, client.Executors);
        var before = seen.Look(ghost);

        ExecutorSyncBench.Report(builder, ghost, Working25);
        bench.Settle();

        Assert.Equal(host.Look(ghost), seen.Look(ghost));
        Assert.NotEqual(before, seen.Look(ghost));
    }

    [Fact]
    public void Register_on_a_client_is_still_refused_host_only()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);

        var refused = client.Executors.Register(ExecutorsBench.Info("builder"));

        Assert.True(refused.TryGetError(out var why));
        Assert.Equal(ReasonCodes.HostOnly, why.Code);
    }

    [Theory]
    [InlineData("lattice-status 1 join=0\nclaim ghost=1 by=testkit/builder name=builder left=30 colour=red\n")]
    [InlineData("lattice-status 1 join=0\nclaim ghost=x by=testkit/builder name=builder left=30\n")]
    [InlineData("lattice-status 1 join=0\nclaim ghost=1 by=testkit/builder name=builder left=30 now=working step=two of=5\n")]
    [InlineData("lattice-status 1 join=0\nheld ghost=1\n")]
    public void A_status_message_that_does_not_read_is_dropped_with_a_report_and_the_mirror_unchanged(string bad)
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var ghost = bench.Ghost();
        var builder = bench.Register("builder");
        ExecutorSyncBench.Claim(builder, ghost);
        ExecutorSyncBench.Report(builder, ghost, Working25);
        bench.Settle();
        var before = client.Executors.StatusOf(ghost);

        client.Sync.Receive(SyncKind.Status, "0 1\n" + bad.Replace("ghost=1", "ghost=" + ghost.Value));
        client.T.Events.Flush();

        Assert.Single(client.Reports);
        Assert.Equal(before, client.Executors.StatusOf(ghost));
    }

    [Fact]
    public void A_status_frame_out_of_order_drops_the_message_with_a_report()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        bench.Settle();

        client.Sync.Receive(SyncKind.Status, "1 2\ngone ghost=1\n");

        Assert.Single(client.Reports);
    }

    [Fact]
    public void Without_a_joined_client_or_without_executors_no_status_is_queued_or_sent()
    {
        var alone = new ExecutorSyncBench();
        var ghost = alone.Ghost();
        var builder = alone.Register("builder");
        ExecutorSyncBench.Claim(builder, ghost);
        ExecutorSyncBench.Report(builder, ghost, Working25);
        alone.HostFrame();
        Assert.Equal(0, alone.Sync.StatusWaiting);
        Assert.Equal(0, alone.Sync.Queued);

        var idle = new ExecutorSyncBench();
        var client = idle.Join(1);
        idle.Ghost();
        idle.Settle();
        Assert.DoesNotContain(idle.Wire.SentByHost, static sent => sent.Kind == SyncKind.Status);
        Assert.Empty(client.Reports);
    }

    [Fact]
    public void A_host_with_executors_but_no_sync_reads_its_own_claims()
    {
        var bench = new ExecutorsBench();
        var ghost = bench.Ghost();
        var builder = bench.Register("builder");
        builder.Claim(ghost, ExecutorsBench.Seconds(30));
        TestKitIds.Must(builder.Report(ghost, Working25));

        Assert.Null(bench.Executors.Mirror);
        Assert.Equal(Working25, bench.Executors.StatusOf(ghost)[0].Status);
    }

    private static double Until(ExecutorSyncBench.Seat client, GhostId ghost) =>
        client.Executors.ClaimOn(ghost).TryGet(out var claim) ? claim.Until.Seconds : throw new Xunit.Sdk.XunitException("no claim");

    private static string[] Lines(string frame) => frame.Substring(frame.IndexOf('\n') + 1).Split('\n');
}
