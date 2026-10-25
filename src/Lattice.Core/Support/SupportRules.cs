using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;
using Lattice.Core.Validation;

namespace Lattice.Core.Support;

/// <summary>Whether a mod's rule claims a piece.</summary>
internal enum Claim
{
    No = 0,
    Yes = 1,

    /// <summary>It threw, on this call or before in this world, and is switched off.</summary>
    SwitchedOff = 2,

    /// <summary>It did not answer within the limit this time; asked again next time.</summary>
    TimedOut = 3,
}

/// <summary>
/// The support rules of one world that mods register. Rules run in registration order from a copied array, so a
/// registration or disposal during a check takes effect from the next check. A mod's rule gets the decided treatment
/// and nothing else, through the shared <see cref="RuleGuard"/>: a call past the
/// time limit is dropped as timed out; a rule that throws (from <c>AppliesTo</c> or <c>Check</c>) is switched off for
/// the rest of the world. Lattice's own census rules are not here: they are Lattice code and are not guarded.
/// Registration is on the main thread; checks run on the thread that judges (the worker), main-thread rules through
/// the facts the main thread answers.
/// </summary>
internal sealed class SupportRules
{
    /// <summary>The time limit per call, the same for every rule (the contract's 2 ms).</summary>
    public static TimeSpan DefaultLimit => RuleGuard.DefaultLimit;

    private volatile Entry[] _rules = [];

    /// <summary>Rules under a guard.</summary>
    public SupportRules(RuleGuard guard) => Guard = guard;

    /// <summary>Rules timed against a limit on a clock, every call on the calling thread.</summary>
    public SupportRules(TimeSpan limit, IClock clock)
        : this(new RuleGuard(limit, clock))
    {
    }

    /// <summary>Rules timed against the default limit on the real clock.</summary>
    public SupportRules()
        : this(DefaultLimit, new StopwatchClock())
    {
    }

    public RuleGuard Guard { get; }

    /// <summary>Rises with every registration and disposal, so cached judgements know to look again.</summary>
    public int Generation { get; private set; }

    /// <summary>A registered rule, its mode, and its answer to <c>AppliesTo</c> per piece (asked once per piece per world).</summary>
    internal sealed class Entry(SupportRuleDescriptor rule, SupportMode mode)
    {
        public SupportRuleDescriptor Rule => rule;

        public SupportMode Mode => mode;

        public Dictionary<PrefabKey, bool> Claims { get; } = [];
    }

    /// <summary>The rules now registered, in registration order.</summary>
    public IReadOnlyList<Entry> Current => _rules;

    /// <summary>Adds a rule in the mode it declares (<see cref="SupportRuleDescriptor.Mode"/>); refused for an id already registered.</summary>
    public Result<IDisposable, Reason> Register(SupportRuleDescriptor rule)
    {
        foreach (var existing in _rules)
            if (existing.Rule.Id == rule.Id)
                return Result<IDisposable, Reason>.Error(
                    Reason.Of(ReasonCodes.AlreadyRegistered, rule.Id, ValueMap<string, string>.Of("rule", rule.Id.Value)));
        var entry = new Entry(rule, rule.Mode);
        var next = new Entry[_rules.Length + 1];
        Array.Copy(_rules, next, _rules.Length);
        next[_rules.Length] = entry;
        _rules = next;
        Generation++;
        return Result<IDisposable, Reason>.Ok(new Registration(this, entry));
    }

    public bool IsSwitchedOff(Entry entry) => Guard.IsSwitchedOff(entry.Rule.Id);

    /// <summary>Whether a switched-off rule is reported on this ghost (once per world, on one ghost: <see cref="RuleGuard.ReportSwitchOff"/>).</summary>
    public bool ReportSwitchOff(Entry entry, GhostId ghost) => Guard.ReportSwitchOff(entry.Rule.Id, ghost);

    /// <summary>Whether the rule claims the piece, asked once per piece per world (a timed-out answer is asked again).</summary>
    public Claim Claims(Entry entry, PrefabKey piece)
    {
        if (Guard.IsSwitchedOff(entry.Rule.Id))
            return Claim.SwitchedOff;
        lock (entry.Claims)
            if (entry.Claims.TryGetValue(piece, out var known))
                return known ? Claim.Yes : Claim.No;
        switch (Guard.Call(entry.Rule.Id, entry.Rule.Thread, () => entry.Rule.AppliesTo(piece)))
        {
            case Guarded<bool>.Answered answered:
                lock (entry.Claims)
                    entry.Claims[piece] = answered.Value;
                return answered.Value ? Claim.Yes : Claim.No;
            case Guarded<bool>.SwitchedOff:
                return Claim.SwitchedOff;
            default:
                return Claim.TimedOut;
        }
    }

    /// <summary>The rule's answer for a placement, under the guard.</summary>
    public Guarded<SupportVerdict> Check(Entry entry, SupportInput input) =>
        Guard.Call(entry.Rule.Id, entry.Rule.Thread, () => entry.Rule.Check(input));

    private void Remove(Entry entry)
    {
        var rules = _rules;
        var index = Array.IndexOf(rules, entry);
        if (index < 0)
            return;
        var next = new Entry[rules.Length - 1];
        Array.Copy(rules, 0, next, 0, index);
        Array.Copy(rules, index + 1, next, index, rules.Length - index - 1);
        _rules = next;
        Generation++;
    }

    private sealed class Registration(SupportRules rules, Entry entry) : IDisposable
    {
        public void Dispose() => rules.Remove(entry);
    }
}
