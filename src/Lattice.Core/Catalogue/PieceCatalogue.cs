using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;

namespace Lattice.Core.Catalogue;

/// <summary>
/// <see cref="IPieceCatalogue"/> over a <see cref="CatalogueListing"/>: both lists sorted by prefab name (ordinal) and
/// every answer built once, so a read is a dictionary lookup that allocates nothing. A piece's first kit is the one
/// <see cref="KitHomes.Primary"/> picks among the kits that place it in source order, the same kit
/// <see cref="PrefabCatalogue"/> gives its facts; the rest follow in the sorted kit order. Families come from
/// <see cref="IPieceFamilies"/>, the registry style rules read; ports from <see cref="PiecePorts"/>. A key listed twice
/// keeps its first entry; a piece whose grid the listing could not tell is not listed.
/// </summary>
internal sealed class PieceCatalogue : IPieceCatalogue
{
    private readonly IPieceFamilies _families;
    private readonly PiecePorts _ports;
    private readonly Lattice.Core.Styles.Facings _facings;
    private readonly Dictionary<PrefabKey, PieceEntry> _pieces;
    private readonly Dictionary<KitKey, KitEntry> _kits;
    private readonly Dictionary<PrefabKey, Result<KitPlace, Reason>> _kitOf;

    public PieceCatalogue(CatalogueListing listing, IPieceFamilies families, PiecePorts ports, Lattice.Core.Styles.Facings facings)
    {
        _families = families;
        _ports = ports;
        _facings = facings;
        var sourceKits = new List<ListedKit>(listing.Kits.Count);
        _kits = new Dictionary<KitKey, KitEntry>(listing.Kits.Count);
        foreach (var kit in listing.Kits)
            if (_kits.TryAdd(kit.Key, new KitEntry(kit.Key, kit.PrefabName, kit.Name, kit.Options)))
                sourceKits.Add(kit);

        var kitEntries = new List<KitEntry>(_kits.Values);
        kitEntries.Sort(static (a, b) => string.CompareOrdinal(a.PrefabName, b.PrefabName));
        var rank = new Dictionary<KitKey, int>(kitEntries.Count);
        for (var i = 0; i < kitEntries.Count; i++)
            rank.Add(kitEntries[i].Key, i);
        Kits = ValueList.From(kitEntries);

        var homes = new Dictionary<PrefabKey, List<KitPlace>>(listing.Pieces.Count);
        foreach (var kit in sourceKits)
            for (var i = 0; i < kit.Options.Count; i++)
            {
                if (!KitOption.Create(i).TryGet(out var option))
                    continue;
                if (!homes.TryGetValue(kit.Options[i], out var places))
                    homes.Add(kit.Options[i], places = new List<KitPlace>(1));
                places.Add(new KitPlace(kit.Key, option));
            }

        _pieces = new Dictionary<PrefabKey, PieceEntry>(listing.Pieces.Count);
        _kitOf = new Dictionary<PrefabKey, Result<KitPlace, Reason>>(listing.Pieces.Count);
        var pieceEntries = new List<PieceEntry>(listing.Pieces.Count);
        foreach (var piece in listing.Pieces)
        {
            if (_pieces.ContainsKey(piece.Key))
                continue;
            var places = homes.TryGetValue(piece.Key, out var found) ? Ordered(found, piece.FirstStep, rank) : ValueList<KitPlace>.Empty;
            var entry = new PieceEntry(piece.Key, piece.PrefabName, piece.Name, piece.Grid, places);
            _pieces.Add(piece.Key, entry);
            pieceEntries.Add(entry);
            _kitOf.Add(piece.Key, places.Count > 0
                ? Result<KitPlace, Reason>.Ok(places[0])
                : Result<KitPlace, Reason>.Error(CatalogueReasons.NoKit(Number(piece.Key.Hash))));
        }

        pieceEntries.Sort(static (a, b) => string.CompareOrdinal(a.PrefabName, b.PrefabName));
        Pieces = ValueList.From(pieceEntries);
    }

    /// <inheritdoc />
    public ValueList<PieceEntry> Pieces { get; }

    /// <inheritdoc />
    public ValueList<KitEntry> Kits { get; }

    /// <inheritdoc />
    public Result<PieceEntry, Reason> Piece(PrefabKey piece) =>
        _pieces.TryGetValue(piece, out var entry)
            ? Result<PieceEntry, Reason>.Ok(entry)
            : Result<PieceEntry, Reason>.Error(Unknown(piece));

    /// <inheritdoc />
    public Result<KitEntry, Reason> Kit(KitKey kit) =>
        _kits.TryGetValue(kit, out var entry)
            ? Result<KitEntry, Reason>.Ok(entry)
            : Result<KitEntry, Reason>.Error(CatalogueReasons.Kit(kit.Hash));

