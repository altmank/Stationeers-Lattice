using System.Collections.Generic;
using System;
using System.Linq;
using Lattice.Game.Live;

namespace Lattice.Tests.BigBase;

/// <summary>The move that puts the generated base on the world's ground beside the landing: footprint, side, gap and rise.</summary>
public sealed class BaseOffsetTests
{
    // The held base file's extent: piece centres x 596.5 to 760, y 301 to 371, z 600 to 692.
    private static readonly Footprint Base = Footprint.Of([(596.5f, 301f, 600f), (760f, 371f, 692f), (601f, 303f, 605f)])!.Value;

    private static bool WholeCells(float metres) => Math.Abs(metres / BaseOffset.Grid - Math.Round(metres / BaseOffset.Grid)) < 1e-4;

    [Fact]
    public void The_footprint_reaches_half_a_cell_past_the_outer_centres_and_keeps_the_lowest_centre()
    {
        Assert.Equal(new Footprint(595.5f, 599f, 761f, 693f, 301f), Base);
        Assert.Equal((678.25f, 646f), Base.Centre);
        Assert.Equal(5, Base.Corners.Count);
        Assert.Contains((595.5f, 693f), Base.Corners);
        Assert.Contains(Base.Centre, Base.Corners);
    }

    [Fact]
    public void No_pieces_have_no_footprint() => Assert.Null(Footprint.Of([]));

    [Fact]
    public void A_lander_west_of_the_body_puts_the_base_east_of_it_a_gap_away()
    {
        var landing = new LandingSpot(100f, 50f, 95f, 49f);
        var (dx, dz) = BaseOffset.Beside(Base, landing);
        var moved = Base.Moved(new BaseOffset(dx, 0f, dz));

        Assert.True(moved.MinX >= 100f + BaseOffset.Gap);
        Assert.True(moved.MinX < 100f + BaseOffset.Gap + BaseOffset.Grid);
        Assert.InRange(moved.Centre.Z, 50f - BaseOffset.HalfGrid, 50f + BaseOffset.HalfGrid);
        Assert.True(moved.DistanceTo(95f, 49f) > moved.DistanceTo(100f, 50f));
    }

    [Fact]
    public void A_lander_east_of_the_body_puts_the_base_west_of_it_a_gap_away()
    {
        var (dx, dz) = BaseOffset.Beside(Base, new LandingSpot(100f, 50f, 104f, 52f));
        var moved = Base.Moved(new BaseOffset(dx, 0f, dz));

        Assert.True(moved.MaxX <= 100f - BaseOffset.Gap);
        Assert.True(moved.MaxX > 100f - BaseOffset.Gap - BaseOffset.Grid);
        Assert.InRange(moved.Centre.Z, 50f - BaseOffset.HalfGrid, 50f + BaseOffset.HalfGrid);
    }

    [Theory]
    [InlineData(-6f, 1f)]
    [InlineData(6f, -1f)]
    public void A_lander_mostly_along_z_puts_the_base_across_z_away_from_it(float landerDz, float side)
    {
        var (dx, dz) = BaseOffset.Beside(Base, new LandingSpot(-40f, 210f, -38f, 210f + landerDz));
        var moved = Base.Moved(new BaseOffset(dx, 0f, dz));

        if (side > 0)
            Assert.True(moved.MinZ >= 210f + BaseOffset.Gap);
        else
            Assert.True(moved.MaxZ <= 210f - BaseOffset.Gap);
        Assert.InRange(moved.Centre.X, -40f - BaseOffset.HalfGrid, -40f + BaseOffset.HalfGrid);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(100f, 50f)]
    public void With_no_lander_or_one_under_the_body_the_base_goes_to_plus_x(float? landerX, float? landerZ)
    {
        var (dx, dz) = BaseOffset.Beside(Base, new LandingSpot(100f, 50f, landerX, landerZ));
        Assert.True(Base.Moved(new BaseOffset(dx, 0f, dz)).MinX >= 100f + BaseOffset.Gap);
    }

    [Theory]
    [InlineData(100f, 50f, 95f, 49f)]
    [InlineData(-333.3f, 17.7f, -333f, 30f)]
    [InlineData(1234.5f, -876.25f, null, null)]
    public void Every_move_is_whole_large_cells_and_leaves_body_and_lander_outside_by_the_gap(float bodyX, float bodyZ, float? landerX, float? landerZ)
    {
        var (dx, dz) = BaseOffset.Beside(Base, new LandingSpot(bodyX, bodyZ, landerX, landerZ));
        var moved = Base.Moved(new BaseOffset(dx, 0f, dz));

        Assert.True(WholeCells(dx), $"dx {dx}");
        Assert.True(WholeCells(dz), $"dz {dz}");
        Assert.True(moved.DistanceTo(bodyX, bodyZ) >= BaseOffset.Gap);
        if (landerX is { } lx && landerZ is { } lz)
            Assert.True(moved.DistanceTo(lx, lz) >= BaseOffset.Gap);
    }

