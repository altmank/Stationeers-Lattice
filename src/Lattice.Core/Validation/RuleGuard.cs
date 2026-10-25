using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;

namespace Lattice.Core.Validation;

/// <summary>A mod's rule's answer after Lattice's treatment: its time limit, and switched off once it throws.</summary>
internal abstract record Guarded<T>
{
    private Guarded() { }

    /// <summary>It answered within the limit.</summary>
    /// <param name="Value">Its answer.</param>
    public sealed record Answered(T Value) : Guarded<T>;

    /// <summary>It did not answer within the limit; a late answer is dropped.</summary>
    public sealed record TimedOut : Guarded<T>;

    /// <summary>It threw, on this call or before in this world, and is switched off.</summary>
    public sealed record SwitchedOff : Guarded<T>;
}

/// <summary>How one call into a mod's rule went, before switch-off is applied.</summary>
internal abstract record CallResult<T>
{
    private CallResult() { }

    /// <summary>It returned within the limit.</summary>
    public sealed record Returned(T Value) : CallResult<T>;

    /// <summary>It returned past the limit, has not returned yet, or is still inside an earlier call.</summary>
    public sealed record Late : CallResult<T>;

    /// <summary>It threw.</summary>
    public sealed record Threw(Exception Error) : CallResult<T>;
}

/// <summary>Calls a mod's rule under a time limit.</summary>
internal interface IRuleCalls
{
    CallResult<T> Call<T>(RuleId rule, TimeSpan limit, Func<T> call);
}

/// <summary>
/// Calls on the calling thread, timed after they return: the treatment for main-thread rules, which cannot be
/// pre-empted (one that never returns stalls the frame, the mod's own failure), and for tests.
/// </summary>
internal sealed class InlineRuleCalls(IClock clock) : IRuleCalls
{
    public CallResult<T> Call<T>(RuleId rule, TimeSpan limit, Func<T> call)
    {
        var started = clock.Now;
        T value;
        try
        {
            value = call();
        }
        catch (Exception error)
        {
            return new CallResult<T>.Threw(error);
        }

        return clock.Now - started > limit ? new CallResult<T>.Late() : new CallResult<T>.Returned(value);
    }
}

/// <summary>
/// The decided treatment of mods' rules, shared by support and validation rules: Lattice's time limit per call (the
/// contract's 2 ms), the same for every rule; a rule that throws is switched off for the rest of the world and
/// reported once. Worker rules go through <see cref="Worker"/> (a rule thread with a deadline in production), main
/// rules through <see cref="Main"/> (timed after they return). Thread-safe: the switch-off state is locked.
/// </summary>
internal sealed class RuleGuard
{
    /// <summary>The time limit per call, the same for every rule (the contract's 2 ms).</summary>
    public static TimeSpan DefaultLimit => TimeSpan.FromMilliseconds(2);

    private readonly object _gate = new();
    private readonly HashSet<RuleId> _switchedOff = [];
    private readonly Dictionary<RuleId, (GhostId Ghost, bool Settled)> _reportedOn = [];

    public RuleGuard(TimeSpan limit, IRuleCalls worker, IRuleCalls main)
    {
        Limit = limit;
        Worker = worker;
        Main = main;
    }

    /// <summary>Every call on the calling thread, timed on the clock.</summary>
    public RuleGuard(TimeSpan limit, IClock clock)
        : this(limit, new InlineRuleCalls(clock), new InlineRuleCalls(clock))
    {
    }

    public TimeSpan Limit { get; }

    public IRuleCalls Worker { get; }

    public IRuleCalls Main { get; }

    public bool IsSwitchedOff(RuleId rule)
    {
        lock (_gate)
            return _switchedOff.Contains(rule);
    }

    /// <summary>Switches a rule off for the rest of the world.</summary>
    public void SwitchOff(RuleId rule)
    {
        lock (_gate)
            _switchedOff.Add(rule);
    }

    /// <summary>
    /// Whether a switched-off rule is reported on this ghost: a rule is reported once per world. The report counts as
    /// given once a complete judgement of that ghost carried it (<see cref="Settle"/>); until then a redone judgement
    /// of the same ghost (one that lacked a main-thread fact) reports it again.
    /// </summary>
    public bool ReportSwitchOff(RuleId rule, GhostId ghost)
    {
        lock (_gate)
        {
            if (!_switchedOff.Contains(rule))
                return false;
            if (_reportedOn.TryGetValue(rule, out var on))
                return !on.Settled && on.Ghost == ghost;
            _reportedOn.Add(rule, (ghost, false));
            return true;
        }
    }

    /// <summary>A complete judgement of the ghost was made: the reports it carried count as given.</summary>
    public void Settle(GhostId ghost)
    {
        lock (_gate)
        {
            List<RuleId>? settled = null;
            foreach (var pair in _reportedOn)
                if (!pair.Value.Settled && pair.Value.Ghost == ghost)
                    (settled ??= []).Add(pair.Key);
            if (settled is null)
                return;
            foreach (var rule in settled)
                _reportedOn[rule] = (ghost, true);
        }
    }

    /// <summary>Calls the rule on its declared thread's treatment.</summary>
    public Guarded<T> Call<T>(RuleId rule, CallbackThread thread, Func<T> call)
    {
        if (IsSwitchedOff(rule))
            return new Guarded<T>.SwitchedOff();
        var calls = thread == CallbackThread.Main ? Main : Worker;
        switch (calls.Call(rule, Limit, call))
        {
            case CallResult<T>.Returned returned:
                return new Guarded<T>.Answered(returned.Value);
            case CallResult<T>.Threw:
                SwitchOff(rule);
                return new Guarded<T>.SwitchedOff();
            default:
                return new Guarded<T>.TimedOut();
        }
    }
}
