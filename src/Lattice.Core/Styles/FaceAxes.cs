using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;

namespace Lattice.Core.Styles;

/// <summary>Which axis a placed piece's face is square to, from the spots it takes.</summary>
internal static class FaceAxes
{
    /// <summary>The axis every spot's face is square to; null when a spot is not a face, the faces differ in axis, or there are none.</summary>
    public static Axis? Of(IReadOnlyList<SpotKey> spots)
    {
        Axis? axis = null;
        for (var i = 0; i < spots.Count; i++)
        {
            if (Faces.DirectionOf(spots[i].Slot) is not { } face)
                return null;
            var along = face.AxisOf();
            if (axis is { } seen && seen != along)
                return null;
            axis = along;
        }

        return axis;
    }
}
