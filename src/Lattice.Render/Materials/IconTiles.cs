using System;
using UnityEngine;

namespace Lattice.Render.Materials;

/// <summary>
/// The icon pattern's tiling, as <c>LatticeCommon.cginc</c> computes it per pixel (<c>LatticeIconPattern</c>): the face's
/// plane coordinate in tiles, the tile it falls in, and which half of an alternating pattern that tile draws. The
/// shader is the drawing; this states the same rule for anything that has to agree with it.
/// </summary>
internal static class IconTiles
{
    /// <summary>
    /// The plane coordinate in tiles of <paramref name="world"/> on a face with <paramref name="normal"/>: x and z for a
    /// face whose normal is mostly up or down; for walls the horizontal axis (signed so it reads left to right from the
    /// side the face looks to) and y.
    /// </summary>
    public static Vector2 Uv(Vector3 world, Vector3 normal, float size)
    {
        var n = new Vector3(Math.Abs(normal.x), Math.Abs(normal.y), Math.Abs(normal.z));
        Vector2 uv;
        if (n.y >= n.x && n.y >= n.z)
            uv = new Vector2(world.x, world.z);
        else if (n.x >= n.z)
            uv = new Vector2(world.z * (normal.x >= 0f ? 1f : -1f), world.y);
        else
            uv = new Vector2(world.x * (normal.z >= 0f ? -1f : 1f), world.y);
        return uv / Math.Max(size, 0.001f);
    }

    /// <summary>The tile (i, j) a plane coordinate falls in: floor of each.</summary>
    public static (int I, int J) Tile(Vector2 uv) => ((int)Math.Floor(uv.x), (int)Math.Floor(uv.y));

    /// <summary>
    /// Whether tile (i, j) of an alternating pattern draws its second half: i + j odd, negatives included. The shader
    /// reads it as frac((i + j) * 0.5) &gt; 0.25.
    /// </summary>
    public static bool Second(int i, int j)
    {
        var half = (i + j) * 0.5f;
        return half - (float)Math.Floor(half) > 0.25f;
    }
}
