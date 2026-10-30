using System.Collections.Generic;
using System.Text;

namespace Lattice.Api;

/// <summary>
/// A set of the 24 <see cref="Turn"/>s. A value: two sets with the same turns are equal. Constant cost to ask, add and
/// count; allocates only <see cref="ToList"/> and <see cref="ToString"/>. Added in contract 0.21.
/// </summary>
public readonly record struct TurnSet
{
    private const uint Every = (1u << Turn.Count) - 1;

    private readonly uint _mask;

    private TurnSet(uint mask) => _mask = mask;

    /// <summary>No turn. <c>default</c> is this set.</summary>
    public static TurnSet None => default;

    /// <summary>All 24 turns.</summary>
    public static TurnSet All => new(Every);

    /// <summary>How many turns the set holds, 0 to 24.</summary>
    public int Count
    {
        get
        {
            var count = 0;
            for (var bits = _mask; bits != 0; bits &= bits - 1)
                count++;
            return count;
        }
    }

    /// <summary>Whether the set holds every turn.</summary>
    public bool IsAll => _mask == Every;

    /// <summary>The set of the turns given.</summary>
    public static TurnSet From(IEnumerable<Turn> turns)
    {
        var set = None;
        foreach (var turn in turns)
            set = set.With(turn);
        return set;
    }

    /// <summary>Whether the set holds the turn.</summary>
    public bool Contains(Turn turn) => (_mask & (1u << turn.Index)) != 0;

    /// <summary>This set and the turn.</summary>
    public TurnSet With(Turn turn) => new(_mask | (1u << turn.Index));

    /// <summary>The turns, by index.</summary>
    public ValueList<Turn> ToList()
    {
        var turns = new List<Turn>(Count);
        for (var index = 0; index < Turn.Count; index++)
            if ((_mask & (1u << index)) != 0 && Turn.Create(index).TryGet(out var turn))
                turns.Add(turn);
        return ValueList.From(turns);
    }

    /// <summary><c>all</c>, <c>none</c>, or the turn indexes by number, comma separated.</summary>
    public override string ToString()
    {
        if (IsAll)
            return "all";
        if (_mask == 0)
            return "none";
        var text = new StringBuilder(64);
        for (var index = 0; index < Turn.Count; index++)
            if ((_mask & (1u << index)) != 0)
                text.Append(text.Length > 0 ? "," : "").Append(index);
        return text.ToString();
    }
}
