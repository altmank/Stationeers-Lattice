using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Routes;
using static Lattice.Tests.Routes.RouteBench;

namespace Lattice.Tests.Routes;

/// <summary>
/// <see cref="RouteToggles.HugFramesAndWalls"/>: a cell held by no large cell with a frame or a face piece costs one
/// more cell, so a route follows structure where that costs no more than the open cells it avoids.
/// </summary>
public sealed class HugTests
{
    private static readonly RouteToggles Hug = Off with { HugFramesAndWalls = true };

    private static RouteBench FramesAlong(int z, int from, int to)
    {
        var bench = new RouteBench();
        for (var i = from; i <= to; i++)
            bench.Runs.Real("StructureFrame", Large(i, 0, z));
        return bench;
    }

    // Cells no frame's large cell holds (its closed box), the ends left out as the search leaves them out.
    private static int OpenCells(RoutePlan plan, IEnumerable<Cell> frames) =>
        plan.Run.Cells.Skip(1).SkipLast(1).Count(c => !frames.Any(f => Math.Abs(c.X - f.X) <= 10 && Math.Abs(c.Y - f.Y) <= 10 && Math.Abs(c.Z - f.Z) <= 10));

    [Fact]
    public void Beside_a_frame_line_the_route_follows_the_frames_at_the_same_length()
    {
        var bench = FramesAlong(z: 1, from: 0, to: 4);
        var (from, to) = (Large(0, 0, 0), Large(4, 0, 1));

        var off = bench.Plan(bench.Route(bench.Runs.Cable, from, to));
        var hug = bench.Plan(bench.Route(bench.Runs.Cable, from, to, Hug));

        Assert.Equal(off.Run.Cells.Count, hug.Run.Cells.Count);
        Assert.Equal(off.Bends, hug.Bends);
        Assert.Contains(off.Run.Cells, c => c.Z < 120 && c.X > 130);
        Assert.All(hug.Run.Cells.Where(c => c.X > 130), c => Assert.True(c.Z >= 120, $"{c} is in open air"));
    }

    [Fact]
    public void A_route_that_reaches_for_frames_gets_no_longer_than_one_cell_per_open_cell_it_avoids()
    {
        var bench = FramesAlong(z: 2, from: 0, to: 6);
        var frames = Enumerable.Range(0, 7).Select(i => Large(i, 0, 2)).ToList();
        var (from, to) = (Large(0, 0, 0), Large(6, 0, 0));

        var off = bench.Plan(bench.Route(bench.Runs.Cable, from, to));
        var hug = bench.Plan(bench.Route(bench.Runs.Cable, from, to, Hug));

        Assert.True(hug.Run.Cells.Count > off.Run.Cells.Count, "the route stayed in open air");
        Assert.True(hug.Run.Cells.Count - off.Run.Cells.Count <= OpenCells(off, frames) - OpenCells(hug, frames));
    }

    [Fact]
    public void A_wall_on_the_far_face_of_the_next_row_is_followed_as_a_frame_is()
    {
        var bench = new RouteBench();
        for (var i = 0; i <= 4; i++)
            bench.Runs.Real("StructureWallIron", Large(i, 0, 2));
        var (from, to) = (Large(0, 0, 0), Large(4, 0, 1));

        var off = bench.Plan(bench.Route(bench.Runs.Cable, from, to));
        var hug = bench.Plan(bench.Route(bench.Runs.Cable, from, to, Hug));

        Assert.Contains(off.Run.Cells, c => c.Z < 120 && c.X > 130);
        Assert.All(hug.Run.Cells.Where(c => c.X > 130), c => Assert.True(c.Z >= 120, $"{c} is in open air"));
    }

    [Fact]
    public void On_frames_still_refuses_open_cells_with_hugging_on()
    {
        var bench = new RouteBench(RouteLimits.Default with { Margin = 4 });

        var why = bench.Refusal(bench.Route(bench.Runs.Cable, Large(0, 0, 0), Large(6, 0, 0), Hug with { OnFrames = true }));

        Assert.Equal(ReasonCodes.NoRoute, why.Code);
    }
}
