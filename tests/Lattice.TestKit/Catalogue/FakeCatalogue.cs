using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;

namespace Lattice.TestKit.Catalogue;

/// <summary>A piece as the catalogue file names it: its prefab name, the names of its kits (in the order of its facts' kits), its facts and its item names.</summary>
internal sealed record NamedPiece(string Name, ValueList<string> KitNames, PieceFacts Facts, ValueMap<ItemKey, string> ItemNames)
{
    /// <summary>Its display name; the prefab name unless the file gives one.</summary>
    public string DisplayName
    {
        get => _displayName ?? Name;
        init => _displayName = value;
    }

    private readonly string? _displayName;
}

/// <summary>A kit line of the catalogue file: the kit's prefab name, kind, display name and every option's prefab name in order.</summary>
internal sealed record KitLine(string Name, KitKind Kind, string DisplayName, ValueList<string> Options);

/// <summary>An item line of the catalogue file: the item's prefab name and display name.</summary>
internal sealed record ItemLine(string Name, string DisplayName);

/// <summary>
/// <see cref="ICatalogue"/> over pieces read from a catalogue data file (<see cref="CatalogueFile"/>). Knows the
/// names behind the hashes so it can write itself back and print readable failures.
/// </summary>
internal sealed class FakeCatalogue : ICatalogue
{
    private readonly Dictionary<PrefabKey, NamedPiece> _pieces;
    private readonly Dictionary<KitKey, KitFacts> _kits;
    private readonly Dictionary<KitKey, string> _kitNames;

    public FakeCatalogue(
        IReadOnlyList<NamedPiece> pieces,
        IReadOnlyList<string>? brokenTools = null,
        IReadOnlyList<KitLine>? kitLines = null,
        IReadOnlyList<ItemLine>? itemLines = null)
    {
        KitLines = ValueList.From(kitLines ?? []);
        ItemLines = ValueList.From(itemLines ?? []);
        BrokenToolNames = ValueList.From(brokenTools ?? []);
        var tools = new List<ItemKey>(BrokenToolNames.Count);
        foreach (var name in BrokenToolNames)
            tools.Add(PrefabHash.Item(name));
        BrokenTools = ValueList.From(tools);
        _pieces = new Dictionary<PrefabKey, NamedPiece>(pieces.Count);
        _kitNames = new Dictionary<KitKey, string>();
        var byKit = new Dictionary<KitKey, SortedList<int, PrefabKey>>();
        var kitOrder = new List<KitKey>();
        foreach (var piece in pieces)
        {
            _pieces.Add(piece.Facts.Key, piece);
            for (var i = 0; i < piece.Facts.Kits.Count; i++)
            {
                var place = piece.Facts.Kits[i];
                if (!byKit.TryGetValue(place.Kit, out var options))
                {
                    byKit.Add(place.Kit, options = new SortedList<int, PrefabKey>());
                    _kitNames.Add(place.Kit, piece.KitNames[i]);
                    kitOrder.Add(place.Kit);
                }

                options.Add(place.Option.Index, piece.Facts.Key);
            }
        }

        _kits = new Dictionary<KitKey, KitFacts>(byKit.Count);
        foreach (var (kit, options) in byKit)
            _kits.Add(kit, new KitFacts(kit, ValueList.From(options.Values)));
        Kits = ValueList.From(kitOrder);
        Pieces = pieces;
        _listing = new System.Lazy<CatalogueListing>(ListAll);
    }

    private readonly System.Lazy<CatalogueListing> _listing;

    /// <summary>
    /// The pieces in file order with their display names; the kits of the kit lines in file order, then those only piece
    /// lines name in order of first mention, each by option index; the items of the item lines in file order.
    /// </summary>
    public CatalogueListing Listing => _listing.Value;

    /// <summary>The hand-written catalogue shipped with the test kit (about 20 pieces).</summary>
    public static FakeCatalogue Hand()
    {
        using var stream = typeof(FakeCatalogue).Assembly.GetManifestResourceStream("Lattice.TestKit.hand-catalogue.lcat")!;
        using var reader = new StreamReader(stream);
        return TestKitIds.MustRead(CatalogueFile.Read(reader, "hand-catalogue.lcat"));
    }

    /// <summary>Every piece, in file order.</summary>
    public IReadOnlyList<NamedPiece> Pieces { get; }

    /// <summary>The item names of <see cref="BrokenTools"/>, in file order.</summary>
    public ValueList<string> BrokenToolNames { get; }

    public ValueList<ItemKey> BrokenTools { get; }

    /// <summary>The same pieces with other broken tools (none when empty).</summary>
    public FakeCatalogue WithBrokenTools(params string[] tools) => new(Pieces, tools, KitLines, ItemLines);

    /// <summary>The file's kit lines, in file order.</summary>
    public ValueList<KitLine> KitLines { get; }

    /// <summary>The file's item lines, in file order.</summary>
    public ValueList<ItemLine> ItemLines { get; }

