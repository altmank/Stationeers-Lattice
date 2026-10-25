using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;

namespace Lattice.TestKit.Rules;

/// <summary>
/// A reference rule host written to the decided rule contract, the test kit's subject for the rule suite
/// (<see cref="Contracts.RuleHostContract"/>): rules run in registration order
/// from a copied array (a registration or disposal during a check takes effect from the next check); a rule that throws
/// is switched off for the world and reported once (<see cref="ReasonCodes.RuleSwitchedOff"/>); a rule that answers
/// after the limit gives <see cref="ReasonCodes.RuleTimedOut"/> and its verdict is dropped.
/// Runs every rule on the calling thread; times each call on <paramref name="clock"/>, so tests decide
/// how long a rule takes.
/// </summary>
internal sealed class FakeRuleHost(IWorldQuery world, TimeSpan limit, IClock clock)
{
    private RuleDescriptor[] _rules = [];
    private readonly HashSet<RuleId> _switchedOff = [];

    public TimeSpan Limit => limit;

    public Result<IDisposable, Reason> Register(RuleDescriptor rule)
    {
        foreach (var existing in _rules)
            if (existing.Id == rule.Id)
                return Result<IDisposable, Reason>.Error(
                    Reason.Of(ReasonCodes.InvalidValue, rule.Id, ValueMap<string, string>.Of("rule", "already-registered")));
        _rules = [.. _rules, rule];
        return Result<IDisposable, Reason>.Ok(new Registration(this, rule));
    }

    public ValueList<Finding> Check(Ghost ghost)
    {
        var findings = new List<Finding>();
        var input = new RuleInput(ghost, world);
        foreach (var rule in _rules)
        {
            if (_switchedOff.Contains(rule.Id))
                continue;
            RuleVerdict verdict;
            var started = clock.Now;
            try
            {
                verdict = rule.Check(input);
            }
            catch (Exception)
            {
                _switchedOff.Add(rule.Id);
                findings.Add(new Finding.Warns(ghost.Id, Reason.Of(ReasonCodes.RuleSwitchedOff, rule.Id)));
                continue;
            }

            if (clock.Now - started > limit)
            {
                findings.Add(new Finding.Warns(ghost.Id, Reason.Of(ReasonCodes.RuleTimedOut, rule.Id)));
                continue;
            }

            switch (verdict)
            {
                case RuleVerdict.Fail fail:
                    findings.Add(new Finding.WillFail(ghost.Id, fail.Why));
                    break;
                case RuleVerdict.Warn warn:
                    findings.Add(new Finding.Warns(ghost.Id, warn.Why));
                    break;
            }
        }

        if (findings.Count == 0)
            findings.Add(new Finding.Passes(ghost.Id));
        return ValueList.From(findings);
    }

    /// <summary>The world ends: every registration ends with it.</summary>
    public void End()
    {
        _rules = [];
        _switchedOff.Clear();
    }

    private void Remove(RuleDescriptor rule)
    {
        var at = Array.IndexOf(_rules, rule);
        if (at < 0)
            return;
        var next = new List<RuleDescriptor>(_rules);
        next.RemoveAt(at);
        _rules = next.ToArray();
    }

    private sealed class Registration(FakeRuleHost host, RuleDescriptor rule) : IDisposable
    {
        public void Dispose() => host.Remove(rule);
    }
}
