using Lattice.Api;

namespace Lattice.Core.Board;

/// <summary>What a planned piece can become in place: the swaps module's answer, asked by the board for <see cref="GhostEdit.Swap"/>.</summary>
internal interface IPieceSwaps
{
    /// <summary>
    /// The placement a planned piece becomes under <paramref name="to"/>, at its cell, or why nothing fits. The same
    /// placement back means it already is that piece in that turn. <paramref name="old"/>: a swap ghost's structure,
    /// which the piece may not become.
    /// </summary>
    Result<Placement, Reason> Target(Placement placement, Option<StructureRef> old, SwapTo to);

    /// <summary><see cref="ReasonCodes.UnknownKind"/> for a kind of swap this version does not know; none otherwise. Constant cost.</summary>
    Option<Reason> Unknown(SwapTo to);
}
