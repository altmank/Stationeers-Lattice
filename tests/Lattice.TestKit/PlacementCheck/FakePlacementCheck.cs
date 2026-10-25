using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.TestKit.PlacementCheck;

/// <summary>
/// <see cref="IPlacementCheck"/> with rules the test gives: the first rule that refuses a placement answers with its
/// reason, which carries the game-style message as <see cref="Reason.GameMessage"/>; with no refusing rule the
/// placement is accepted. Every call is recorded in order.
/// </summary>
internal sealed class FakePlacementCheck : IPlacementCheck
{
    private readonly List<Func<Placement, Option<Reason>>> _rules = [];
    private readonly List<Placement> _calls = [];

    public IReadOnlyList<Placement> Calls => _calls;

    /// <summary>Adds a rule: refuse with <paramref name="code"/> and the game's <paramref name="message"/> when it matches.</summary>
    public FakePlacementCheck Refuse(Func<Placement, bool> when, ReasonCode code, string message)
    {
        var reason = new Reason(code, ValueMap<string, string>.Empty, Option.Some(message), TestKitIds.PlacementCheck);
        _rules.Add(p => when(p) ? Option.Some(reason) : Option<Reason>.None);
        return this;
    }

    /// <summary>Adds a rule that answers with any reason it likes, or none to pass.</summary>
    public FakePlacementCheck Rule(Func<Placement, Option<Reason>> rule)
    {
        _rules.Add(rule);
        return this;
    }

    public Result<Unit, Reason> Check(Placement placement)
    {
        _calls.Add(placement);
        foreach (var rule in _rules)
            if (rule(placement).TryGet(out var reason))
                return Result<Unit, Reason>.Error(reason);
        return Result<Unit, Reason>.Ok(Unit.Value);
    }
}
