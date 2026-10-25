using System;

namespace Lattice.Render;

/// <summary>
/// How much of a see-through look's strength a face seen square-on shows; the outline always shows the full strength.
/// About a third (0.35) up to strength 0.5, rising smoothly to all of it at strength 1, so at full strength the
/// hidden part looks like the visible ghost. The see-through shader reads it as <c>_LatticeSeeThrough.z</c>.
/// </summary>
internal static class SeeThroughCurve
{
    private const float Low = 0.35f;

    public static float SquareOn(float strength)
    {
        var t = Math.Min(1f, Math.Max(0f, (strength - 0.5f) / 0.5f));
        return Low + (1f - Low) * t * t * (3f - 2f * t);
    }
}
