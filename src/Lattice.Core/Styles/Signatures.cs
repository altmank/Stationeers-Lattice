using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Styles;

/// <summary>
/// One atomic facet some rule reads: a leaf of a <see cref="StyleMatch"/> (never <c>All</c>, <c>Any</c> or <c>Not</c>),
/// or a highlight's membership. Counted by its users (rules of live layers, a highlight); a leaf no one uses is retired:
/// it reads false, and the next generation leaves it out.
/// </summary>
internal sealed class Leaf
{
    private Leaf(StyleMatch? match, HighlightEntry? highlight)
    {
        Match = match;
        Highlight = highlight;
        Relational = match is StyleMatch.Picks picks && Facets.IsRelational(picks.Which);
        Kind = highlight is not null ? LeafKind.Highlight : Facets.KindOf(match!);
    }

    /// <summary>Which facet it reads, worked out once so judging it needs no type tests.</summary>
    public LeafKind Kind { get; }

    /// <summary>The match it stands for; null for a highlight's leaf.</summary>
    public StyleMatch? Match { get; }

    /// <summary>The highlight whose targets it holds for; null for a match leaf.</summary>
    public HighlightEntry? Highlight { get; }

    /// <summary>A selector the board answers as a whole (network, room, run through, or an and/or holding one).</summary>
    public bool Relational { get; }

    /// <summary>A relational leaf's picked ghosts, as of <see cref="PickedAt"/>; null until first worked out.</summary>
    public HashSet<GhostId>? Picked { get; set; }

    /// <summary>The board and selectors revisions <see cref="Picked"/> was worked out at.</summary>
    public (long Board, long Selectors) PickedAt { get; set; } = (-1, -1);

    public int Uses { get; set; }

    public bool Retired => Uses <= 0;

    public static Leaf Of(StyleMatch match) => new(match, null);

    public static Leaf Of(HighlightEntry highlight) => new(null, highlight);
}

/// <summary>Which facet a leaf reads.</summary>
internal enum LeafKind
{
    Unknown = 0,
    Picks = 1,
    Intent = 2,
    Part = 3,
    State = 4,
    Finding = 5,
    Status = 6,
    Family = 7,
    InSet = 8,
    Mine = 9,
    Tinted = 10,
    Draft = 11,
    Pending = 12,
    Structure = 13,
    Highlight = 14,
    Finishes = 15,
    Facing = 16,
}

/// <summary>
/// A match compiled against one generation's leaf bits: judged on a signature alone. A leaf a signature has no bit
/// for (the signature is older than the leaf) reads false.
/// </summary>
internal abstract class Compiled
{
    public static readonly Compiled True = new Constant(true);

    public static readonly Compiled False = new Constant(false);

    public abstract bool Holds(Signature signature);

    private sealed class Constant(bool value) : Compiled
    {
        public override bool Holds(Signature signature) => value;
    }

    public sealed class Bit(int bit) : Compiled
    {
        public override bool Holds(Signature signature) => signature.Has(bit);
    }

    public sealed class All(Compiled[] parts) : Compiled
    {
        public override bool Holds(Signature signature)
        {
            foreach (var part in parts)
                if (!part.Holds(signature))
                    return false;
            return true;
        }
    }

    public sealed class Any(Compiled[] parts) : Compiled
    {
        public override bool Holds(Signature signature)
        {
            foreach (var part in parts)
                if (part.Holds(signature))
                    return true;
            return false;
        }
    }

    public sealed class Not(Compiled part) : Compiled
    {
        public override bool Holds(Signature signature) => !part.Holds(signature);
    }
}

/// <summary>
/// The facets of a drawn thing as one value: the bits of its generation's leaves that hold, its tint, and its owner
/// (only while some rule paints with the owner's colour; -1 otherwise). Things with equal signatures look alike, so
/// the look is resolved and cached per signature. Interned per generation; compared by reference.
/// </summary>
internal sealed class Signature
{
    private readonly ulong[] _words;

    public Signature(Generation generation, ulong[] words, Option<Tint> tint, int owner, int finish = -1)
    {
        Generation = generation;
        _words = words;
        Tint = tint;
        Owner = owner;
        Finish = finish;
    }

    public Generation Generation { get; }

    public Option<Tint> Tint { get; }

    /// <summary>The owner's index in the owner ramp's order of first sight; -1 for none.</summary>
    public int Owner { get; }

    /// <summary>The game paint index the thing's finish asks for, while some rule paints with it; -1 for none.</summary>
    public int Finish { get; }

    /// <summary>The look, as of <see cref="LookVersion"/>; null until first resolved.</summary>
    public ResolvedLook? Look { get; set; }

