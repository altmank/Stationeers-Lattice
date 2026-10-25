using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Styles;

/// <summary>What a membership change asks of the styles: re-signature a ghost's things; draw or stop drawing a structure's overlay.</summary>
internal interface IMembership
{
    /// <summary>The ghost entered or left a set: its things, and only they, take new signatures.</summary>
    void Moved(GhostId ghost);

    /// <summary>The structure entered or left a set or highlight: its overlay is drawn while it is in one.</summary>
    void Overlay(StructureRef structure);
}

/// <summary>Structures by id, else by piece and cell, as <see cref="IStructureSet.Contains"/> reads them.</summary>
internal sealed class StructureRefs
{
    private readonly Dictionary<ReferenceId, StructureRef> _byId = new();
    private readonly Dictionary<(PrefabKey, Cell), StructureRef> _bySpot = new();

    public int Count => _byId.Count;

    public IEnumerable<StructureRef> All => _byId.Values;

    public bool Contains(StructureRef structure) =>
        _byId.ContainsKey(structure.Id) || _bySpot.ContainsKey((structure.Piece, structure.At));

    public bool Add(StructureRef structure)
    {
        if (Contains(structure))
            return false;
        _byId[structure.Id] = structure;
        _bySpot[(structure.Piece, structure.At)] = structure;
        return true;
    }

    /// <summary>Removes the member this ref names; the member as held, or none.</summary>
    public Option<StructureRef> Remove(StructureRef structure)
    {
        if (!_byId.TryGetValue(structure.Id, out var held) && !_bySpot.TryGetValue((structure.Piece, structure.At), out held))
            return Option<StructureRef>.None;
        _byId.Remove(held.Id);
        _bySpot.Remove((held.Piece, held.At));
        return Option.Some(held);
    }

    public void Clear()
    {
        _byId.Clear();
        _bySpot.Clear();
    }
}

/// <summary>A ghost set (hover, selection, a mod's own). Each change costs the ghosts entering and leaving, in the call.</summary>
internal sealed class StyleSet(StyleSetId id, IMembership styles) : IStyleSet
{
    private readonly HashSet<GhostId> _members = new();
    private readonly List<GhostId> _left = new(16);

    public StyleSetId Id { get; } = id;

    public int Count => _members.Count;

    public bool Contains(GhostId ghost) => _members.Contains(ghost);

    public void Put(GhostId ghost)
    {
        if (_members.Count == 1 && _members.Contains(ghost))
            return;
        Empty();
        if (_members.Add(ghost))
            styles.Moved(ghost);
    }

    public void Replace(ValueList<GhostId> ghosts)
    {
        _left.Clear();
        foreach (var member in _members)
            if (!Has(ghosts, member))
                _left.Add(member);
        foreach (var gone in _left)
        {
            _members.Remove(gone);
            styles.Moved(gone);
        }

        _left.Clear();
        Add(ghosts);
    }

    public void Add(ValueList<GhostId> ghosts)
    {
        for (var i = 0; i < ghosts.Count; i++)
            if (_members.Add(ghosts[i]))
                styles.Moved(ghosts[i]);
    }

    public void Remove(ValueList<GhostId> ghosts)
    {
        for (var i = 0; i < ghosts.Count; i++)
            if (_members.Remove(ghosts[i]))
                styles.Moved(ghosts[i]);
    }

    public void Clear() => Empty();

    /// <summary>A ghost left the board: it leaves the set with no re-signature (it is not drawn any more).</summary>
    public void Forget(GhostId ghost) => _members.Remove(ghost);

    private void Empty()
    {
        if (_members.Count == 0)
            return;
        _left.Clear();
        foreach (var member in _members)
            _left.Add(member);
        _members.Clear();
        foreach (var gone in _left)
            styles.Moved(gone);
        _left.Clear();
    }

    private static bool Has(ValueList<GhostId> ghosts, GhostId ghost)
    {
        for (var i = 0; i < ghosts.Count; i++)
            if (ghosts[i] == ghost)
                return true;
        return false;
    }
}

/// <summary>A structure set: built structures drawn as overlays while they are members. Each change costs the structures entering and leaving.</summary>
internal sealed class StructureSet(StyleSetId id, IMembership styles) : IStructureSet
{
    private readonly StructureRefs _members = new();
    private readonly List<StructureRef> _left = new(16);

    public StyleSetId Id { get; } = id;

    public int Count => _members.Count;

    public bool Contains(StructureRef structure) => _members.Contains(structure);

