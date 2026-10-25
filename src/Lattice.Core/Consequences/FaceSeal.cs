using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Consequences;

/// <summary>A structure as air sees it: its id and whether it blocks air (<c>!Structure.CanAirPass</c>).</summary>
internal readonly record struct AirBlocker(ReferenceId Id, bool BlocksAir);

/// <summary>
/// Whether a face between two 2 m cells still keeps air apart once a set of structures is gone. The game lets air
/// through a face only when nothing on the face blocks it and neither cell's own structure does
/// (<c>GridController.CanAirPass</c> reads the cell's structure and every face structure;
/// <c>Atmosphere.ReactWithStructures</c> skips a neighbour whose cell <c>IsBlocked</c>): a wall plate on the face of a
/// finished frame opens nothing, and two plates back to back on one face open it only when both go. The removal is
/// judged as a whole: every structure removed counts as gone at once.
/// </summary>
internal static class FaceSeal
{
    /// <summary>Whether the face stays sealed. Linear in the face's structures.</summary>
    /// <param name="onFace">Every structure registered on the face.</param>
    /// <param name="cellA">The structure filling the cell on one side, if any.</param>
    /// <param name="cellB">The structure filling the cell on the other side, if any.</param>
    /// <param name="removed">Every structure removed.</param>
    public static bool Sealed(IEnumerable<AirBlocker> onFace, Option<AirBlocker> cellA, Option<AirBlocker> cellB, ICollection<ReferenceId> removed)
    {
        foreach (var blocker in onFace)
            if (Stays(blocker, removed))
                return true;
        return (cellA.TryGet(out var a) && Stays(a, removed)) || (cellB.TryGet(out var b) && Stays(b, removed));
    }

    private static bool Stays(AirBlocker blocker, ICollection<ReferenceId> removed) =>
        blocker.BlocksAir && !removed.Contains(blocker.Id);
}
