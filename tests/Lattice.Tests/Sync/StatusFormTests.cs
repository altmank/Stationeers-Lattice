using System;
using System.Diagnostics;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Executors;
using Lattice.Core.Sync;
using Lattice.Tests.Executors;
using Xunit.Abstractions;

namespace Lattice.Tests.Sync;

/// <summary>The claims-status wire form; its wall-clock cost test runs with the timing tests.</summary>
[Collection(nameof(Timing))]
public sealed class StatusFormTests(ITestOutputHelper output)
{
    private static readonly ExecutorInfo Builder = ExecutorsBench.Info("builder");

    private static Reason Needs(string item) =>
        Reason.Of(ReasonCodes.NotAllowed, WorldExecutors.Rule, ValueMap.Of("needs", item).With("count", "4"));

    private static readonly ClaimState[] Kinds =
    {
        new ClaimState.Held(new GhostId(7), Builder, 29.5, Option<ExecutorStatus>.None),
        new ClaimState.Held(new GhostId(7), Builder, 12, Option.Some<ExecutorStatus>(new ExecutorStatus.Working(2, 5))),
        new ClaimState.Held(new GhostId(7), Builder, 0.25, Option.Some<ExecutorStatus>(new ExecutorStatus.Paused())),
        new ClaimState.Held(new GhostId(7), Builder, 3, Option.Some<ExecutorStatus>(new ExecutorStatus.Waiting(Needs("steel sheets")))),
        new ClaimState.Gone(new GhostId(7)),
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_entry_reads_back_as_written(int kind)
    {
        var entry = Kinds[kind];
        foreach (var join in new[] { false, true })
        {
            var read = StatusForm.Read(StatusForm.Write(join, new List<ClaimState> { entry, new ClaimState.Gone(new GhostId(9)) }));

            Assert.True(read.TryGet(out var update), read.ToString());
            Assert.Equal(join, update.Join);
            Assert.Equal(new ClaimState[] { entry, new ClaimState.Gone(new GhostId(9)) }, update.Entries);
        }
    }

    [Fact]
    public void A_name_with_spaces_and_marks_reads_back()
    {
        var named = new ExecutorInfo(Builder.Id, Lattice.TestKit.TestKitIds.Must(DisplayName.Create("Drone #3 (north), 100%")));
        var entry = new ClaimState.Held(new GhostId(1), named, 5, Option<ExecutorStatus>.None);

        Assert.True(StatusForm.Read(StatusForm.Write(false, new List<ClaimState> { entry })).TryGet(out var update));
        Assert.Equal(entry, Assert.Single(update.Entries));
    }

    [Fact]
    public void The_lease_left_is_written_to_the_hundredth_of_a_second()
    {
        var entry = new ClaimState.Held(new GhostId(1), Builder, 4.98333333, Option<ExecutorStatus>.None);

        Assert.True(StatusForm.Read(StatusForm.Write(false, new List<ClaimState> { entry })).TryGet(out var update));
        Assert.Equal(4.98, ((ClaimState.Held)update.Entries[0]).LeftSeconds, 6);
    }

    [Fact]
    public void A_renewal_entry_is_64_bytes_or_less()
    {
        var entry = new ClaimState.Held(new GhostId(1_234_567), Builder, 29.98, Option<ExecutorStatus>.None);

        var line = Line(StatusForm.Write(false, new List<ClaimState> { entry }));

        output.WriteLine($"{SyncLimits.Bytes(line)} bytes: {line}");
        Assert.True(SyncLimits.Bytes(line) <= 64, line);
    }

    [Fact]
    public void A_working_entry_stays_under_the_entry_size_the_form_promises()
    {
        var entry = new ClaimState.Held(new GhostId(1_234_567), Builder, 29.98, Option.Some<ExecutorStatus>(new ExecutorStatus.Working(12, 20)));

        var line = Line(StatusForm.Write(false, new List<ClaimState> { entry }));

        output.WriteLine($"{SyncLimits.Bytes(line)} bytes: {line}");
        Assert.True(SyncLimits.Bytes(line) <= 90, line);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lattice-answer 1 join=0\n")]
    [InlineData("lattice-status 1\n")]
    [InlineData("lattice-status 1 join=2\n")]
    [InlineData("lattice-status 1 join=0 extra=1\n")]
    [InlineData("lattice-status 1 join=0\nclaim ghost=1 by=testkit/builder name=builder\n")]
    [InlineData("lattice-status 1 join=0\nclaim ghost=1 by=Not%20An%20Id name=builder left=3\n")]
    [InlineData("lattice-status 1 join=0\nclaim ghost=1 by=testkit/builder name=builder left=-1\n")]
    [InlineData("lattice-status 1 join=0\nclaim ghost=1 by=testkit/builder name=builder left=3 now=working step=1\n")]
    [InlineData("lattice-status 1 join=0\nclaim ghost=1 by=testkit/builder name=builder left=3 now=sleeping\n")]
    [InlineData("lattice-status 1 join=0\nclaim ghost=1 by=testkit/builder name=builder left=3 now=waiting\n")]
    [InlineData("lattice-status 1 join=0\nclaim ghost=1 by=testkit/builder name=builder left=3 now=paused step=1\n")]
    [InlineData("lattice-status 1 join=0\ngone ghost=1 left=3\n")]
    [InlineData("lattice-status 1 join=0\ngone\n")]
    public void A_text_that_is_not_exactly_the_form_does_not_read(string text)
    {
        Assert.False(StatusForm.Read(text).IsOk);
    }

    [Fact]
    public void Writing_a_message_allocates_little_beyond_its_text()
    {
        var entries = new List<ClaimState>(500);
        for (var i = 0; i < 500; i++)
            entries.Add(new ClaimState.Held(new GhostId(1000 + i), Builder, 29.5, Option.Some<ExecutorStatus>(new ExecutorStatus.Working(1, 3))));
        StatusForm.Write(false, entries);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var text = StatusForm.Write(false, entries);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"{allocated} bytes allocated for a {text.Length}-character text");
        Assert.True(allocated < 3L * text.Length * sizeof(char), $"{allocated} bytes for {text.Length} characters");
    }

    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void A_thousand_claims_renewing_every_five_seconds_with_four_clients_cost_little()
    {
        var bench = new ExecutorSyncBench();
        var clients = new List<ExecutorSyncBench.Seat>();
        for (var i = 1; i <= 4; i++)
            clients.Add(bench.Join(i));
        var builder = bench.Register("builder");
        var claims = new List<Claim>(1000);
        var placed = TestBoard(bench, 1000);
        foreach (var ghost in placed)
        {
            claims.Add(ExecutorSyncBench.Claim(builder, ghost, seconds: 10));
            ExecutorSyncBench.Report(builder, ghost, new ExecutorStatus.Working(1, 3));
        }

        bench.Settle(1000);
        bench.Wire.SentByHost.Clear();

        const int frames = 250;
        const double frameSeconds = 5.0 / frames;
        var times = new List<double>(frames);
        var watch = new Stopwatch();
        Perf.Collect();
        for (var frame = 0; frame < frames; frame++)
        {
            watch.Restart();
            bench.HostClock.Advance(frameSeconds);
            for (var i = frame * 4; i < frame * 4 + 4; i++)
                claims[i] = builder.Renew(claims[i]).Match(static c => c, static why => throw new Xunit.Sdk.XunitException(why.Why.ToString()));
            bench.HostFrame();
            times.Add(watch.Elapsed.TotalMilliseconds);
            bench.Deliver();
        }

        long bytes = 0;
        foreach (var (to, _, text) in bench.Wire.SentByHost)
            if (to == clients[0].Connection)
                bytes += SyncLimits.Bytes(text);
        times.Sort();
        var perSecond = bytes / 5.0;
        output.WriteLine($"{perSecond:F0} bytes a second a client; host frame median {times[frames / 2]:F3} ms, p99 {times[frames * 99 / 100]:F3} ms");
        Assert.True(perSecond < 30_000, $"{perSecond} bytes a second");
        Assert.True(times[frames * 99 / 100] < 2.0, $"p99 {times[frames * 99 / 100]} ms");
        foreach (var client in clients)
            Assert.Empty(client.Reports);
    }

    private static List<GhostId> TestBoard(ExecutorSyncBench bench, int count)
    {
        var ghosts = new List<GhostId>(count);
        for (var i = 0; i < count; i++)
            ghosts.Add(bench.Ghost());
        return ghosts;
    }

    private static string Line(string text) => text.Split('\n')[1];
}