    [Theory]
    [InlineData(290f, -8f)]
    [InlineData(290.3f, -6f)]
    [InlineData(298f, 0f)]
    [InlineData(412.9f, 116f)]
    public void The_rise_puts_the_lowest_underside_two_to_four_metres_over_the_highest_ground(float highest, float expected)
    {
        var dy = BaseOffset.Up(301f, highest);
        var underside = 301f + dy - BaseOffset.HalfGrid;

        Assert.Equal(expected, dy);
        Assert.True(WholeCells(dy));
        Assert.InRange(underside - highest, BaseOffset.Rise, BaseOffset.Rise + BaseOffset.Grid);
    }

    [Theory]
    [InlineData(290f)]
    [InlineData(-17.31f)]
    [InlineData(301f)]
    [InlineData(5000.5f)]
    public void The_moved_base_passes_the_ground_verdict_at_every_measured_point(float highest)
    {
        var samples = new[] { new GroundSample(1f, 1f, highest - 3f), new GroundSample(2f, 2f, highest), new GroundSample(3f, 3f, highest - 0.5f) };
        var placement = GroundPlacement.Over((0f, 0f), 301f, samples);

        Assert.True(GroundVerdict.Judge(samples, 301f + placement.Offset!.Value.Dy).Ok);
    }

    [Fact]
    public void Terrain_lower_at_one_corner_lifts_the_base_over_the_highest_sample()
    {
        var samples = new[] { new GroundSample(0f, 0f, 20f), new GroundSample(10f, 0f, 21f), new GroundSample(0f, 10f, 9f) };
        var placement = GroundPlacement.Over((12f, -6f), 301f, samples);

        Assert.Equal(21f, placement.Highest);
        Assert.Equal(new BaseOffset(12f, BaseOffset.Up(301f, 21f), -6f), placement.Offset);
        Assert.Empty(placement.Misses);
    }

    [Fact]
    public void A_point_with_no_ground_gives_no_offset_and_is_named()
    {
        var samples = new[] { new GroundSample(0f, 0f, 20f), new GroundSample(10.5f, 4f, null) };
        var placement = GroundPlacement.Over((0f, 0f), 301f, samples);

        Assert.Null(placement.Offset);
        Assert.Equal(new[] { "x 10.5 z 4" }, placement.Misses);
    }

    [Fact]
    public void No_samples_give_no_offset() => Assert.Null(GroundPlacement.Over((0f, 0f), 301f, []).Offset);

    [Fact]
    public void The_offset_is_written_as_three_metres() => Assert.Equal("12 -8 -4.5", new BaseOffset(12f, -8f, -4.5f).Text);
}

/// <summary>Where the ground is measured before the spawn, and from where.</summary>
public sealed class GroundMeasureTests
{
    private static readonly Footprint Base = new(0f, 0f, 100f, 60f, 301f);

    [Fact]
    public void The_candidates_are_the_stand_the_corners_the_centre_the_footprint_grid_and_a_ring_around_the_stand_each_once()
    {
        var points = GroundVerdict.Candidates(Base, 5f, 5f);
        var ring = points.Where(static p => Math.Abs(Math.Sqrt((p.X - 5f) * (p.X - 5f) + (p.Z - 5f) * (p.Z - 5f)) - GroundVerdict.Ring) < 1e-3).ToList();

        Assert.Equal((5f, 5f), points[0]);
        Assert.Contains((100f, 60f), points);
        Assert.Contains((50f, 30f), points);
        Assert.Equal(8, ring.Count);
        Assert.All(Base.Grid(GroundVerdict.Spacing), p => Assert.Contains(p, points));
        Assert.Equal(points.Count, points.Distinct().Count());
        Assert.Equal(126 + 1 + 1 + 8, points.Count);
    }

