using System.Linq;
using Lattice.Api;
using Lattice.Core.Routes;
using static Lattice.Tests.Routes.RouteBench;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Routes;

/// <summary>
/// The <c>stopped</c> fact of a route that finds no route: for each neighbour of the nearest cell the route never
/// reached and could not enter, the direction and why, in direction order.
/// </summary>
public sealed class RouteStoppedTests
{
    private static RouteToggles Only(bool onFrames = false, bool clear = false) => new(onFrames, false, clear, false, false);

    [Fact]
    public void A_route_walled_off_by_a_doors_keep_out_stopped_at_an_opening_on_that_side()
    {
        var bench = new RouteBench(RouteLimits.Default with { Margin = 0 });
        bench.Runs.Real("ModHatch", Large(2, 0, 0));

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, Large(0, 0, 0), Large(6, 0, 0), Only(clear: true)));

        Assert.Equal(ReasonCodes.NoRoute, why.Code);
        Assert.Equal("145,110,110", why.Facts["nearest"]);
        Assert.Equal("x+:opening,y+:outside-box,y-:outside-box,z+:outside-box,z-:outside-box", why.Facts["stopped"]);
    }

    [Fact]
    public void A_route_on_frames_with_no_frames_stopped_off_the_frames()
    {
        var bench = new RouteBench();

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, Large(0, 0, 0), Large(6, 0, 0), Only(onFrames: true)));

        Assert.Equal(ReasonCodes.NoRoute, why.Code);
        Assert.Contains("x+:off-frame", why.Facts["stopped"].Split(','));
    }

    [Fact]
    public void A_box_too_small_to_go_round_what_stands_stopped_at_its_edge()
    {
        var bench = new RouteBench(RouteLimits.Default with { Margin = 0 });
        bench.Runs.Real("StructureAreaPowerControl", At(6, 2, 2));

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, At(2, 2, 2), At(10, 2, 2)));

        Assert.Equal(ReasonCodes.NoRoute, why.Code);
        Assert.Equal("x+:collides,y+:outside-box,y-:outside-box,z+:outside-box,z-:outside-box", why.Facts["stopped"]);
    }

    [Fact]
    public void A_leg_boxed_in_by_an_earlier_leg_stopped_where_it_would_cross()
    {
        var bench = new RouteBench(RouteLimits.Default with { Margin = 0 });
        var request = bench.Route(bench.Runs.Cable, At(0, 2, 2), At(3, 2, 6)) with { Via = ValueList.From(new[] { At(6, 2, 2), At(3, 2, 0) }) };

        var why = bench.Refusal(request);

        Assert.Equal("2", why.Facts["leg"]);
        Assert.Equal("x+:outside-box,x-:outside-box,y+:outside-box,y-:outside-box,z+:crosses", why.Facts["stopped"]);
    }

    [Fact]
    public void A_search_limit_names_only_the_neighbours_it_could_not_enter()
    {
        var bench = new RouteBench();

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, At(2, 2, 2), At(18, 2, 2)) with { SearchBound = Option.Some(3) });

        Assert.Equal(ReasonCodes.SearchLimit, why.Code);
        Assert.Equal("", why.Facts["stopped"]);
    }

    [Fact]
    public void The_facts_before_stopped_are_unchanged()
    {
        var bench = new RouteBench(RouteLimits.Default with { Margin = 0 });
        bench.Runs.Real("StructureAreaPowerControl", At(6, 2, 2));

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, At(2, 2, 2), At(10, 2, 2)));

        Assert.Equal(
            new[] { "bound", "cells-left", "expanded", "from", "margin", "nearest", "stopped", "to" },
            why.Facts.Keys.OrderBy(k => k, System.StringComparer.Ordinal).ToArray());
    }
}
