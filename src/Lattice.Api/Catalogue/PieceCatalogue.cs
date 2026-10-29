namespace Lattice.Api;

/// <summary>
/// The pieces and kits the game holds, read by kind: every structure prefab and every kit, modded ones found by type
/// like the game's own. Built once at world load on every seat; every peer holds the same prefabs, so every seat reads
/// the same view except <see cref="PieceEntry.Name"/>. Fixed for the world except <see cref="FamilyOf"/>. Main thread;
/// every member but <see cref="PortsAt"/> is constant cost and allocates nothing once a piece's family has been read.
/// Lattice implements this; consumers call it. A minor may add members: to fake it in a test, inherit
/// <c>Lattice.Testing.Stubs.StubPieceCatalogue</c>, which gains every added member in the same release. Added in
/// contract 0.3: read <see cref="IGhostSystem.Version"/> first.
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
    /// The family a piece is drawn and filtered by: the top of its chain (<see cref="FamiliesOf"/>), so a window answers
    /// <see cref="PieceFamilies.Wall"/>. The chain starts at the one a mod registered
    /// (<see cref="CatalogueAddition.InFamily"/>) while its handle lives, else the one found by type. The one answer that
    /// changes during a world. Refused <see cref="ReasonCodes.UnknownPiece"/> for an unknown piece. The first ask of a
    /// piece reads its prefab once. Added in contract 0.3.
    /// </summary>
    Result<PieceFamily, Reason> FamilyOf(PrefabKey piece);

    /// <summary>
    /// A piece's family and every family above it, narrowest first: <c>[window, wall]</c> for a window,
    /// <c>[frame]</c> for a frame. Style rules match any of them. Refused <see cref="ReasonCodes.UnknownPiece"/> for an
    /// unknown piece. Allocates nothing. Added in contract 0.7.
    /// </summary>
    Result<ValueList<PieceFamily>, Reason> FamiliesOf(PrefabKey piece);

    /// <summary>
    /// The family a family lies under: <see cref="PieceFamilies.Wall"/> for <see cref="PieceFamilies.Window"/>, none for
    /// Lattice's other families and for a mod's own. Allocates nothing. Added in contract 0.7.
    /// </summary>
    Option<PieceFamily> ParentOf(PieceFamily family);

    /// <summary>
    /// The piece's connection ports placed at the cell and turn, in the game's <c>OpenEnds</c> order: for a ghost (its
    /// <see cref="Intent.Build.Target"/> or <see cref="Intent.Swap.New"/>), a draft, or a built structure
    /// (<c>new Placement(info.Ref.Piece, info.Ref.At, info.Turn)</c>) alike. Empty for a piece with none. Refused
    /// <see cref="ReasonCodes.UnknownPiece"/> for an unknown piece. Main thread; linear in the piece's ports; allocates
    /// the returned list and its ports. Added in contract 0.8.
    /// </summary>
    Result<ValueList<Port>, Reason> PortsAt(Placement placement);

    /// <summary>
    /// The axis a face piece's face is square to under the placement: the axis <see cref="StyleMatch.Facing"/> matches,
    /// worked out from the piece's own face points and turn, so a modded face piece answers as the game places it.
    /// <see cref="Axis.Y"/> for a floor or a wall laid flat. None for a piece that takes a cell centre or small-grid
    /// slots (a frame, a cable, a device). Lay a line of face pieces along an axis square to it, a rectangle over the two
    /// axes square to it, and layers along it. Refused <see cref="ReasonCodes.UnknownPiece"/> for an unknown piece. Main
    /// thread; constant cost; allocates nothing once a piece, turn and place within its large cell have been asked.
    /// Added in contract 0.9.
    /// </summary>
    Result<Option<Axis>, Reason> FacingOf(Placement placement);

    /// <summary>
    /// The cells the piece fills at the placement, turn applied, in its grid: each footprint cell once, in the
    /// footprint's order (the placement's own cell alone for a piece with no footprint); for a piece the game puts on a
    /// face, the cells its face takes. A piece's body need not sit round its anchor: the small insulated in-line liquid
    /// tank fills cells from 10 decimetres below its cell to 5 above, so the lowest cell's <see cref="Cell.Y"/> says how
    /// far a piece hangs below the cell it is placed at, and what must stand beneath it. Refused
    /// <see cref="ReasonCodes.UnknownPiece"/> for an unknown piece. Main thread; linear in the piece's footprint;
    /// allocates the returned list. Added in contract 0.18.
    /// </summary>
    Result<ValueList<Cell>, Reason> CellsAt(Placement placement);

    /// <summary>
    /// An item's display name in the local player's language, read at world load as <see cref="PieceEntry.Name"/> (the
    /// prefab name where the game has no localised one), for every item prefab the game holds: kits, tools, ingots and
    /// every other item a <see cref="Bill"/> or a <see cref="ReasonCodes.NeedsItems"/> fact can name. A kit's is its
    /// <see cref="KitEntry.Name"/>. Refused <see cref="ReasonCodes.NotFound"/> (fact <c>item</c>) for an item the game
    /// does not hold. Main thread; constant cost; allocates nothing. Added in contract 0.19.
    /// </summary>
    Result<string, Reason> ItemName(ItemKey item);
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
public sealed record PieceEntry(PrefabKey Key, string PrefabName, string Name, PieceGrid Grid, ValueList<KitPlace> Kits)
{
    /// <summary>
    /// How the game snaps the piece's position on each world axis, read from its prefab at load: the grid's own
    /// (<see cref="PieceSnap.Of"/>, the default for an entry built without one) for most pieces, each axis its own for
    /// an axis-independent piece such as a rocket engine. Place the piece where <see cref="PieceSnap.Holds"/> answers
    /// true. Added in contract 0.18.
    /// </summary>
    public PieceSnap Snap { get; init; } = PieceSnap.Of(Grid);
}

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
