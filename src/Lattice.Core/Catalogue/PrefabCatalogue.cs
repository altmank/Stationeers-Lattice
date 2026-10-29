using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Catalogue;

/// <summary>
/// What reading every structure prefab found: how many mapped, why each kit piece that did not map was refused,
/// and the structures no kit places (burnt pieces, extensions, pieces a mod spawns by code).
/// </summary>
internal sealed record ReadReport(int Read, ValueList<Reason> Refused, ValueList<string> WithoutKit);

/// <summary>
/// <see cref="ICatalogue"/> over the game's prefabs, built at every world load. Kits are listed when it is built;
/// each piece is read from its prefab the first time it is asked for and kept, a refusal included. A piece lists every
/// kit that places it, the kit its first build step uses first (the game's own choice, Structure.cs:1278), else in
/// kit order; a structure no kit places is described with no kit.
/// </summary>
internal sealed class PrefabCatalogue : ICatalogue
{
    private readonly IPrefabSource _source;
    private readonly Dictionary<KitKey, KitFacts> _kits;
    private readonly Dictionary<KitKey, string> _kitNames;
    private readonly Dictionary<PrefabKey, List<KitPlace>> _homes;
    private readonly Dictionary<PrefabKey, Result<PieceFacts, Reason>> _pieces = new();
    private readonly Dictionary<PrefabKey, PrefabData> _read = new();
    private readonly Dictionary<KitKey, ItemRef> _mergeTools = new();
    private readonly Dictionary<ItemKey, string> _itemNames = new();

    public PrefabCatalogue(IPrefabSource source)
    {
        _source = source;
        var kits = source.Kits;
        _kits = new Dictionary<KitKey, KitFacts>(kits.Count);
        _kitNames = new Dictionary<KitKey, string>(kits.Count);
        _homes = new Dictionary<PrefabKey, List<KitPlace>>(kits.Count * 4);
        var order = new List<KitKey>(kits.Count);
        foreach (var kit in kits)
        {
            if (!_kits.TryAdd(kit.Key, new KitFacts(kit.Key, kit.Pieces)))
                continue;
            _kitNames.Add(kit.Key, kit.Name);
            order.Add(kit.Key);
            if (kit.MergeTool.TryGet(out var mergeTool))
            {
                _mergeTools.Add(kit.Key, mergeTool);
                _itemNames[mergeTool.Key] = mergeTool.Name;
            }

            for (var i = 0; i < kit.Pieces.Count; i++)
            {
                if (!KitOption.Create(i).TryGet(out var option))
                    continue;
                if (!_homes.TryGetValue(kit.Pieces[i], out var homes))
                    _homes.Add(kit.Pieces[i], homes = new List<KitPlace>(1));
                homes.Add(new KitPlace(kit.Key, option));
            }
        }

        Kits = ValueList.From(order);
        var brokenTools = source.BrokenTools;
        var tools = new List<ItemKey>(brokenTools.Count);
        foreach (var tool in brokenTools)
        {
            tools.Add(tool.Key);
            _itemNames[tool.Key] = tool.Name;
        }

        BrokenTools = ValueList.From(tools);
        _listing = new Lazy<CatalogueListing>(ListAll);
    }

    private readonly Lazy<CatalogueListing> _listing;

    /// <inheritdoc />
    public CatalogueListing Listing => _listing.Value;

    /// <inheritdoc />
    public IReadOnlyList<KitKey> Kits { get; }

    /// <inheritdoc />
    public ValueList<ItemKey> BrokenTools { get; }

    /// <summary>The prefab name of a kit's merge tool or of a broken tool.</summary>
    public Option<string> NameOf(ItemKey item) =>
        _itemNames.TryGetValue(item, out var name) ? Option.Some(name) : Option<string>.None;

    /// <inheritdoc />
    public Result<PieceFacts, Reason> Piece(PrefabKey piece)
    {
        if (_pieces.TryGetValue(piece, out var known))
            return known;
        var facts = ReadPiece(piece);
        _pieces.Add(piece, facts);
        return facts;
    }

    /// <inheritdoc />
    public Result<KitFacts, Reason> Kit(KitKey kit) =>
        _kits.TryGetValue(kit, out var facts)
            ? Result<KitFacts, Reason>.Ok(facts)
            : Result<KitFacts, Reason>.Error(CatalogueReasons.Kit(kit.Hash));

