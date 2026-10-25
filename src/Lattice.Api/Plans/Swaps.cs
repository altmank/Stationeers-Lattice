namespace Lattice.Api;

/// <summary>
/// What a built structure can become in place. Main thread. Lattice implements this; consumers call it. A minor may
/// add members, so consumer fakes of it are not covered by compatibility.
/// </summary>
public interface ISwaps
{
    /// <summary>
    /// Every piece a kit places that the structure can become in place (same spots, same ends with the same network
    /// kinds, under any turn; its own turn kept when it fits), in kit order then option order, each with its face load
    /// now. Refused <see cref="ReasonCodes.NotFound"/> for a structure that is gone, <see cref="ReasonCodes.UnknownPiece"/>
    /// for a piece the catalogue does not hold. The first call reads the catalogue once; after that linear in the
    /// pieces of the structure's size.
    /// </summary>
    Result<ValueList<SwapOption>, Reason> OptionsFor(StructureRef structure);
}

/// <summary>One thing a structure can become in place.</summary>
/// <param name="New">The new piece and turn, at the structure's cell.</param>
/// <param name="Face">Its load when the new piece closes a face and has a face rating; none otherwise.</param>
public sealed record SwapOption(Placement New, Option<FaceLoad> Face);

/// <summary>
/// A face piece's load: the face between <paramref name="Cell"/> and its neighbour on the positive side of
/// <paramref name="Normal"/> (as the board keys faces), the piece's rating and the pressure difference across that face now.
/// Over the rating the game damages the piece each tick.
/// </summary>
/// <param name="Cell">The cell on the face's negative side.</param>
/// <param name="Normal">The axis the face is square to, as in <see cref="MirrorPlane.Normal"/>.</param>
/// <param name="Rating">The piece's face rating.</param>
/// <param name="Difference">The pressure difference across the face now, never negative.</param>
public sealed record FaceLoad(Cell Cell, Axis Normal, Kilopascals Rating, Kilopascals Difference)
{
    /// <summary>Whether the difference now is above the rating.</summary>
    public bool Over => Difference.Value > Rating.Value;
}
