using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Catalogue;

/// <summary>
/// The game's prefabs as the catalogue reads them: every kit and every structure prefab the game holds at world
/// load, modded ones included. Reading a prefab may cost; the catalogue asks for each one once.
/// </summary>
internal interface IPrefabSource
{
    /// <summary>Every kit prefab, in the game's prefab order.</summary>
    IReadOnlyList<KitData> Kits { get; }

    /// <summary>Every structure prefab, in the game's prefab order.</summary>
    IReadOnlyList<PrefabKey> Structures { get; }

    /// <summary>
    /// Every structure prefab of <see cref="Structures"/>, in its order, as the catalogue view lists it: names, grid and
    /// first-step items only, with none of <see cref="Read"/>'s cost. A prefab whose grid is not one of the game's two is
    /// left out. Read once, on first use.
    /// </summary>
    IReadOnlyList<ListedPiece> Listed { get; }

    /// <summary>
    /// Every item prefab that takes a broken structure apart, in the game's prefab order: any <c>IConstructor</c> tool
    /// (decompile <c>Assets.Scripts.Objects/Structure.cs</c> <c>AttackWith</c>),
    /// a modded one included.
    /// </summary>
    IReadOnlyList<ItemRef> BrokenTools { get; }

    /// <summary>
    /// Every spray can prefab (<c>SprayCan</c>, a modded one included), in the game's prefab order, with the colour it
    /// sprays and what an act takes from it.
    /// </summary>
    IReadOnlyList<SprayCanData> SprayCans { get; }

    /// <summary>Every labeller prefab (<c>Labeller</c>, a modded one included), in the game's prefab order.</summary>
    IReadOnlyList<ItemRef> Labellers { get; }

    /// <summary>
    /// Every item prefab, kits and tools included, in the game's prefab order, with its display name as
    /// <see cref="Listed"/> gives a piece's. Read once, on first use.
    /// </summary>
    IReadOnlyList<ListedItem> Items { get; }

    /// <summary>The prefab's data, or none when the game holds no structure prefab with that hash.</summary>
    Option<PrefabData> Read(PrefabKey piece);
}