    public void Put(StructureRef structure)
    {
        if (_members.Count == 1 && _members.Contains(structure))
            return;
        Clear();
        Add(structure);
    }

    public void Replace(ValueList<StructureRef> structures)
    {
        Clear();
        Add(structures);
    }

    public void Add(ValueList<StructureRef> structures)
    {
        for (var i = 0; i < structures.Count; i++)
            Add(structures[i]);
    }

    public void Remove(ValueList<StructureRef> structures)
    {
        for (var i = 0; i < structures.Count; i++)
            if (_members.Remove(structures[i]).TryGet(out var held))
                styles.Overlay(held);
    }

    public void Clear()
    {
        if (_members.Count == 0)
            return;
        _left.Clear();
        _left.AddRange(_members.All);
        _members.Clear();
        foreach (var gone in _left)
            styles.Overlay(gone);
        _left.Clear();
    }

    private void Add(StructureRef structure)
    {
        if (_members.Add(structure))
            styles.Overlay(structure);
    }
}

/// <summary>
/// A timed highlight: its targets are a set of its own with a leaf of its own, so adding and ending it re-signatures
/// its targets only. Its rule sits in the highlight band, after earlier highlights.
/// </summary>
internal sealed class HighlightEntry : IDisposable
{
    private readonly Action<HighlightEntry> _end;

    public HighlightEntry(long registration, Style style, TimeSpan until, HashSet<GhostId> ghosts, StructureRefs structures, Action<HighlightEntry> end)
    {
        Registration = registration;
        Style = style;
        Until = until;
        Ghosts = ghosts;
        Structures = structures;
        _end = end;
        Leaf = Leaf.Of(this);
        Leaf.Uses = 1;
    }

    public long Registration { get; }

    public Style Style { get; }

    /// <summary>When it ends by itself, on the styles' clock.</summary>
    public TimeSpan Until { get; }

    public HashSet<GhostId> Ghosts { get; }

    public StructureRefs Structures { get; }

    public Leaf Leaf { get; }

    public bool Ended { get; private set; }

    public bool Holds(Thing thing) => !Ended && thing.Kind switch
    {
        ThingKind.Ghost => Ghosts.Contains(thing.Id),
        ThingKind.Structure => Structures.Contains(thing.Structure),
        _ => false,
    };

    /// <summary>Ends it; the styles re-signature its targets. Ending an ended highlight does nothing.</summary>
    public void Dispose()
    {
        if (Ended)
            return;
        Ended = true;
        Leaf.Uses = 0;
        _end(this);
    }
}

/// <summary>Every ghost set, structure set and live highlight of the styles, by id.</summary>
internal sealed class StyleSets(IMembership styles)
{
    private readonly Dictionary<StyleSetId, StyleSet> _ghostSets = new();
    private readonly Dictionary<StyleSetId, StructureSet> _structureSets = new();

    public List<HighlightEntry> Highlights { get; } = new(8);

    public StyleSet Ghosts(StyleSetId id)
    {
        if (!_ghostSets.TryGetValue(id, out var set))
            _ghostSets[id] = set = new StyleSet(id, styles);
        return set;
    }

    public StructureSet Structures(StyleSetId id)
    {
        if (!_structureSets.TryGetValue(id, out var set))
            _structureSets[id] = set = new StructureSet(id, styles);
        return set;
    }

    /// <summary>Whether a thing is in the set with this id: a ghost by id, a structure by ref; a draft never.</summary>
    public bool Holds(StyleSetId id, Thing thing) => thing.Kind switch
    {
        ThingKind.Ghost => _ghostSets.TryGetValue(id, out var ghosts) && ghosts.Contains(thing.Id),
        ThingKind.Structure => _structureSets.TryGetValue(id, out var structures) && structures.Contains(thing.Structure),
        _ => false,
    };

    /// <summary>Whether any structure set or live highlight holds the structure, so its overlay is drawn.</summary>
    public bool Overlaid(StructureRef structure)
    {
        foreach (var set in _structureSets.Values)
            if (set.Contains(structure))
                return true;
        foreach (var highlight in Highlights)
            if (!highlight.Ended && highlight.Structures.Contains(structure))
                return true;
        return false;
    }

    /// <summary>A ghost left the board: it leaves every set and highlight.</summary>
    public void Forget(GhostId ghost)
    {
        foreach (var set in _ghostSets.Values)
            set.Forget(ghost);
        foreach (var highlight in Highlights)
            highlight.Ghosts.Remove(ghost);
    }
}