    public int LookVersion { get; set; } = -1;

    /// <summary>How many things hold it now.</summary>
    public int Holders { get; set; }

    public ReadOnlySpan<ulong> Words => _words;

    public bool Has(int bit)
    {
        var word = bit >> 6;
        return word < _words.Length && (_words[word] & (1UL << (bit & 63))) != 0;
    }
}

/// <summary>
/// The leaves, rule program and signatures that belong together. A layer added, replaced or removed (or a policy
/// change, or a board reset) starts a new generation built from the live leaves only, so bits stay compact; things move
/// to it in drawing slices while the one before keeps drawing them.
/// </summary>
internal sealed class Generation
{
    private readonly Dictionary<Leaf, int> _bits = new();
    private readonly Dictionary<SignatureKey, Signature> _signatures = new(new SignatureKey.Comparer());
    private readonly List<Leaf> _leaves = new(64);

    public Generation(int number) => Number = number;

    public int Number { get; }

    public IReadOnlyList<Leaf> Leaves => _leaves;

    /// <summary>Words per signature for the leaves known now.</summary>
    public int Words => (_leaves.Count + 63) >> 6;

    public RuleProgram Program { get; set; } = RuleProgram.Empty;

    /// <summary>Whether some rule paints with the owner's colour, so signatures carry the owner.</summary>
    public bool UsesOwner { get; set; }

    /// <summary>Whether some rule paints with the finish's colour, so signatures carry the finish's paint.</summary>
    public bool UsesFinish { get; set; }

    public int SignatureCount => _signatures.Count;

    public IEnumerable<Signature> Signatures => _signatures.Values;

    /// <summary>The leaf's bit, given one if new. New bits read false in every signature made before.</summary>
    public int BitOf(Leaf leaf)
    {
        if (_bits.TryGetValue(leaf, out var bit))
            return bit;
        bit = _leaves.Count;
        _leaves.Add(leaf);
        _bits[leaf] = bit;
        return bit;
    }

    public bool TryBit(Leaf leaf, out int bit) => _bits.TryGetValue(leaf, out bit);

    /// <summary>The signature with these words, tint and owner: an existing one without allocating, else a new one.</summary>
    public Signature Intern(ulong[] words, int count, Option<Tint> tint, int owner, int finish = -1)
    {
        var probe = new SignatureKey(words, count, tint, owner, finish);
        if (_signatures.TryGetValue(probe, out var found))
            return found;
        var own = new ulong[Trimmed(words, count)];
        Array.Copy(words, own, own.Length);
        var made = new Signature(this, own, tint, owner, finish);
        _signatures[new SignatureKey(own, own.Length, tint, owner, finish)] = made;
        return made;
    }

    /// <summary>Drops signatures no thing holds (after a generation's things have moved, or a highlight ended).</summary>
    public void DropUnheld()
    {
        List<SignatureKey>? unheld = null;
        foreach (var pair in _signatures)
            if (pair.Value.Holders <= 0)
                (unheld ??= new List<SignatureKey>()).Add(pair.Key);
        if (unheld is null)
            return;
        foreach (var key in unheld)
            _signatures.Remove(key);
    }

    private static int Trimmed(ulong[] words, int count)
    {
        while (count > 0 && words[count - 1] == 0)
            count--;
        return count;
    }

    /// <summary>A signature's identity: words compared with missing words as zero, then tint, owner and finish paint.</summary>
    private readonly struct SignatureKey(ulong[] words, int count, Option<Tint> tint, int owner, int finish)
    {
        private readonly ulong[] _words = words;
        private readonly int _count = count;
        private readonly Option<Tint> _tint = tint;
        private readonly int _owner = owner;
        private readonly int _finish = finish;

        public sealed class Comparer : IEqualityComparer<SignatureKey>
        {
            public bool Equals(SignatureKey a, SignatureKey b)
            {
                if (a._owner != b._owner || a._finish != b._finish || !a._tint.Equals(b._tint))
                    return false;
                var most = Math.Max(a._count, b._count);
                for (var i = 0; i < most; i++)
                {
                    var x = i < a._count ? a._words[i] : 0UL;
                    var y = i < b._count ? b._words[i] : 0UL;
                    if (x != y)
                        return false;
                }

                return true;
            }

            public int GetHashCode(SignatureKey key)
            {
                var hash = (((key._owner * 397) ^ key._finish) * 397) ^ key._tint.GetHashCode();
                for (var i = 0; i < key._count; i++)
                    if (key._words[i] != 0)
                        hash = (hash * 31) ^ i ^ key._words[i].GetHashCode();
                return hash;
            }
        }
    }
}
