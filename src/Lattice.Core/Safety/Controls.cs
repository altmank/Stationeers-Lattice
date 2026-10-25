using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.Safety;

/// <summary>
/// Unreachable controls: a device's control side (<see cref="ISafetyFacts.ControlFace"/>, turned as it stands) must
/// keep the cell in front of it clear. In the planned world that cell is blocked by a piece taking the centre of its
/// large cell (a frame, a large machine), by a face piece (a wall, a window) between the device's large cell and that
/// one, or, for a small device, by another small device in that small cell. A device counts when it or a blocker is one
/// of the plan's ghosts: the plan's devices, and the devices standing in the cells next to each of the plan's pieces
/// (found in the cell they stand in). One warning per device, naming the plan's ghosts among it and its blockers.
/// </summary>
internal static class Controls
{
    /// <summary>Where a visit of one ghost stands: -1 before its own cell, else the next of its spots.</summary>
    public sealed class Cursor
    {
        public int Spot { get; set; } = -1;
    }

    /// <summary>The whole visit of one ghost.</summary>
    public static void Visit(SafetyScan scan, Ghost ghost)
    {
        var reads = int.MaxValue;
        Visit(scan, ghost, new Cursor(), ref reads);
    }

    /// <summary>
    /// Visits one ghost from where <paramref name="at"/> stands, one cell read off <paramref name="reads"/> per cell looked
    /// in (its own, then the cells around each of its spots); stops between spots once no reads are left (a spot's six
    /// cells may take it up to five below zero). True once the ghost is done. The same warnings come out however it is cut.
    /// </summary>
    public static bool Visit(SafetyScan scan, Ghost ghost, Cursor at, ref int reads)
    {
        var looked = scan.Looked;
        if (!SafetyScan.Put(ghost).TryGet(out var placement) || !scan.Shape(placement).TryGet(out var shape))
            return true;
        if (at.Spot < 0)
        {
            reads--;
            foreach (var info in scan.Planned.At(placement.At))
                if (scan.Planned.AssumedFrom(info.Ref).TryGet(out var from) && from == ghost.Id && looked.Add(info.Ref.Id))
                    Check(scan, info);
            at.Spot = 0;
        }

        while (at.Spot < shape.Spots.Count)
        {
            if (reads <= 0)
                return false;
            foreach (var near in Around(scan, shape.Spots[at.Spot++], shape.Piece.Grid))
            {
                reads--;
                foreach (var info in scan.Planned.At(near))
                    if (looked.Add(info.Ref.Id))
                        Check(scan, info);
            }
        }

        return true;
    }

    /// <summary>The cells next to a spot at the piece's grid; for a face, next to both its large cells.</summary>
    private static IEnumerable<Cell> Around(SafetyScan scan, SpotKey spot, GridSize grid)
    {
        if (SafetyScan.IsFace(spot.Slot))
        {
            var (negative, positive) = scan.SidesOf(spot);
            yield return negative;
            yield return positive;
            yield break;
        }

        for (var direction = Direction.XPlus; direction <= Direction.ZMinus; direction++)
            yield return scan.Grid.Neighbour(spot.Cell, direction, grid);
    }

    private static void Check(SafetyScan scan, StructureInfo device)
    {
        if (!scan.Facts.ControlFace(device.Ref.Piece).TryGet(out var local) || !scan.Piece(device.Ref.Piece).TryGet(out var piece))
            return;
        var facing = scan.Grid.Apply(device.Turn, local);
        var front = scan.Grid.Neighbour(device.Ref.At, facing, piece.Grid);
        var own = scan.LargeOf(device.Ref.At, piece.Grid);
        var ahead = scan.LargeOf(front, piece.Grid);
        var blockers = new List<StructureInfo>(1);
        Centre(scan, ahead, device, blockers);
        if (ahead != own)
            Face(scan, scan.FaceBetween(own, facing), own, ahead, blockers);
        if (piece.Grid == GridSize.Small)
            SmallDevices(scan, front, device, blockers);
        if (blockers.Count == 0)
            return;

        var ghosts = new List<GhostId>(blockers.Count + 1);
        if (scan.PlanGhostOf(device.Ref).TryGet(out var mine))
            ghosts.Add(mine);
        var blockerIds = new List<string>(blockers.Count);
        foreach (var blocker in blockers)
        {
            blockerIds.Add(SafetyScan.Text(blocker.Ref.Id.Value));
            if (scan.PlanGhostOf(blocker.Ref).TryGet(out var theirs) && !ghosts.Contains(theirs))
                ghosts.Add(theirs);
        }

        if (ghosts.Count == 0)
            return;
        scan.Warnings.Add(SafetyCodes.ControlsUnreachable, "device " + SafetyScan.Text(device.Ref.Id.Value), new Dictionary<string, string>
        {
            ["device"] = SafetyScan.Text(device.Ref.Id.Value),
            ["facing"] = facing.ToString(),
            ["blocked-by"] = string.Join(", ", blockerIds),
        }, ghosts);
    }

    private static void Centre(SafetyScan scan, Cell large, StructureInfo device, List<StructureInfo> blockers)
    {
        var centre = new SpotKey(large, Slot.Centre);
        foreach (var info in scan.Planned.At(large))
            if (info.Ref.Id != device.Ref.Id && Takes(scan, info, centre))
                blockers.Add(info);
    }

    private static void Face(SafetyScan scan, Boundary face, Cell own, Cell ahead, List<StructureInfo> blockers)
    {
        foreach (var info in scan.Planned.At(own))
            if (Takes(scan, info, face) && !blockers.Contains(info))
                blockers.Add(info);
        foreach (var info in scan.Planned.At(ahead))
            if (Takes(scan, info, face) && !blockers.Contains(info))
                blockers.Add(info);
    }

    private static void SmallDevices(SafetyScan scan, Cell front, StructureInfo device, List<StructureInfo> blockers)
    {
        foreach (var info in scan.Planned.At(front))
            if (info.Ref.Id != device.Ref.Id && scan.Piece(info.Ref.Piece).TryGet(out var piece)
                && piece.Grid == GridSize.Small && (piece.Collision & SmallCollision.Devices) != 0 && !blockers.Contains(info))
                blockers.Add(info);
    }

    private static bool Takes(SafetyScan scan, StructureInfo info, SpotKey spot)
    {
        if (!scan.Shape(info).TryGet(out var shape))
            return false;
        foreach (var taken in shape.Spots)
            if (taken == spot)
                return true;
        return false;
    }

    private static bool Takes(SafetyScan scan, StructureInfo info, Boundary face)
    {
        if (!scan.Shape(info).TryGet(out var shape))
            return false;
        foreach (var taken in shape.Spots)
            if (face.Holds(taken))
                return true;
        return false;
    }
}