    /// <inheritdoc />
    public Result<PrefabKey, Reason> PieceOf(KitKey kit, KitOption option)
    {
        if (!_kits.TryGetValue(kit, out var entry))
            return Result<PrefabKey, Reason>.Error(CatalogueReasons.Kit(kit.Hash));
        return option.Index < entry.Options.Count
            ? Result<PrefabKey, Reason>.Ok(entry.Options[option.Index])
            : Result<PrefabKey, Reason>.Error(CatalogueReasons.PastLastOption(kit.Hash, option.Index, entry.Options.Count));
    }

    /// <inheritdoc />
    public Result<KitPlace, Reason> KitOf(PrefabKey piece) =>
        _kitOf.TryGetValue(piece, out var place) ? place : Result<KitPlace, Reason>.Error(Unknown(piece));

    /// <inheritdoc />
    public Result<PieceFamily, Reason> FamilyOf(PrefabKey piece)
    {
        if (!_pieces.ContainsKey(piece))
            return Result<PieceFamily, Reason>.Error(Unknown(piece));
        var chain = _families.ChainOf(piece);
        return Result<PieceFamily, Reason>.Ok(chain[chain.Count - 1]);
    }

    /// <inheritdoc />
    public Result<ValueList<PieceFamily>, Reason> FamiliesOf(PrefabKey piece) =>
        _pieces.ContainsKey(piece)
            ? Result<ValueList<PieceFamily>, Reason>.Ok(_families.ChainOf(piece))
            : Result<ValueList<PieceFamily>, Reason>.Error(Unknown(piece));

    /// <inheritdoc />
    public Option<PieceFamily> ParentOf(PieceFamily family) => FamilyTree.ParentOf(family);

    /// <inheritdoc />
    public Result<Option<Axis>, Reason> FacingOf(Placement placement)
    {
        if (!_pieces.ContainsKey(placement.Piece))
            return Result<Option<Axis>, Reason>.Error(Unknown(placement.Piece));
        return Result<Option<Axis>, Reason>.Ok(_facings.Of(placement) is { } axis ? Option.Some(axis) : Option<Axis>.None);
    }

    /// <inheritdoc />
    public Result<ValueList<Port>, Reason> PortsAt(Placement placement) =>
        _pieces.ContainsKey(placement.Piece)
            ? _ports.At(placement)
            : Result<ValueList<Port>, Reason>.Error(Unknown(placement.Piece));

    // The game's choice first, then the others by the sorted kit order and option.
    private static ValueList<KitPlace> Ordered(List<KitPlace> homes, ValueList<ItemKey> firstStep, Dictionary<KitKey, int> rank)
    {
        var primary = KitHomes.Primary(homes, firstStep);
        var rest = new List<KitPlace>(homes.Count - 1);
        for (var i = 0; i < homes.Count; i++)
            if (i != primary)
                rest.Add(homes[i]);
        rest.Sort((a, b) =>
        {
            var byKit = rank[a.Kit].CompareTo(rank[b.Kit]);
            return byKit != 0 ? byKit : a.Option.Index.CompareTo(b.Option.Index);
        });
        var places = new KitPlace[homes.Count];
        places[0] = homes[primary];
        rest.CopyTo(places, 1);
        return ValueList.From(places);
    }

    private static Reason Unknown(PrefabKey piece) => CatalogueReasons.Piece(Number(piece.Hash), "no-prefab");

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// A short text that two seats' catalogue views share when they list the same pieces and kits the same way: counts and
/// an FNV-1a hash over keys, prefab names, grids, kit places and options, display names left out (they follow each
/// seat's language).
/// </summary>
internal static class CatalogueSummary
{
    public static string Of(IPieceCatalogue catalogue)
    {
        var hash = 14695981039346656037UL;
        foreach (var piece in catalogue.Pieces)
        {
            hash = Mix(hash, piece.Key.Hash);
            hash = Mix(hash, piece.PrefabName);
            hash = Mix(hash, (int)piece.Grid);
            foreach (var place in piece.Kits)
            {
                hash = Mix(hash, place.Kit.Hash);
                hash = Mix(hash, place.Option.Index);
            }
        }

        foreach (var kit in catalogue.Kits)
        {
            hash = Mix(hash, kit.Key.Hash);
            hash = Mix(hash, kit.PrefabName);
            foreach (var option in kit.Options)
                hash = Mix(hash, option.Hash);
        }

        return string.Format(CultureInfo.InvariantCulture, "pieces={0} kits={1} hash={2:x16}", catalogue.Pieces.Count, catalogue.Kits.Count, hash);
    }

    private static ulong Mix(ulong hash, int value)
    {
        for (var i = 0; i < 4; i++)
            hash = (hash ^ (byte)(value >> (8 * i))) * 1099511628211UL;
        return hash;
    }

    private static ulong Mix(ulong hash, string text)
    {
        foreach (var c in text)
            hash = Mix(hash, c);
        return Mix(hash, -1);
    }
}
