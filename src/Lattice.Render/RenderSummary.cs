using System;

namespace Lattice.Render;

/// <summary>What render load found: whose materials draw, how many, how many have an unsupported shader, and the device and Unity version they run on.</summary>
internal sealed record RenderSummary(bool Own, int Materials, int Unsupported, string Device, string UnityVersion)
{
    /// <summary>The one Info line render writes when it opens.</summary>
    public string Line() =>
        FormattableString.Invariant(
            $"Lattice render: drawing with {(Own ? "Lattice's shaders" : "the stand-in")} ({Materials} materials, {Unsupported} unsupported) on {Device}, Unity {UnityVersion}.");
}
