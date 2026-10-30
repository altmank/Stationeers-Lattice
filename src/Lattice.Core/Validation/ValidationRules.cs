using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Validation;

/// <summary>
/// The validation rules of one world that mods register (<see cref="IRegistries.Register(RuleDescriptor)"/>). Rules
/// run in registration order from a copied array, so a registration or disposal during a check takes effect from the
/// next check; registrations end with the world. Each call gets the decided treatment through the shared
/// <see cref="RuleGuard"/>: Lattice's time limit, switched off if it throws,
/// reported once. Worker rules run on a rule thread with a deadline; main-thread rules run in the main thread's
/// checking slices and are timed after they return. Registration is on the main thread.
/// </summary>
internal sealed class ValidationRules(RuleGuard guard)
{
    private volatile Entry[] _rules = [];

    public RuleGuard Guard => guard;

    /// <summary>Rises with every registration and disposal, so cached judgements know to look again. Main thread.</summary>
    public int Generation { get; private set; }

    /// <summary>A registered rule.</summary>
    internal sealed class Entry(RuleDescriptor rule, Func<Ghost, bool>? applies, bool inline)
    {
        public RuleDescriptor Rule => rule;

        /// <summary>
        /// Whether the rule has anything to say about a ghost; a ghost it does not apply to passes it without a call.
        /// Lattice's own rules only: every mod rule applies to every ghost.
        /// </summary>
        public bool AppliesTo(Ghost ghost) => applies is null || applies(ghost);

        /// <summary>
        /// Called directly on the validation worker, with no rule thread and no guard: Lattice's own constant checks
        /// over what the worker holds already. Mod rules are never inline.
        /// </summary>
        public bool Inline => inline;
    }

    /// <summary>The rules now registered, in registration order.</summary>
    public IReadOnlyList<Entry> Current => _rules;

    /// <summary>Adds a rule; refused for an id already registered.</summary>
    public Result<IDisposable, Reason> Register(RuleDescriptor rule) => Register(rule, null, false);

    /// <summary>
    /// Adds one of Lattice's own rules: <paramref name="applies"/> says which ghosts it is asked about (none: every
    /// ghost), and an <paramref name="inline"/> rule is called on the worker directly (<see cref="Entry.Inline"/>).
    /// </summary>
    public Result<IDisposable, Reason> Register(RuleDescriptor rule, Func<Ghost, bool>? applies, bool inline)
    {
        foreach (var existing in _rules)
            if (existing.Rule.Id == rule.Id)
                return Result<IDisposable, Reason>.Error(
                    Reason.Of(ReasonCodes.AlreadyRegistered, rule.Id, ValueMap<string, string>.Of("rule", rule.Id.Value)));
        var entry = new Entry(rule, applies, inline);
        var next = new Entry[_rules.Length + 1];
        Array.Copy(_rules, next, _rules.Length);
        next[_rules.Length] = entry;
        _rules = next;
        Generation++;
        return Result<IDisposable, Reason>.Ok(new Registration(this, entry));
    }

    /// <summary>The world ends: every registration ends with it.</summary>
    public void End()
    {
        _rules = [];
        Generation++;
    }

    /// <summary>The rule's answer for a ghost, under the guard.</summary>
    public Guarded<RuleVerdict> Check(Entry entry, RuleInput input) =>
        guard.Call(entry.Rule.Id, entry.Rule.Thread, () => entry.Rule.Check(input));

    /// <summary>Whether a switched-off rule is reported on this ghost (once per world, on one ghost: <see cref="RuleGuard.ReportSwitchOff"/>).</summary>
    public bool ReportSwitchOff(Entry entry, GhostId ghost) => guard.ReportSwitchOff(entry.Rule.Id, ghost);

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

    private sealed class Registration(ValidationRules rules, Entry entry) : IDisposable
    {
        public void Dispose() => rules.Remove(entry);
    }
}
