using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Catalogue;

/// <summary>Which of the game's kit types a kit is, found by type (a modded subclass is its base's kind).</summary>
internal enum KitKind
{
    /// <summary>A <c>Constructor</c> with one <c>BuildStructure</c>.</summary>
    Single = 1,

    /// <summary>A <c>MultiConstructor</c>: its <c>Constructables</c>, scrolled through by option index.</summary>
    Multi = 2,

    /// <summary>A <c>MultiMergeConstructor</c>: a multi kit that also merges into the piece standing in its cell.</summary>
    Merge = 3,
}

/// <summary>
/// A structure prefab as the catalogue lists it, without reading its build steps, ends or footprint: names, grid,
/// and the items its first build step takes (<c>ToolEntry</c>, <c>ToolEntry2</c>), from which the kit the game uses
/// for it is chosen.
/// </summary>
internal sealed record ListedPiece(PrefabKey Key, string PrefabName, string Name, PieceGrid Grid, ValueList<ItemKey> FirstStep);

/// <summary>A kit prefab as the catalogue lists it: names, kind and the piece at each option, in the game's scroll order.</summary>
internal sealed record ListedKit(KitKey Key, string PrefabName, string Name, KitKind Kind, ValueList<PrefabKey> Options);

/// <summary>Every piece and kit the catalogue holds, in the source's order (the game's prefab order or the file's).</summary>
internal sealed record CatalogueListing(ValueList<ListedPiece> Pieces, ValueList<ListedKit> Kits)
{
    public static CatalogueListing Empty { get; } = new(ValueList<ListedPiece>.Empty, ValueList<ListedKit>.Empty);
}

/// <summary>The rules every catalogue shares for the kits that place a piece and for grids.</summary>
internal static class KitHomes
{
    /// <summary>
    /// The index in <paramref name="homes"/> (kits in source order) of the kit the game places the piece with: the first
    /// one its first build step takes (decompile <c>Assets.Scripts.Objects/Structure.cs:1278</c> reads
    /// <c>BuildStates[0].Tool.ToolEntry</c>, then <c>ToolEntry2</c>), else the first. 0 for an empty list.
    /// </summary>
    public static int Primary(IReadOnlyList<KitPlace> homes, IReadOnlyList<ItemKey> firstStep)
    {
        for (var i = 0; i < homes.Count; i++)
            for (var j = 0; j < firstStep.Count; j++)
                if (firstStep[j].Hash == homes[i].Kit.Hash)
                    return i;
        return 0;
    }

    /// <summary>The grid of a prefab's <c>GridSize</c> in metres; none for a size the game does not use for pieces.</summary>
    public static Option<PieceGrid> GridOf(double metres) => metres switch
    {
        2.0 => Option.Some(PieceGrid.Large),
        0.5 => Option.Some(PieceGrid.Small),
        _ => Option<PieceGrid>.None,
    };

    /// <summary>The contract's grid of a catalogue grid.</summary>
    public static PieceGrid ToPiece(this GridSize grid) => grid == GridSize.Large ? PieceGrid.Large : PieceGrid.Small;
}
