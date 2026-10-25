namespace Lattice.Api;

/// <summary>
/// The pieces and kits the game holds, read by kind: every structure prefab and every kit, modded ones found by type
/// like the game's own. Built once at world load on every seat; every peer holds the same prefabs, so every seat reads
/// the same view except <see cref="PieceEntry.Name"/>. Fixed for the world except <see cref="FamilyOf"/>. Main thread;
/// every member is constant cost and allocates nothing once a piece's family has been read. Lattice implements this;
/// consumers call it. A minor may add members, so consumer fakes of it are not covered by compatibility.
/// Added in contract 0.3: read <see cref="IGhostSystem.Version"/> first.
/// </summary>
public interface IPieceCatalogue
{
    /// <summary>Every structure prefab, kitless ones included, ordered by prefab name (ordinal). Added in contract 0.3.</summary>
    ValueList<PieceEntry> Pieces { get; }

    /// <summary>Every kit, ordered by prefab name (ordinal). Added in contract 0.3.</summary>
    ValueList<KitEntry> Kits { get; }

    /// <summary>A piece. Refused <see cref="ReasonCodes.UnknownPiece"/> for one the game does not hold. Added in contract 0.3.</summary>
    Result<PieceEntry, Reason> Piece(PrefabKey piece);

    /// <summary>
    /// A kit. Refused <see cref="ReasonCodes.UnknownPiece"/> (fact <c>kit</c>) for one the game does not hold. Added in
    /// contract 0.3.
    /// </summary>
    Result<KitEntry, Reason> Kit(KitKey kit);

    /// <summary>
    /// The piece a kit places at an option. Refused as <see cref="Kit"/> for an unknown kit, and
    /// <see cref="ReasonCodes.InvalidValue"/> for an option past the kit's last. Added in contract 0.3.
    /// </summary>
    Result<PrefabKey, Reason> PieceOf(KitKey kit, KitOption option);

    /// <summary>
    /// The kit and option a piece's first build step uses: the game's own choice when several kits place it. Refused
    /// <see cref="ReasonCodes.UnknownPiece"/> for an unknown piece, and <see cref="ReasonCodes.NotFound"/> (fact
    /// <c>problem=no-kit</c>) for a piece no kit places. Added in contract 0.3.
    /// </summary>
    Result<KitPlace, Reason> KitOf(PrefabKey piece);

    /// <summary>
    /// The family a piece is drawn and filtered by, the same one style rules read: the one a mod registered
    /// (<see cref="CatalogueAddition.InFamily"/>) while its handle lives, else the one found by type. The one answer that
    /// changes during a world. Refused <see cref="ReasonCodes.UnknownPiece"/> for an unknown piece. The first ask of a
    /// piece reads its prefab once. Added in contract 0.3.
    /// </summary>
    Result<PieceFamily, Reason> FamilyOf(PrefabKey piece);
}

/// <summary>One structure prefab. Added in contract 0.3.</summary>
/// <param name="Key">The piece.</param>
/// <param name="PrefabName">The game's code name, which <paramref name="Key"/> hashes; the same on every seat.</param>
/// <param name="Name">
/// Display text in the local player's language, read at world load (a language change shows on the next load); the
/// prefab name where the game has none. It may differ between peers and is never an identity.
/// </param>
/// <param name="Grid">The grid it sits on.</param>
/// <param name="Kits">Every kit that places it, <see cref="IPieceCatalogue.KitOf"/>'s answer first, the rest in kit order; empty when no kit places it.</param>
public sealed record PieceEntry(PrefabKey Key, string PrefabName, string Name, PieceGrid Grid, ValueList<KitPlace> Kits);

/// <summary>One kit. Added in contract 0.3.</summary>
/// <param name="Key">The kit.</param>
/// <param name="PrefabName">The game's code name, which <paramref name="Key"/> hashes; the same on every seat.</param>
/// <param name="Name">Display text in the local player's language, as <see cref="PieceEntry.Name"/>.</param>
/// <param name="Options">The piece at each <see cref="KitOption"/> index, in the game's scroll order; one for a single-piece kit.</param>
public sealed record KitEntry(KitKey Key, string PrefabName, string Name, ValueList<PrefabKey> Options);

/// <summary>A kit that places a piece, and the piece's option in it. Added in contract 0.3.</summary>
/// <param name="Kit">The kit.</param>
/// <param name="Option">The piece's option in the kit.</param>
public readonly record struct KitPlace(KitKey Kit, KitOption Option);

/// <summary>The game's two grids. Values are fixed. Added in contract 0.3.</summary>
public enum PieceGrid
{
    /// <summary>The small grid (0.5 m cells): cables, pipes, chutes, small devices.</summary>
    Small = 1,

    /// <summary>The large grid (2 m cells): frames, walls, large devices.</summary>
    Large = 2,
}
