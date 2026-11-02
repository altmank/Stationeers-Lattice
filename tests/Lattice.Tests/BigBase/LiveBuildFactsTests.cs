using System.Collections.Generic;
using Lattice.Game.Live;

namespace Lattice.Tests.BigBase;

/// <summary>The pure parts of the live big-base build: the base file's header, the ground verdict and the world file.</summary>
public sealed class LiveBuildFactsTests
{
    private static readonly string[] Header =
    [
        "# lattice-gen-big: a generated big base, seed 128, 36120 structures",
        "# catalogue catalogue-headless.lcat SHA1 A2903B76; mix lattice-gen-big-mix.txt SHA1 BC2D6B14",
        "# station lattice-gen-big",
        "# stand 61.5 30.1 62.5",
        "# prefab\tx\ty\tz\tqx\tqy\tqz\tqw\tstate",
        "StructureFrame\t601\t301\t621\t0\t0\t0\t1\t3",
    ];

    [Fact]
    public void The_header_gives_station_stand_seed_and_hashes()
    {
        var header = BaseHeader.Read(Header);
        Assert.Equal("lattice-gen-big", header.Station);
        Assert.Equal(new StandSpot(61.5f, 30.1f, 62.5f), header.Stand);
        Assert.Equal("128", header.Seed);
        Assert.Equal("A2903B76", header.CatalogueSha1);
        Assert.Equal("BC2D6B14", header.MixSha1);
    }

    [Fact]
    public void A_header_without_station_or_stand_has_none()
    {
        var header = BaseHeader.Read(["# nothing here", "StructureFrame\t1\t2\t3\t0\t0\t0\t1\t3"]);
        Assert.Null(header.Station);
        Assert.Null(header.Stand);
        Assert.Null(header.SaveName(5000));
        Assert.Equal("unknown", header.Seed);
    }

    [Fact]
    public void A_stand_line_that_is_not_three_numbers_is_ignored()
    {
        Assert.Null(BaseHeader.Read(["# stand 1 2"]).Stand);
        Assert.Null(BaseHeader.Read(["# stand a b c"]).Stand);
    }

    [Theory]
    [InlineData(5000, "lattice-gen-big-held")]
    [InlineData(1, "lattice-gen-big-held")]
    [InlineData(0, "lattice-gen-big")]
    public void The_save_name_is_the_station_plus_held_when_anything_is_held_back(int held, string expected) =>
        Assert.Equal(expected, BaseHeader.Read(Header).SaveName(held));

    [Fact]
    public void Ground_below_the_lowest_piece_by_more_than_a_metre_passes()
    {
        var verdict = GroundVerdict.Judge([10f, 12f, 11f, 9f, 10.5f], 20f);
        Assert.True(verdict.Ok);
        Assert.Equal(9f, verdict.Min);
        Assert.Equal(12f, verdict.Max);
        Assert.Equal("ground under the base: min 9 max 12; lowest piece 20", verdict.Describe());
    }

    [Fact]
    public void Ground_exactly_a_metre_under_the_lowest_piece_passes_and_above_that_fails()
    {
        Assert.True(GroundVerdict.Judge([19f], 20f).Ok);
        Assert.False(GroundVerdict.Judge([19.01f], 20f).Ok);
        Assert.False(GroundVerdict.Judge([5f, 5f, 25f, 5f, 5f], 20f).Ok);
    }

    [Fact]
    public void A_point_that_hits_nothing_is_logged_and_not_failed()
    {
        var verdict = GroundVerdict.Judge([null, 3f, null, 4f, 3.5f], 20f);
        Assert.True(verdict.Ok);
        Assert.Equal(2, verdict.Misses);
        Assert.Contains("(2 points hit nothing)", verdict.Describe());
        var none = GroundVerdict.Judge([null, null], 20f);
        Assert.True(none.Ok);
        Assert.Equal("ground under the base: min none max none; lowest piece 20 (2 points hit nothing)", none.Describe());
    }

    [Fact]
    public void The_world_file_is_one_key_value_line_per_fact()
    {
        var text = WorldFile.Text(
        [
            new KeyValuePair<string, string>("station", "lattice-gen-big-held"),
            new KeyValuePair<string, string>("stand", WorldFile.Metres(61.5f, 30.1f, 62.5f)),
            new KeyValuePair<string, string>("note", "two\nlines"),
        ]);
        Assert.Equal("station lattice-gen-big-held\nstand 61.5 30.1 62.5\nnote two lines\n", text);
    }
}
