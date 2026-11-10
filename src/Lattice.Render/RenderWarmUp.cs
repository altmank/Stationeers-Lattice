using Lattice.Core.EntryPoint;
using Lattice.Core.Slices;

namespace Lattice.Render;

/// <summary>Render's part of the warm-up: its drawing, batch rebuilds, claims, marks and materials compiled in idle frames.</summary>
internal static class RenderWarmUp
{
    internal const string Set = "render";

    private static readonly string[] Namespaces =
    [
        "Lattice.Render", "Lattice.Render.Claims", "Lattice.Render.Drawing", "Lattice.Render.Gpu", "Lattice.Render.Marks", "Lattice.Render.Materials",
    ];

    /// <summary>Queues the warm-up of Render's methods on the world's scheduler when the world warms up.</summary>
    public static void Schedule(LatticeWorld world)
    {
        if (world.WarmsUp)
            WarmUp.Schedule(world.Scheduler, Set, typeof(Renderer).Assembly, Namespaces);
    }
}
