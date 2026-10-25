using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Routes;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Routes;

/// <summary>
/// <see cref="RouteToggles.First"/>: among routes of equal cells and bends, which leg comes first. Cells are written as
/// small-cell steps from <see cref="Lattice.Tests.Runs.RunBench.Origin"/>; the game's Y is up.
/// </summary>
public sealed class FirstLegTests
{
    private static readonly Cell Start = At(2, 2, 2);
    private static readonly Cell AcrossAndUp = At(8, 6, 2);
    private static readonly Cell ThreeAxes = At(9, 5, 7);
    private static readonly Cell UpAndAlong = At(2, 7, 9);

    private static RoutePlan Plan(Cell to, Option<FirstLeg> first = default, bool fewestBends = false)
    {
        var bench = new RouteBench();
        return bench.Plan(bench.Route(bench.Runs.Cable, Start, to, RouteBench.Off with { FewestBends = fewestBends, First = first }));
    }

    private static string Steps(RoutePlan plan) =>
        string.Join(" ", plan.Run.Cells.Select(c => $"{(c.X - Origin.X) / 5},{(c.Y - Origin.Y) / 5},{(c.Z - Origin.Z) / 5}"));

    private static bool FirstStepIsVertical(RoutePlan plan) => plan.Run.Cells[1].Y != plan.Run.Cells[0].Y;

    public static IEnumerable<object[]> Ends() => [[AcrossAndUp], [UpAndAlong], [ThreeAxes]];

    [Theory]
    [MemberData(nameof(Ends))]
    public void Vertical_goes_up_first_and_level_goes_across_first_with_the_same_cells_and_bends(Cell to)
    {
        var vertical = Plan(to, Option.Some(FirstLeg.Vertical));
        var level = Plan(to, Option.Some(FirstLeg.Level));

        Assert.True(FirstStepIsVertical(vertical), Steps(vertical));
        Assert.False(FirstStepIsVertical(level), Steps(level));
        Assert.Equal(vertical.Run.Cells.Count, level.Run.Cells.Count);
        Assert.Equal(vertical.Bends, level.Bends);
    }

    [Fact]
    public void Vertical_takes_the_whole_climb_before_going_across()
    {
        var plan = Plan(AcrossAndUp, Option.Some(FirstLeg.Vertical));

        Assert.Equal("2,2,2 2,3,2 2,4,2 2,5,2 2,6,2 3,6,2 4,6,2 5,6,2 6,6,2 7,6,2 8,6,2", Steps(plan));
    }

    [Theory]
    [InlineData(false, "2,2,2 3,2,2 4,2,2 5,2,2 6,2,2 7,2,2 8,2,2 8,3,2 8,4,2 8,5,2 8,6,2")]
    [InlineData(true, "2,2,2 3,2,2 4,2,2 5,2,2 6,2,2 7,2,2 8,2,2 8,3,2 8,4,2 8,5,2 8,6,2")]
    public void No_first_leg_gives_the_route_the_search_gave_before_across_and_up(bool fewestBends, string steps) =>
        Assert.Equal(steps, Steps(Plan(AcrossAndUp, fewestBends: fewestBends)));

    [Theory]
    [InlineData(false, "2,2,2 3,2,2 4,2,2 5,2,2 6,2,2 7,2,2 8,2,2 9,2,2 9,3,2 9,4,2 9,5,2 9,5,3 9,5,4 9,5,5 9,5,6 9,5,7")]
    [InlineData(true, "2,2,2 3,2,2 4,2,2 5,2,2 6,2,2 7,2,2 8,2,2 9,2,2 9,3,2 9,4,2 9,5,2 9,5,3 9,5,4 9,5,5 9,5,6 9,5,7")]
    public void No_first_leg_gives_the_route_the_search_gave_before_on_three_axes(bool fewestBends, string steps) =>
        Assert.Equal(steps, Steps(Plan(ThreeAxes, fewestBends: fewestBends)));

    [Fact]
    public void No_first_leg_gives_the_route_the_search_gave_before_up_and_along() =>
        Assert.Equal("2,2,2 2,3,2 2,4,2 2,5,2 2,6,2 2,7,2 2,7,3 2,7,4 2,7,5 2,7,6 2,7,7 2,7,8 2,7,9", Steps(Plan(UpAndAlong)));

    [Theory]
    [MemberData(nameof(Ends))]
    public void A_first_leg_never_adds_a_bend_or_a_cell_under_fewest_bends_or_without(Cell to)
    {
        foreach (var fewestBends in new[] { false, true })
        {
            var none = Plan(to, fewestBends: fewestBends);
            foreach (var first in new[] { FirstLeg.Vertical, FirstLeg.Level })
            {
                var chosen = Plan(to, Option.Some(first), fewestBends);
                Assert.Equal(none.Bends, chosen.Bends);
                Assert.Equal(none.Run.Cells.Count, chosen.Run.Cells.Count);
            }
        }
    }
}
