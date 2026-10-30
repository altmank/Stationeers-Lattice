using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.TestKit.Catalogue;

namespace Lattice.Tests.Catalogue;

/// <summary>Prefab data as the game bridge reads it, built by name for tests.</summary>
internal static class Prefabs
{
    public static ItemRef Item(string name) => new(name, PrefabHash.Item(name));

    public static EntryItem Stack(string name, int quantity) => new(Item(name), quantity, IsStack: true);

    public static EntryItem Tool(string name, int quantity = 0) => new(Item(name), quantity, IsStack: false);

    public static StateData State(EntryItem? entry = null, EntryItem? entry2 = null, string? exit = null, bool air = false) =>
        new(Optional(entry), Optional(entry2), exit is null ? Option<ItemRef>.None : Option.Some(Item(exit)), air);

    public static EndData End(Vector position, Vector forward, NetworkKinds kinds) => new(position, forward, kinds, kinds.ToString());

    public static PrefabData Piece(
        string name,
        double grid = 0.5,
        RunKind run = RunKind.Other,
        StateData[]? states = null,
        EndData[]? ends = null,
        TypeData[]? types = null,
        double? cableWatts = null,
        double? pipeKilopascals = null,
        double maxPressureDelta = -1,
        bool rocketOnly = false) =>
        new(
            name,
            PrefabHash.Piece(name),
            grid,
            ValueList.From(types ?? new[] { Type("Assets.Scripts.Objects.Structure", declares: true) }),
            run,
            ValueList.From(states ?? new[] { State(Stack("ItemKit" + name, 1), exit: "ItemWrench") }),
            ValueList.From(ends ?? new EndData[0]),
            cableWatts is { } w ? Option.Some(w) : Option<double>.None,
            pipeKilopascals is { } p ? Option.Some(p) : Option<double>.None,
            maxPressureDelta,
            rocketOnly);

    public static TypeData Type(string fullName, bool declares = false) => new(fullName, declares);

    public static KitData Kit(string name, params PrefabData[] pieces)
    {
        var keys = new PrefabKey[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
            keys[i] = pieces[i].Key;
        return new KitData(name, PrefabHash.Kit(name), ValueList.From(keys));
    }

    public static T Ok<T>(Result<T, Reason> result) where T : notnull =>
        result.TryGet(out var value) ? value : throw new Xunit.Sdk.XunitException($"Expected Ok, got {result}.");

    public static T Ok<T>(Result<T, Lattice.TestKit.FileError> result) where T : notnull =>
        result.TryGet(out var value) ? value : throw new Xunit.Sdk.XunitException($"Expected Ok, got {result}.");

    public static Reason Error<T>(Result<T, Reason> result) where T : notnull =>
        result.TryGetError(out var error) ? error : throw new Xunit.Sdk.XunitException($"Expected an error, got {result}.");

    private static Option<EntryItem> Optional(EntryItem? item) => item is null ? Option<EntryItem>.None : Option.Some(item);
}

/// <summary>An <see cref="IPrefabSource"/> over a fixed set, counting how often each prefab is read.</summary>
internal sealed class CountingSource(
    KitData[] kits,
    PrefabData[] pieces,
    ItemRef[]? brokenTools = null,
    Dictionary<PrefabKey, string>? displayNames = null,
    ListedItem[]? items = null,
    SprayCanData[]? sprayCans = null,
    ItemRef[]? labellers = null) : IPrefabSource
{
    private readonly Dictionary<PrefabKey, int> _reads = [];

    public IReadOnlyList<KitData> Kits { get; } = kits;

    public IReadOnlyList<ItemRef> BrokenTools { get; } = brokenTools ?? [];

    public IReadOnlyList<SprayCanData> SprayCans { get; } = sprayCans ?? [];

    public IReadOnlyList<ItemRef> Labellers { get; } = labellers ?? [];

    public IReadOnlyList<PrefabKey> Structures { get; } = Keys(pieces);

    public IReadOnlyList<ListedPiece> Listed { get; } = Listing(pieces, displayNames);

    public IReadOnlyList<ListedItem> Items { get; } = items ?? [];

    public Option<PrefabData> Read(PrefabKey piece)
    {
        _reads[piece] = Reads(piece) + 1;
        foreach (var prefab in pieces)
            if (prefab.Key == piece)
                return Option.Some(prefab);
        return Option<PrefabData>.None;
    }

    public int Reads(PrefabKey piece) => _reads.TryGetValue(piece, out var n) ? n : 0;

    private static List<ListedPiece> Listing(PrefabData[] pieces, Dictionary<PrefabKey, string>? displayNames)
    {
        var listed = new List<ListedPiece>(pieces.Length);
        foreach (var prefab in pieces)
            if (KitHomes.GridOf(prefab.GridMetres).TryGet(out var grid))
                listed.Add(new ListedPiece(prefab.Key, prefab.Name, displayNames?.GetValueOrDefault(prefab.Key) ?? prefab.Name, grid, PrefabCatalogue.FirstStep(prefab))
                {
                    Snap = prefab.Snap.Reduce(PieceSnap.Of(grid)),
                });
        return listed;
    }

    private static PrefabKey[] Keys(PrefabData[] pieces)
    {
        var keys = new PrefabKey[pieces.Length];
        for (var i = 0; i < pieces.Length; i++)
            keys[i] = pieces[i].Key;
        return keys;
    }
}
