using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.TestKit.Allocations;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// The viewer's clip: a stack where the last installed applies; moves cost
/// nothing; the change is raised at most once a frame, last in the styles' frame, from the subscribers' array, as a
/// plain call.
/// </summary>
public sealed class ClipTests
{
    private static ClipShape Floor(float y) => Must(ClipShape.Plane.Create(new WorldPoint(0f, y, 0f), new WorldVector(0f, 1f, 0f)));

    [Fact]
    public void The_last_installed_clip_applies_and_disposing_one_under_it_changes_nothing()
    {
        var bench = new StyleBench();
        var seen = new List<Option<ClipShape>>();
        bench.Styles.ClipChanged += seen.Add;
        var low = Must(bench.Styles.Clip(Floor(1f)));
        var high = Must(bench.Styles.Clip(Floor(2f)));
        bench.Frame();

        low.Dispose();
        bench.Frame();
        Assert.Equal(Option.Some(Floor(2f)), bench.Styles.InForce);

        high.Dispose();
        bench.Frame();
        Assert.Equal(new[] { Option.Some(Floor(2f)), Option<ClipShape>.None }, seen);
    }

    [Fact]
    public void Several_moves_in_one_frame_raise_one_change_with_the_last_shape()
    {
        var bench = new StyleBench();
        var seen = new List<Option<ClipShape>>();
        bench.Styles.ClipChanged += seen.Add;
        var clip = Must(bench.Styles.Clip(Floor(1f)));
        bench.Frame();
        seen.Clear();

        clip.Move(Floor(2f));
        clip.Move(Floor(3f));
        clip.Move(Floor(4f));
        bench.Frame();
        bench.Frame();

        Assert.Equal(new[] { Option.Some(Floor(4f)) }, seen);
    }

    [Fact]
    public void A_throwing_handler_leaves_only_the_raise_and_the_styles_frame_work_before_it_is_done()
    {
        var bench = new StyleBench();
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        Must(bench.Styles.Highlight(new HighlightRequest(new HighlightTargets.Ghosts(ValueList.Of(ghost.Id)),
            new Style { Dim = Option.Some(Must(Fraction.Create(0.5f))) }, Must(Duration.Create(1f)))));
        Action<Option<ClipShape>> boom = static _ => throw new InvalidOperationException("consumer");
        bench.Styles.ClipChanged += boom;
        Must(bench.Styles.Clip(Floor(1f)));
        bench.Clock.Advance(TimeSpan.FromSeconds(2));

        Assert.Throws<InvalidOperationException>(bench.Styles.Frame);

        Assert.Equal(new StyleSource.Default(), bench.Story(ghost.Id).Sources[StyleProperty.Dim]);
        bench.Styles.Frame();
    }

    [Fact]
    public void A_handler_may_unsubscribe_while_the_change_is_raised()
    {
        var bench = new StyleBench();
        var calls = 0;
        Action<Option<ClipShape>>? first = null;
        first = _ =>
        {
            calls++;
            bench.Styles.ClipChanged -= first!;
        };
        bench.Styles.ClipChanged += first;
        bench.Styles.ClipChanged += _ => calls++;

        var clip = Must(bench.Styles.Clip(Floor(1f)));
        bench.Frame();
        clip.Move(Floor(2f));
        bench.Frame();

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Moving_and_raising_allocate_nothing()
    {
        var bench = new StyleBench();
        var shapes = new[] { Floor(1f), Floor(2f) };
        var clip = Must(bench.Styles.Clip(shapes[0]));
        Action<Option<ClipShape>> quiet = static _ => { };
        bench.Styles.ClipChanged += quiet;
        var i = 0;

        Allocations.AssertNone(() =>
        {
            clip.Move(shapes[i++ & 1]);
            bench.Styles.Frame();
        }, "a clip move and its raise");
    }

    [Fact]
    public void Clip_installs_are_never_refused()
    {
        var bench = new StyleBench();
        var box = Must(ClipShape.Box.Create(new WorldPoint(0f, 0f, 0f), new WorldPoint(1f, 1f, 1f)));

        Assert.True(bench.Styles.Clip(box).TryGet(out _));
        Assert.Equal(Option.Some<ClipShape>(box), bench.Styles.InForce);
    }
}
