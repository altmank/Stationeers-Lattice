using Lattice.Api;

namespace Lattice.Core.Routes;

/// <summary>
/// What bounds one route search. <see cref="Margin"/> is how far, in the kit's cells, the search box reaches past the
/// two ends on every side. <see cref="Expansions"/> is the most search states taken from the open set; none scales it
/// with the request: <see cref="BaseExpansions"/> plus <see cref="ExpansionsPerCell"/> per cell between the ends, so a
/// long route gets room in proportion. <see cref="PerStep"/> is how many states one sliced step takes. None of these
/// limits a route's length: a long route is bounded only by its box and its expansions, and its ghosts by the plan
/// size limit a higher layer sets.
/// </summary>
internal sealed record RouteLimits(int Margin, Option<int> Expansions, int PerStep)
{
    public const int BaseExpansions = 4096;

    public const int ExpansionsPerCell = 256;

    /// <summary>The most branches one route request may have (<see cref="RouteRequest.Branches"/>).</summary>
    public const int MaxBranches = 16;

    /// <summary>32 cells (16 m on the small grid) past the ends, expansions scaled with the request, 32 states a step.</summary>
    public static RouteLimits Default => new(Margin: 32, Expansions: Option<int>.None, PerStep: 32);

    /// <summary>The expansions for ends this many cells apart.</summary>
    public int ExpansionsFor(int cellsApart) =>
        Expansions.TryGet(out var fixedBound) ? fixedBound : BaseExpansions + ExpansionsPerCell * cellsApart;
}
