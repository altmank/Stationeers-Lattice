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

    private static GroundSample[] Heights(params float?[] ys)
    {
        var samples = new GroundSample[ys.Length];
        for (var i = 0; i < ys.Length; i++)
            samples[i] = new GroundSample(i * 10f, 5f, ys[i]);
        return samples;
    }

    [Fact]
    public void Ground_below_the_lowest_piece_by_more_than_a_metre_passes()
    {
        var verdict = GroundVerdict.Judge(Heights(10f, 12f, 11f, 9f, 10.5f), 20f);
        Assert.True(verdict.Ok);
        Assert.Equal(9f, verdict.Min);
        Assert.Equal(12f, verdict.Max);
        Assert.Equal("ground under the base: min 9 max 12; lowest piece 20", verdict.Describe());
    }

    [Fact]
    public void Ground_exactly_a_metre_under_the_lowest_piece_passes_and_above_that_fails()
    {
        Assert.True(GroundVerdict.Judge(Heights(19f), 20f).Ok);
        Assert.False(GroundVerdict.Judge(Heights(19.01f), 20f).Ok);
        Assert.False(GroundVerdict.Judge(Heights(5f, 5f, 25f, 5f, 5f), 20f).Ok);
    }

    [Fact]
    public void A_ray_that_hits_nothing_fails_and_names_its_point()
    {
        var verdict = GroundVerdict.Judge(Heights(null, 3f, 4f), 20f);
        Assert.False(verdict.Ok);
        Assert.Equal(["x 0 z 5"], verdict.Misses);
        Assert.Equal("ground under the base: min 3 max 4; lowest piece 20; no ground found at x 0 z 5", verdict.Describe());
        Assert.False(GroundVerdict.Judge(Heights(null, null), 20f).Ok);
    }

    [Fact]
    public void No_sample_at_all_fails()
    {
        Assert.False(GroundVerdict.Judge([], 20f).Ok);
    }

    [Fact]
    public void Only_points_within_the_loaded_radius_of_the_stand_spot_are_sampled()
    {
        var near = GroundVerdict.Within([(0f, 0f), (59f, 0f), (60f, 0f), (60.1f, 0f), (40f, 40f), (-100f, 0f)], 0f, 0f);
        Assert.Equal([(0f, 0f), (59f, 0f), (60f, 0f), (40f, 40f)], near);
        Assert.Equal([(1000f, 1000f)], GroundVerdict.Within([(1000f, 1000f), (0f, 0f)], 1010f, 990f));
    }

    [Fact]
    public void A_failed_step_logs_one_FAIL_line_and_ends_the_build()
    {
        var gate = new StepGate();
        var lines = new List<string>();
        Assert.False(gate.Failed);
        gate.Fail(lines.Add, "the main menu did not come up in 300 s");
        Assert.True(gate.Failed);
        Assert.Equal(["FAIL bigbase: the main menu did not come up in 300 s"], lines);
    }

    [Fact]
    public void A_long_wait_logs_once_for_each_full_minute()
    {
        var progress = new WaitProgress();
        var due = new List<int>();
        for (var s = 0; s <= 650; s++)
            if (progress.Due(s))
                due.Add(s);
        Assert.Equal([60, 120, 180, 240, 300, 360, 420, 480, 540, 600], due);
        Assert.False(new WaitProgress().Due(59.9));
        Assert.True(new WaitProgress().Due(200));
    }

    [Theory]
    [InlineData(0f, false, true)]
    [InlineData(0.5f, false, true)]
    [InlineData(0.6f, true, true)]
    [InlineData(1f, true, true)]
    [InlineData(1.01f, true, false)]
    [InlineData(30f, true, false)]
    public void The_body_is_put_back_beyond_half_a_metre_and_counts_as_staying_within_a_metre(float away, bool restand, bool stayed)
    {
        Assert.Equal(restand, BodyHold.NeedsRestand(away));
        Assert.Equal(stayed, BodyHold.Stayed(away));
    }

    [Fact]
    public void The_world_file_is_one_key_value_line_per_fact_and_leaves_out_facts_with_no_value()
    {
        var text = WorldFile.Text(
        [
            new KeyValuePair<string, string?>("station", "lattice-gen-big-held"),
            new KeyValuePair<string, string?>("world", null),
            new KeyValuePair<string, string?>("stand", WorldFile.Metres(61.5f, 30.1f, 62.5f)),
            new KeyValuePair<string, string?>("note", "two\nlines"),
        ]);
        Assert.Equal("station lattice-gen-big-held\nstand 61.5 30.1 62.5\nnote two lines\n", text);
        Assert.DoesNotContain("unknown", text);
    }

    [Theory]
    [InlineData(0, 0, false, true, "")]
    [InlineData(1, 0, false, false, "1 lander capsule(s) descending")]
    [InlineData(0, 2, false, false, "2 lander capsule(s) venting")]
    [InlineData(0, 0, true, false, "the body is still in a capsule")]
    [InlineData(1, 1, true, false, "1 lander capsule(s) descending, 1 lander capsule(s) venting, the body is still in a capsule")]
    public void A_world_is_landed_only_when_nothing_descends_or_vents_and_the_body_is_out(int descending, int venting, bool inCapsule, bool landed, string pending)
    {
        var landing = new Landing(descending, venting, inCapsule);
        Assert.Equal(landed, landing.Landed);
        Assert.Equal(pending, landing.Pending);
    }
}
