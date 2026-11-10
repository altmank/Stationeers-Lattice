using System.Collections.Generic;
using Lattice.Game.Live;

namespace Lattice.Tests.BigBase;

/// <summary>The pure parts of the live timing log: step labels, phase texts and the choice of slow frames.</summary>
public sealed class StepTimingFactsTests
{
    private sealed class Inner;

    private sealed class Outer
    {
        public sealed class Work;
    }

    private sealed class Generic<T>
    {
        public sealed class Step;
    }

    [Fact]
    public void A_top_level_type_is_labelled_by_its_name()
    {
        Assert.Equal("StepTimingFactsTests", StepLabel.Of(typeof(StepTimingFactsTests)));
    }

    [Fact]
    public void A_nested_type_is_labelled_outer_dot_inner()
    {
        Assert.Equal("StepTimingFactsTests.Inner", StepLabel.Of(typeof(Inner)));
        Assert.Equal("StepTimingFactsTests.Outer.Work", StepLabel.Of(typeof(Outer.Work)));
    }

    [Fact]
    public void A_generic_types_arity_suffix_is_dropped()
    {
        Assert.Equal("StepTimingFactsTests.Generic.Step", StepLabel.Of(typeof(Generic<int>.Step)));
        Assert.Equal("StepTimingFactsTests.Generic", StepLabel.Of(typeof(Generic<int>)));
    }

    [Fact]
    public void Each_safety_check_has_one_fixed_text()
    {
        for (var check = 0; check < 8; check++)
        {
            Assert.Equal($"safety:{check}", StepPhase.Safety(check));
            Assert.Same(StepPhase.Safety(check), StepPhase.Safety(check));
        }

        Assert.Equal("safety:?", StepPhase.Safety(8));
        Assert.Equal("safety:?", StepPhase.Safety(-1));
    }

    [Fact]
    public void Slow_frames_are_the_slowest_over_the_limit_first()
    {
        double[] ms = [1.0, 12.5, 6.0, 7.5, 25.0, 6.1];
        Assert.Equal<IEnumerable<int>>([4, 1, 3, 5], SlowFrames.Pick(ms, 10, 6.0));
    }

    [Fact]
    public void Slow_frames_stop_at_the_count_and_break_ties_by_order()
    {
        double[] ms = [9.0, 9.0, 3.0, 9.0];
        Assert.Equal<IEnumerable<int>>([0, 1], SlowFrames.Pick(ms, 2, 6.0));
    }

    [Fact]
    public void A_limit_of_zero_takes_every_frame_that_took_any_time()
    {
        double[] ms = [0.0, 0.5, 0.2];
        Assert.Equal<IEnumerable<int>>([1, 2], SlowFrames.Pick(ms, 5, 0.0));
        Assert.Empty(SlowFrames.Pick([], 5, 0.0));
    }
}
