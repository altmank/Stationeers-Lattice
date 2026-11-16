using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Sync;
using Lattice.TestKit.Rules;

namespace Lattice.Tests.Sync;

public sealed class ReportGateTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(300);

    private readonly ManualClock _clock = new();
    private readonly List<Reason> _passed = new();

    private ReportGate Gate(int maxKeys = ReportGate.DefaultMaxKeys) => new(_passed.Add, _clock, Window, maxKeys);

    private static Reason From(long connection, ReasonCode? code = null) =>
        (SyncRules.Problem("bad message") with { Code = code ?? ReasonCodes.InvalidValue }).WithConnection(new ConnectionId(connection));

    private static ReasonCode Code(int n) =>
        ModId.Create("gate").TryGet(out var mod) && ReasonCode.Create(mod, "code-" + new string((char)('a' + n % 26), 1 + n / 26)).TryGet(out var code)
            ? code
            : throw new InvalidOperationException("test code");

    [Fact]
    public void The_first_report_passes_unchanged()
    {
        var gate = Gate();
        var report = From(3);

        gate.Report(report);

        Assert.Equal(report, Assert.Single(_passed));
    }

    [Fact]
    public void A_flood_inside_the_window_passes_once()
    {
        var gate = Gate();

        for (var i = 0; i < 1000; i++)
            gate.Report(From(3));

        Assert.Single(_passed);
    }

    [Fact]
    public void The_first_report_after_the_window_passes_with_the_count_and_the_window_restarts()
    {
        var gate = Gate();
        for (var i = 0; i < 1000; i++)
            gate.Report(From(3));

        _clock.Advance(TimeSpan.FromSeconds(301));
        gate.Report(From(3));
        gate.Report(From(3));

        Assert.Equal(2, _passed.Count);
        Assert.Equal("999", _passed[1].Facts["more"]);
        Assert.Equal(From(3).Facts.Count + 1, _passed[1].Facts.Count);
    }

    [Fact]
    public void A_report_just_inside_the_window_is_still_counted()
    {
        var gate = Gate();
        gate.Report(From(3));

        _clock.Advance(Window - TimeSpan.FromSeconds(1));
        gate.Report(From(3));

        Assert.Single(_passed);
    }

    [Fact]
    public void Each_connection_is_reported_once()
    {
        var gate = Gate();

        gate.Report(From(3));
        gate.Report(From(4));
        gate.Report(From(3));
        gate.Report(From(4));

        Assert.Equal(["3", "4"], [.. _passed.ConvertAll(r => r.Facts["connection"])]);
    }

    [Fact]
    public void Each_code_from_one_connection_is_reported_once()
    {
        var gate = Gate();

        gate.Report(From(3, ReasonCodes.InvalidValue));
        gate.Report(From(3, ReasonCodes.TooLarge));
        gate.Report(From(3, ReasonCodes.InvalidValue));
        gate.Report(From(3, ReasonCodes.TooLarge));

        Assert.Equal([ReasonCodes.InvalidValue, ReasonCodes.TooLarge], [.. _passed.ConvertAll(r => r.Code)]);
    }

    [Fact]
    public void A_report_with_no_connection_is_one_key()
    {
        var gate = Gate();
        var report = SyncRules.Problem("not a host message");

        gate.Report(report);
        gate.Report(report);

        Assert.Equal(report, Assert.Single(_passed));
    }

    [Fact]
    public void Distinct_keys_beyond_the_cap_share_an_overflow_key()
    {
        var gate = Gate(maxKeys: 64);

        for (var n = 0; n < 200; n++)
            gate.Report(From(3, Code(n)));

        Assert.Equal(64, gate.Keys);
        Assert.Equal(65, _passed.Count);

        _clock.Advance(TimeSpan.FromSeconds(301));
        gate.Report(From(3, Code(199)));

        Assert.Equal("135", _passed[^1].Facts["more"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_gate_without_a_positive_window_is_refused(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportGate(_passed.Add, _clock, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void A_gate_that_tracks_no_key_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Gate(maxKeys: 0));

    [Fact]
    public void A_repeat_inside_the_window_allocates_nothing()
    {
        var gate = Gate();
        var report = From(3);
        gate.Report(report);
        for (var i = 0; i < 100; i++)
            gate.Report(report);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            gate.Report(report);

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void The_host_reports_a_peer_that_keeps_sending_bad_messages_once()
    {
        var bench = new SyncBench();
        bench.Join(connection: 1, player: 7);
        bench.Frame();

        for (var i = 0; i < 5; i++)
            bench.HostSync.Receive(new ConnectionId(1), new ClientId(7), "not a command");
        bench.Frames(2);

        Assert.Single(bench.HostReports);
    }

    [Fact]
    public void The_client_reports_a_host_that_keeps_sending_bad_messages_once()
    {
        var bench = new SyncBench();
        var (client, _) = bench.Join(connection: 1, player: 7);
        bench.Frame();

        for (var i = 0; i < 5; i++)
            client.Receive((SyncKind)99, "x");

        Assert.Single(bench.ClientReports);
    }
}
