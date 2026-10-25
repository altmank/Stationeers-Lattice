using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Lattice.Api;
using Lattice.Core.Validation;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Validation;

/// <summary>Mods' validation rules: Lattice's time limit and switch-off, worker and main-thread rules.</summary>
/// <summary>Worker rules; the deadline test is timed on the real clock and runs with the timing tests.</summary>
[Collection(nameof(Timing))]
public sealed class RuleTests
{
    [Fact]
    public void A_throwing_rule_is_switched_off_and_reported_once_and_the_others_still_run()
    {
        var bench = new ValidationBench();
        var heard = new List<GhostId>();
        bench.Rules.Register(new RuleDescriptor(Rule("throws"), _ => throw new InvalidOperationException("bug")));
        bench.Rules.Register(new RuleDescriptor(Rule("counts"), input =>
        {
            heard.Add(input.Ghost.Id);
            return new RuleVerdict.Warn(Why("counted"));
        }));
        bench.Start();
        var a = bench.Ghost("StructureFrameIron", Large(0, 0, 0));
        var b = bench.Ghost("StructureFrameIron", Large(4, 0, 0));

        bench.Pump();
        var report = bench.Report();

        var switchedOff = report.Findings.OfType<Finding.Warns>().Where(w => w.Why.Code == ReasonCodes.RuleSwitchedOff).ToList();
        Assert.Single(switchedOff);
        Assert.Equal(Rule("throws"), switchedOff[0].Why.From);
        Assert.Contains(a, heard);
        Assert.Contains(b, heard);
        Assert.Contains(Why("counted"), Warnings(report, a));
        Assert.Contains(Why("counted"), Warnings(report, b));
    }

    [Fact]
    public void A_rule_past_the_limit_times_out_its_verdict_is_dropped_and_validation_completes()
    {
        var bench = new ValidationBench();
        bench.Rules.Register(new RuleDescriptor(Rule("slow"), _ =>
        {
            bench.Clock.Advance(TimeSpan.FromMilliseconds(5));
            return new RuleVerdict.Fail(Why("slow"));
        }));
        bench.Rules.Register(new RuleDescriptor(Rule("fails"), _ => new RuleVerdict.Fail(Why("fails"))));
        bench.Start();
        var ghost = bench.Ghost("StructureFrameIron", Large(0, 0, 0));

        bench.Pump();
        var report = bench.Report();

        Assert.Contains(Reason.Of(ReasonCodes.RuleTimedOut, Rule("slow")), Warnings(report, ghost));
        Assert.DoesNotContain(report.Findings, f => f is Finding.WillFail fail && fail.Why == Why("slow"));
        Assert.Contains(report.Findings, f => f is Finding.WillFail fail && fail.Why == Why("fails"));
    }

    [Fact]
    public void A_main_thread_rule_runs_in_the_main_slices_and_sees_earlier_ghosts_assumed_built()
    {
        var bench = new ValidationBench();
        var seen = new List<(GhostId Ghost, bool OnWorker, bool FloorStands)>();
        GhostId floor = default;
        bench.Rules.Register(new RuleDescriptor(Rule("main"), input =>
        {
            var below = input.World.At(Large(0, 0, 0));
            seen.Add((input.Ghost.Id, bench.Worker.Running, below.Any(s => input.World.AssumedFrom(s.Ref) == Option.Some(floor))));
            return new RuleVerdict.Pass();
        })
        {
            Thread = CallbackThread.Main,
        });
        bench.Start();
        floor = bench.Ghost("ModStackFrame", Large(0, 0, 0));
        var upper = bench.Ghost("ModStackFrame", Large(0, 1, 0));

        bench.Pump();

        Assert.NotEmpty(seen);
        Assert.DoesNotContain(seen, s => s.OnWorker);
        Assert.Contains(seen, s => s.Ghost == upper && s.FloorStands);
        Assert.Contains(seen, s => s.Ghost == floor && !s.FloorStands);
        AssertWaitsFor(Verdict(bench.Report(), upper), floor);
    }

    [Fact]
    public void A_main_thread_rule_is_not_asked_about_a_ghost_that_left_the_board_before_its_turn()
    {
        var bench = new ValidationBench();
        var asked = new List<GhostId>();
        bench.Rules.Register(new RuleDescriptor(Rule("main"), input =>
        {
            asked.Add(input.Ghost.Id);
            return new RuleVerdict.Pass();
        })
        {
            Thread = CallbackThread.Main,
        });
        bench.Start();
        var stays = bench.Ghost("StructureFrameIron", Large(0, 0, 0));
        var leaves = bench.Ghost("StructureFrameIron", Large(6, 0, 0));

        // The run asks the rule about both ghosts; one leaves before the checking slices answer.
        bench.Events.Flush();
        bench.Service.Frame();
        bench.Worker.RunAll();
        bench.Service.Frame();
        bench.Apply(new Command.Remove(new Selector.Ids(ValueList.Of(leaves))));
        bench.Pump();

        Assert.Contains(stays, asked);
        Assert.DoesNotContain(leaves, asked);
        Assert.IsType<Finding.Passes>(Verdict(bench.Report(), stays));
    }

    /// <summary>The answering rule must answer within 20 ms of wall time on its rule thread.</summary>
    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void A_worker_rule_that_never_returns_times_out_and_validation_completes_without_it()
    {
        using var calls = new RuleThreads(TimeSpan.FromMilliseconds(20));
        using var release = new ManualResetEventSlim(false);
        var bench = new ValidationBench(workerCalls: calls);
        var hangs = 0;
        bench.Rules.Register(new RuleDescriptor(Rule("hangs"), _ =>
        {
            Interlocked.Increment(ref hangs);
            release.Wait();
            return new RuleVerdict.Fail(Why("hangs"));
        }));
        bench.Rules.Register(new RuleDescriptor(Rule("answers"), _ => new RuleVerdict.Warn(Why("answers"))));
        bench.Start();
        var first = bench.Ghost("StructureFrameIron", Large(0, 0, 0));
        var second = bench.Ghost("StructureFrameIron", Large(4, 0, 0));

        bench.Pump();
        var report = bench.Report();
        release.Set();

        Assert.Equal(1, Volatile.Read(ref hangs));
        Assert.Contains(Reason.Of(ReasonCodes.RuleTimedOut, Rule("hangs")), Warnings(report, first));
        Assert.Contains(Reason.Of(ReasonCodes.RuleTimedOut, Rule("hangs")), Warnings(report, second));
        Assert.Contains(Why("answers"), Warnings(report, first));
        Assert.Contains(Why("answers"), Warnings(report, second));
        Assert.Equal(2, calls.ThreadsStarted);
    }

    [Fact]
    public void A_rule_registered_after_start_rechecks_every_ghost()
    {
        var bench = new ValidationBench().Start();
        var ghost = bench.Ghost("StructureFrameIron", Large(0, 0, 0));
        bench.Pump();
        Assert.IsType<Finding.Passes>(Verdict(bench.Report(), ghost));

        bench.Rules.Register(new RuleDescriptor(Rule("late"), _ => new RuleVerdict.Fail(Why("late"))));
        bench.Pump();

        Assert.Contains(bench.Report().Findings, f => f is Finding.WillFail fail && fail.Why == Why("late"));
    }
}