    /// <inheritdoc />
    public Result<PrefabKey, Reason> PieceOf(KitKey kit, KitOption option)
    {
        if (!_kits.TryGetValue(kit, out var facts))
            return Result<PrefabKey, Reason>.Error(CatalogueReasons.Kit(kit.Hash));
        return option.Index < facts.Pieces.Count
            ? Result<PrefabKey, Reason>.Ok(facts.Pieces[option.Index])
            : Result<PrefabKey, Reason>.Error(CatalogueReasons.Option(kit.Hash, option.Index));
    }

    /// <summary>Reads every structure prefab the game holds (each once) and reports the refusals.</summary>
    public ReadReport ReadAll()
    {
        var structures = _source.Structures;
        var refused = new List<Reason>();
        var withoutKit = new List<string>();
        var read = 0;
        foreach (var piece in structures)
        {
            if (Piece(piece).TryGetError(out var reason))
                refused.Add(reason);
            else
            {
                read++;
                if (!_homes.ContainsKey(piece))
                    withoutKit.Add(NameOf(piece).Reduce(piece.Hash.ToString()));
            }
        }

        return new ReadReport(read, ValueList.From(refused), ValueList.From(withoutKit));
    }

    /// <summary>The prefab name of a piece read so far.</summary>
    public Option<string> NameOf(PrefabKey piece) =>
        _read.TryGetValue(piece, out var data) ? Option.Some(data.Name) : Option<string>.None;

    /// <summary>The prefab name of a kit.</summary>
    public Option<string> NameOf(KitKey kit) =>
        _kitNames.TryGetValue(kit, out var name) ? Option.Some(name) : Option<string>.None;

    /// <summary>The structures read so far that no kit places, in the order read.</summary>
    public IReadOnlyList<PrefabKey> Kitless
    {
        get
        {
            var kitless = new List<PrefabKey>();
            foreach (var (key, facts) in _pieces)
                if (facts.TryGet(out var piece) && piece.Kits.Count == 0)
                    kitless.Add(key);
            return kitless;
        }
    }

    /// <summary>The prefab data of a piece read so far.</summary>
    public Option<PrefabData> DataOf(PrefabKey piece) =>
        _read.TryGetValue(piece, out var data) ? Option.Some(data) : Option<PrefabData>.None;

    private Result<PieceFacts, Reason> ReadPiece(PrefabKey piece)
    {
        if (!_source.Read(piece).TryGet(out var data))
            return Result<PieceFacts, Reason>.Error(CatalogueReasons.Piece(piece.Hash.ToString(), "no-prefab"));
        _read.Add(piece, data);
        if (!_homes.TryGetValue(piece, out var homes))
            return PieceMapper.Map(data, ValueList<KitPlace>.Empty);
        var chosen = KitHomes.Primary(homes, FirstStep(data));
        var kits = new List<KitPlace>(homes.Count) { homes[chosen] };
        for (var i = 0; i < homes.Count; i++)
            if (i != chosen)
                kits.Add(homes[i]);
        var mergeTool = _mergeTools.TryGetValue(homes[chosen].Kit, out var tool) ? Option.Some(tool.Key) : Option<ItemKey>.None;
        return PieceMapper.Map(data, ValueList.From(kits), mergeTool);
    }

    /// <summary>The items a prefab's first build state takes, <c>ToolEntry</c> then <c>ToolEntry2</c>.</summary>
    public static ValueList<ItemKey> FirstStep(PrefabData data)
    {
        if (data.States.Count == 0)
            return ValueList<ItemKey>.Empty;
        var first = data.States[0];
        var items = new List<ItemKey>(2);
        if (first.Entry.TryGet(out var entry))
            items.Add(entry.Item.Key);
        if (first.Entry2.TryGet(out var entry2))
            items.Add(entry2.Item.Key);
        return ValueList.From(items);
    }

    private CatalogueListing ListAll()
    {
        var kits = new List<ListedKit>(_source.Kits.Count);
        foreach (var kit in _source.Kits)
            kits.Add(new ListedKit(kit.Key, kit.Name, kit.DisplayName, kit.Kind, kit.Pieces));
        return new CatalogueListing(ValueList.From(_source.Listed), ValueList.From(kits)) { Items = ValueList.From(_source.Items) };
    }
}