    /// <summary>
    /// The first kit without a kit line whose options do not run from 0 without a gap, as text; none when every kit's
    /// options do. A kit with a gap lists <c>default</c> at each missing option in <see cref="Listing"/>.
    /// </summary>
    public Option<string> Gap
    {
        get
        {
            foreach (var kit in Kits)
            {
                if (KitLineOf(kit) is not null)
                    continue;
                var options = Indexed(kit);
                for (var i = 0; i < options.Length; i++)
                    if (options[i] == default)
                        return Option.Some($"{_kitNames[kit]} has no option {i}");
            }

            return Option<string>.None;
        }
    }

    public IReadOnlyList<KitKey> Kits { get; }

    public Result<PieceFacts, Reason> Piece(PrefabKey piece) =>
        _pieces.TryGetValue(piece, out var named)
            ? Result<PieceFacts, Reason>.Ok(named.Facts)
            : Result<PieceFacts, Reason>.Error(Unknown("piece", piece.Hash));

    public Result<KitFacts, Reason> Kit(KitKey kit) =>
        _kits.TryGetValue(kit, out var facts)
            ? Result<KitFacts, Reason>.Ok(facts)
            : Result<KitFacts, Reason>.Error(Unknown("kit", kit.Hash));

    public Result<PrefabKey, Reason> PieceOf(KitKey kit, KitOption option)
    {
        if (!_kits.TryGetValue(kit, out var facts))
            return Result<PrefabKey, Reason>.Error(Unknown("kit", kit.Hash));
        foreach (var key in facts.Pieces)
            if (System.Linq.Enumerable.Contains(_pieces[key].Facts.Kits, new KitPlace(kit, option)))
                return Result<PrefabKey, Reason>.Ok(key);
        return Result<PrefabKey, Reason>.Error(Unknown("option", option.Index));
    }

    /// <summary>A piece by its prefab name; a bug in the test when absent.</summary>
    public PieceFacts Named(string prefabName) =>
        Piece(PrefabHash.Piece(prefabName)).TryGet(out var facts)
            ? facts
            : throw new KeyNotFoundException($"{prefabName} is not in the catalogue.");

    /// <summary>The prefab name of a piece, or its hash when it is not in the catalogue.</summary>
    public string NameOf(PrefabKey piece) => _pieces.TryGetValue(piece, out var named) ? named.Name : piece.Hash.ToString();

    public string NameOf(KitKey kit) => _kitNames.TryGetValue(kit, out var name) ? name : kit.Hash.ToString();

    private CatalogueListing ListAll()
    {
        var pieces = new List<ListedPiece>(Pieces.Count);
        foreach (var piece in Pieces)
        {
            var firstStep = piece.Facts.Kits.Count > 0
                ? ValueList.Of(new ItemKey(piece.Facts.Kits[0].Kit.Hash))
                : ValueList<ItemKey>.Empty;
            pieces.Add(new ListedPiece(piece.Facts.Key, piece.Name, piece.DisplayName, piece.Facts.Grid.ToPiece(), firstStep) { Snap = piece.Facts.Snap });
        }

        var kits = new List<ListedKit>(Kits.Count + KitLines.Count);
        var lined = new HashSet<KitKey>();
        foreach (var line in KitLines)
        {
            var listed = new List<PrefabKey>(line.Options.Count);
            foreach (var option in line.Options)
                listed.Add(PrefabHash.Piece(option));
            var key = PrefabHash.Kit(line.Name);
            lined.Add(key);
            kits.Add(new ListedKit(key, line.Name, line.DisplayName, line.Kind, ValueList.From(listed)));
        }

        foreach (var kit in Kits)
        {
            if (lined.Contains(kit))
                continue;
            var options = Indexed(kit);
            kits.Add(new ListedKit(kit, _kitNames[kit], _kitNames[kit], options.Length == 1 ? KitKind.Single : KitKind.Multi, ValueList.From(options)));
        }

        var items = new List<ListedItem>(ItemLines.Count);
        foreach (var line in ItemLines)
            items.Add(new ListedItem(PrefabHash.Item(line.Name), line.Name, line.DisplayName));
        return new CatalogueListing(ValueList.From(pieces), ValueList.From(kits)) { Items = ValueList.From(items) };
    }

    private KitLine? KitLineOf(KitKey kit)
    {
        foreach (var line in KitLines)
            if (PrefabHash.Kit(line.Name) == kit)
                return line;
        return null;
    }

    // The kit's pieces by option index, 0 to the last option the piece lines give; default where none does.
    private PrefabKey[] Indexed(KitKey kit)
    {
        var last = -1;
        foreach (var piece in Pieces)
            foreach (var place in piece.Facts.Kits)
                if (place.Kit == kit)
                    last = System.Math.Max(last, place.Option.Index);
        var options = new PrefabKey[last + 1];
        foreach (var piece in Pieces)
            foreach (var place in piece.Facts.Kits)
                if (place.Kit == kit)
                    options[place.Option.Index] = piece.Facts.Key;
        return options;
    }

    private static Reason Unknown(string what, int value) =>
        Reason.Of(ReasonCodes.UnknownPiece, TestKitIds.Catalogue, ValueMap<string, string>.Empty.With(what, value.ToString()));
}
