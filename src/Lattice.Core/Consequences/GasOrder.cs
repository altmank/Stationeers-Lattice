using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Consequences;

/// <summary>
/// One fixed order of gases for <see cref="GasContents"/>: every gas of the readings it was built from, once, by name
/// (ordinal), so two readings of one world compare gas by gas. A modded gas is ordered like any other.
/// </summary>
internal sealed class GasOrder
{
    private readonly Dictionary<GasKind, int> _index;

    private GasOrder(Dictionary<GasKind, int> index) => _index = index;

    /// <summary>How many gases it orders.</summary>
    public int Count => _index.Count;

    /// <summary>The order over every gas the readings hold. Linear in the gases read, plus sorting the distinct ones.</summary>
    public static GasOrder Of(IEnumerable<ValueList<GasHeld>> readings)
    {
        var seen = new HashSet<GasKind>();
        var names = new List<GasKind>();
        foreach (var reading in readings)
            foreach (var held in reading)
                if (seen.Add(held.Gas))
                    names.Add(held.Gas);
        names.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
        var index = new Dictionary<GasKind, int>(names.Count);
        for (var at = 0; at < names.Count; at++)
            index.Add(names[at], at);
        return new GasOrder(index);
    }

    /// <summary>The gas's place in the order; none for a gas the readings did not hold.</summary>
    public Option<int> IndexOf(GasKind gas) => _index.TryGetValue(gas, out var at) ? Option.Some(at) : Option<int>.None;

    /// <summary>
    /// The held gases as contents in this order; a gas not in the order is a bug in the caller, which builds the
    /// order from the readings it then reads.
    /// </summary>
    public GasContents Contents(ValueList<GasHeld> held)
    {
        var moles = new double[Count];
        var energies = new double[Count];
        foreach (var gas in held)
        {
            if (!_index.TryGetValue(gas.Gas, out var at))
                throw new ArgumentException($"Gas {gas.Gas} is not in the order.", nameof(held));
            moles[at] += gas.Amount.Value;
            energies[at] += gas.Energy.Value;
        }

        return new GasContents(moles, energies);
    }
}