    [Fact]
    public void The_grid_covers_the_footprint_edge_to_edge_at_most_the_spacing_apart()
    {
        var grid = Base.Grid(8f);
        var xs = grid.Select(static p => p.X).Distinct().OrderBy(static x => x).ToList();
        var zs = grid.Select(static p => p.Z).Distinct().OrderBy(static z => z).ToList();

        Assert.Equal(xs.Count * zs.Count, grid.Count);
        Assert.Equal((0f, 100f), (xs[0], xs[^1]));
        Assert.Equal((0f, 60f), (zs[0], zs[^1]));
        Assert.All(xs.Zip(xs.Skip(1)), g => Assert.InRange(g.Second - g.First, 0.01f, 8f));
        Assert.All(zs.Zip(zs.Skip(1)), g => Assert.InRange(g.Second - g.First, 0.01f, 8f));
        Assert.Equal(new[] { (3f, 4f) }, new Footprint(3f, 4f, 3f, 4f, 0f).Grid(8f));
    }

    // Flat ground at 10 m with one hill 12 m high and 40 m across its foot, between the corners of a base the held file's
    // size: no corner, the centre or the stand spot is on the hill.
    private static readonly Footprint Big = new(0f, 0f, 165.5f, 94f, 301f);

    private static float Hill(float x, float z)
    {
        var d = (float)Math.Sqrt((x - 37f) * (x - 37f) + (z - 29f) * (z - 29f));
        return 10f + 12f * Math.Max(0f, 1f - d / 40f);
    }

    private static GroundSample[] Measure(IEnumerable<(float X, float Z)> points) =>
        points.Select(static p => new GroundSample(p.X, p.Z, Hill(p.X, p.Z))).ToArray();

    [Fact]
    public void A_hill_between_the_corners_lifts_the_base_over_its_top()
    {
        var placement = GroundPlacement.Over((0f, 0f), Big.Lowest, Measure(GroundVerdict.Candidates(Big, 5.5f, 6f)));
        var underside = Big.Lowest + placement.Offset!.Value.Dy - BaseOffset.HalfGrid;

        Assert.True(underside > 22f, $"underside {underside} under the hill's top 22");
    }

    [Fact]
    public void Corners_and_centre_alone_miss_the_hill_and_the_whole_footprint_verdict_catches_it()
    {
        var cornersOnly = GroundPlacement.Over((0f, 0f), Big.Lowest, Measure(Big.Corners));
        var lowered = Big.Lowest + cornersOnly.Offset!.Value.Dy;

        Assert.True(lowered - BaseOffset.HalfGrid < 22f);
        Assert.False(GroundVerdict.Judge(Measure(GroundVerdict.Candidates(Big, 5.5f, 6f)), lowered).Ok);
    }

    [Fact]
    public void The_base_placed_over_the_whole_footprint_passes_the_whole_footprint_verdict()
    {
        var samples = Measure(GroundVerdict.Candidates(Big, 5.5f, 6f));
        var placement = GroundPlacement.Over((0f, 0f), Big.Lowest, samples);

        Assert.True(GroundVerdict.Judge(samples, Big.Lowest + placement.Offset!.Value.Dy).Ok);
    }

    [Fact]
    public void Each_point_is_measured_from_its_nearest_spot_and_idle_spots_are_left_out()
    {
        var visits = GroundPlacement.Visits([(0f, 0f), (100f, 0f), (500f, 500f)], [(1f, 1f), (90f, 3f), (40f, 0f), (60f, 0f)]);

        Assert.Equal(2, visits.Count);
        Assert.Equal((0f, 0f), visits[0].Spot);
        Assert.Equal(new[] { (1f, 1f), (40f, 0f) }, visits[0].Points);
        Assert.Equal(new[] { (90f, 3f), (60f, 0f) }, visits[1].Points);
    }

    [Fact]
    public void Every_candidate_is_measured_once()
    {
        var points = GroundVerdict.Candidates(Base, 5f, 5f);
        var visits = GroundPlacement.Visits([(5f, 5f), .. Base.Corners], points);

        Assert.Equal(points.Count, visits.Sum(static v => v.Points.Count));
    }

    [Fact]
    public void Over_the_held_base_every_point_lies_within_the_loaded_radius_of_the_spot_it_is_measured_from()
    {
        var held = new Footprint(595.5f, 599f, 761f, 693f, 301f);
        var visits = GroundPlacement.Visits([(601f, 605f), .. held.Corners], GroundVerdict.Candidates(held, 601f, 605f));

        Assert.All(visits, v => Assert.All(v.Points, p =>
            Assert.True(Math.Sqrt((p.X - v.Spot.X) * (p.X - v.Spot.X) + (p.Z - v.Spot.Z) * (p.Z - v.Spot.Z)) <= GroundVerdict.LoadedRadius, $"{p} from {v.Spot}")));
    }
}
