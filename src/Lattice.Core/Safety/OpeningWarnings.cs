using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Openings;
using Lattice.Core.Ports;
using Lattice.Core.Support;

namespace Lattice.Core.Safety;

/// <summary>
/// Runs through doors and anything on a window, on the one keep-out definition route planning keeps clear
/// (<see cref="OpeningKeepOuts"/>, over the planned world): run-through-door for a planned cable, pipe or chute piece
/// (two or more ends, none of them a device's port) with a cell in a door's keep-out; on-window for any planned piece
/// that is not itself a door or window with a cell in a window's keep-out. One warning per door or window, naming
/// every ghost in its keep-out. The cells looked at are the piece's spot cells: small cells for small pieces, the large
/// cell for large ones.
/// </summary>
internal static class OpeningWarnings
{
    public static void Visit(SafetyScan scan, OpeningKeepOuts keepOuts, IOpeningFacts openings, Ghost ghost)
    {
        if (!SafetyScan.Put(ghost).TryGet(out var placement) || !scan.Shape(placement).TryGet(out var shape))
            return;
        var own = SupportWorld.IdOf(ghost.Id);
        var run = IsRun(shape.Piece);
        var opening = openings.Of(placement.Piece).IsSome;
        foreach (var spot in shape.Spots)
        {
            if (!keepOuts.At(spot.Cell).TryGet(out var hit) || hit.Opening.Id == own)
                continue;
            if (hit.Kind == OpeningKind.Door && run)
                scan.Warnings.Add(SafetyCodes.RunThroughDoor, "door " + SafetyScan.Text(hit.Opening.Id.Value),
                    new Dictionary<string, string> { ["door"] = SafetyScan.Text(hit.Opening.Id.Value) }, ghost.Id);
            else if (hit.Kind == OpeningKind.Window && !opening)
                scan.Warnings.Add(SafetyCodes.OnWindow, "window " + SafetyScan.Text(hit.Opening.Id.Value),
                    new Dictionary<string, string> { ["window"] = SafetyScan.Text(hit.Opening.Id.Value) }, ghost.Id);
        }
    }

    /// <summary>A run piece: two or more ends and none with a device role (cables, pipes, chutes; a modded run the same).</summary>
    private static bool IsRun(PieceFacts piece)
    {
        if (piece.Ends.Count < 2)
            return false;
        foreach (var end in piece.Ends)
            if (end.Role.IsSome)
                return false;
        return true;
    }
}
