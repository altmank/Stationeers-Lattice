using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.Scenarios;

namespace Lattice.Tests.Contracts;

/// <summary>
/// The extension-point suite for validation rules from other mods: a registered rule is heard;
/// rules run in registration order; an id is registered once; disposing a registration during a check is safe and
/// takes effect from the next check; a rule registered from inside a check runs from the next check, never nested;
/// registrations end with the world; a rule that throws is switched off for the world and reported once; a rule slower
/// than the limit gives a timed-out finding and its verdict is dropped, while the other rules still answer.
/// Derive it and give a fresh host per test (xunit makes a new instance per test).
/// </summary>
public abstract class RuleHostContract
{
    private static readonly ModId Mod = TestKitIds.Must(ModId.Create("suite"));
    private static readonly Ghost Ghost = Scenario.With(FakeCatalogue.Hand())
        .Ghost("StructureFrameIron", new Cell(10, 10, 10))
        .Build()
        .Ghosts[0];

    internal abstract Result<IDisposable, Reason> Register(RuleDescriptor rule);

    /// <summary>Validates one ghost with the registered rules and returns its findings (waiting for the worker if any).</summary>
    internal abstract ValueList<Finding> Check(Ghost ghost);

    internal abstract void EndWorld();

    /// <summary>The time limit per rule call.</summary>
    internal abstract TimeSpan Limit { get; }

    /// <summary>Lets the given time pass on the clock the subject times rules with; called from inside a rule.</summary>
    internal abstract void Elapse(TimeSpan time);

    [Fact]
    public void A_registered_rule_is_heard()
    {
        var why = Why("a");
        Must(Register(Rule("a", _ => new RuleVerdict.Fail(why))));
        Assert.Contains(new Finding.WillFail(Ghost.Id, why), Check(Ghost));
    }

    [Fact]
    public void Rules_run_in_registration_order()
    {
        var log = new List<string>();
        foreach (var name in new[] { "a", "b", "c" })
            Must(Register(Rule(name, _ => Log(log, name))));
        Check(Ghost);
        Assert.Equal(new[] { "a", "b", "c" }, log);
    }

    [Fact]
    public void An_id_is_registered_once()
    {
        Must(Register(Rule("a", _ => new RuleVerdict.Pass())));
        Assert.True(Register(Rule("a", _ => new RuleVerdict.Pass())).IsError);
    }

    [Fact]
    public void Disposing_during_a_check_takes_effect_from_the_next_check()
    {
        var log = new List<string>();
        IDisposable? b = null;
        Must(Register(Rule("a", _ =>
        {
            b?.Dispose();
            return Log(log, "a");
        })));
        b = Must(Register(Rule("b", _ => Log(log, "b"))));

        Check(Ghost);
        log.Clear();
        Check(Ghost);

        Assert.Equal(new[] { "a" }, log);
    }

    [Fact]
    public void A_rule_registered_during_a_check_runs_from_the_next_check()
    {
        var log = new List<string>();
        var registered = false;
        Must(Register(Rule("a", _ =>
        {
            if (!registered)
            {
                registered = true;
                Must(Register(Rule("late", _ => Log(log, "late"))));
            }

            return Log(log, "a");
        })));

        Check(Ghost);
        Assert.Equal(new[] { "a" }, log);
        Check(Ghost);
        Assert.Equal(new[] { "a", "a", "late" }, log);
    }

    [Fact]
    public void Registrations_end_with_the_world()
    {
        var called = 0;
        Must(Register(Rule("a", _ =>
        {
            called++;
            return new RuleVerdict.Pass();
        })));
        EndWorld();
        Check(Ghost);
        Assert.Equal(0, called);
    }

    [Fact]
    public void A_throwing_rule_is_switched_off_and_reported_once()
    {
        var throwing = Rule("throws", _ => throw new InvalidOperationException("rule bug"));
        var calls = 0;
        Must(Register(throwing));
        Must(Register(Rule("after", _ =>
        {
            calls++;
            return new RuleVerdict.Pass();
        })));

        var first = Check(Ghost);
        var second = Check(Ghost);

        Assert.Equal(1, CountReasons(first, ReasonCodes.RuleSwitchedOff, throwing.Id));
        Assert.Equal(0, CountReasons(second, ReasonCodes.RuleSwitchedOff, throwing.Id));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void A_slow_rule_times_out_and_its_verdict_is_dropped()
    {
        var slowWhy = Why("slow");
        var fastWhy = Why("fast");
        var slow = Rule("slow", _ =>
        {
            Elapse(Limit + Limit);
            return new RuleVerdict.Fail(slowWhy);
        });
        Must(Register(slow));
        Must(Register(Rule("fast", _ => new RuleVerdict.Fail(fastWhy))));

        var findings = Check(Ghost);

        Assert.Equal(1, CountReasons(findings, ReasonCodes.RuleTimedOut, slow.Id));
        Assert.DoesNotContain(new Finding.WillFail(Ghost.Id, slowWhy), findings);
        Assert.Contains(new Finding.WillFail(Ghost.Id, fastWhy), findings);
    }

    private static RuleDescriptor Rule(string name, Func<RuleInput, RuleVerdict> check) =>
        new(TestKitIds.Must(RuleId.Create(Mod, name)), check);

    private static Reason Why(string name) =>
        Reason.Of(TestKitIds.Must(ReasonCode.Create(Mod, name)), TestKitIds.Must(RuleId.Create(Mod, name)));

    private static RuleVerdict Log(List<string> log, string name)
    {
        log.Add(name);
        return new RuleVerdict.Pass();
    }

    private static int CountReasons(ValueList<Finding> findings, ReasonCode code, RuleId from)
    {
        var count = 0;
        foreach (var finding in findings)
        {
            var why = finding switch
            {
                Finding.WillFail f => f.Why,
                Finding.Warns w => w.Why,
                _ => null,
            };
            if (why is not null && why.Code == code && why.From == from)
                count++;
        }

        return count;
    }

    private static T Must<T>(Result<T, Reason> result) where T : notnull => TestKitIds.Must(result);
}
