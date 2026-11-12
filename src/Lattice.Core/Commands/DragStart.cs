using Lattice.Api;

namespace Lattice.Core.Commands;

/// <summary>
/// Which previews a dragging cursor leaves behind: a run or route asked from the same start (cell and port) as an older
/// one of its own type, with another end.
/// </summary>
internal static class DragStart
{
    /// <summary>
    /// Whether <paramref name="newer"/> replaces <paramref name="older"/>: two different runs, or two different routes,
    /// that start at the same cell and port. A run never replaces a route, nor the other way round; every other command
    /// replaces nothing.
    /// </summary>
    public static bool Overtakes(Command newer, Command older) => (newer, older) switch
    {
        (Command.PlanRun a, Command.PlanRun b) => a != b && a.Request.From == b.Request.From && a.Request.FromPort == b.Request.FromPort,
        (Command.PlanRoute a, Command.PlanRoute b) => a != b && a.Request.From == b.Request.From && a.Request.FromPort == b.Request.FromPort,
        _ => false,
    };
}
