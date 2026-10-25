using System;
using System.Collections.Generic;
using System.Diagnostics;
using Lattice.Api;
using Lattice.TestKit.Allocations;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// The targets at 20,000 ghosts: a full resolve (every thing to a new generation) at most 40 ms in
/// all, a hover change at most 0.05 ms, a steady styles frame at most 1.0 ms, 0 B on an idle frame, and a hover pick
/// inside a dense 32 m chunk at most 0.05 ms. Wall-clock: run with <c>./build.ps1 -Perf</c> on an idle machine.
/// </summary>
[Trait(Perf.Trait, Perf.Value)]
public sealed class StylePerfTests
{
    private const int Ghosts = 20_000;

    /// <summary>A large base: frames, walls and cables on 20,000 ghosts across many chunks, with a library-like set of layers.</summary>
    private static (StyleBench Bench, List<Ghost> Placed) Base()
    {
        var bench = new StyleBench();
        bench.Looks.Family("StructureFrame", PieceFamilies.Frame).Family("StructureCableStraight", PieceFamilies.Cable).Family("StructureWallIron", PieceFamilies.Wall);
        bench.Add(LayerOf("library", StyleBand.Meaning, 0,
            Rule(new StyleMatch.State(StateKind.Stale), Edge(LineStyle.Dotted, LibraryPalette.Stale)),
            Rule(new StyleMatch.Finding(FindingKind.WillFail, Option<ReasonCode>.None), Edge(LineStyle.Dashed, LibraryPalette.Fail)),
            Rule(new StyleMatch.Finding(FindingKind.Warns, Option<ReasonCode>.None), Edge(LineStyle.Dashed, LibraryPalette.Warn)),
            Rule(new StyleMatch.Intent(IntentKind.Remove), Edge(LineStyle.Solid, LibraryPalette.Remove)),
            Rule(new StyleMatch.Family(PieceFamilies.Cable), new Style { FillPaint = Option.Some<Paint>(new Paint.GhostTint(LibraryPalette.Planned)) })));
        bench.Add(LayerOf("selection", StyleBand.Selection, 0,
            Rule(new StyleMatch.InSet(LibrarySets.Selected), Edge(LineStyle.Solid, LibraryPalette.Select)),
            Rule(new StyleMatch.InSet(LibrarySets.Hovered), new Style
            {
                Glow = Option.Some<GlowLook>(new GlowLook.Lit(new Paint.Token(LibraryPalette.Select), Fraction.One)),
                Ease = Option.Some(Must(Duration.Create(0.15f))),
            })));
        var placed = new List<Ghost>(Ghosts);
        var drafts = new List<GhostDraft>(5_000);
        for (var i = 0; i < Ghosts; i++)
        {
            var x = i % 100;
            var z = i / 100 % 100;
            var y = i / 10_000;
            drafts.Add(i % 3 == 0
                ? bench.Draft("StructureCableStraight", new Cell(5 + 5 * x, 5 + 40 * y, 5 + 5 * z))
                : bench.Draft("StructureFrame", new Cell(10 + 20 * x, 110 + 20 * y, 10 + 20 * z)));
            if (drafts.Count == 5_000 || i == Ghosts - 1)
            {
                placed.AddRange(bench.T.Placed(drafts.ToArray()).Added);
                drafts.Clear();
            }
        }

        bench.Settle(1_000_000);
        return (bench, placed);
    }

    [Fact]
    public void A_full_resolve_of_twenty_thousand_ghosts_takes_at_most_40_ms_in_all()
    {
        var (bench, _) = Base();
        bench.Add(LayerOf("warm", StyleBand.View, 0, Rule(new StyleMatch.Everything(), Style.Empty)));
        bench.Settle(1_000_000);

        var best = double.MaxValue;
        for (var round = 0; round < 3; round++)
        {
            bench.Add(LayerOf("theme-" + (char)('a' + round), StyleBand.Theme, 0, Rule(new StyleMatch.Tinted(), Edge(LineStyle.Solid, LibraryPalette.Next))));
            var watch = Stopwatch.StartNew();
            bench.Settle(1_000_000);
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }

        Assert.True(best <= 40.0, $"a full resolve took {best:F1} ms");
    }

    [Fact]
    public void A_hover_change_takes_at_most_five_hundredths_of_a_millisecond()
    {
        var (bench, placed) = Base();
        var hover = Must(bench.Styles.Set(LibrarySets.Hovered));
        for (var i = 0; i < 2_000; i++)
            hover.Put(placed[i % placed.Count].Id);

        const int Puts = 20_000;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Puts; i++)
            hover.Put(placed[(i * 7919) % placed.Count].Id);
        var each = watch.Elapsed.TotalMilliseconds / Puts;

        Assert.True(each <= 0.05, $"a hover change took {each:F4} ms");
    }

    [Fact]
    public void A_steady_frame_takes_at_most_one_millisecond_and_allocates_nothing()
    {
        var (bench, _) = Base();
        bench.Add(LayerOf("labels", StyleBand.Theme, 1, Rule(new StyleMatch.Family(PieceFamilies.Frame), new Style
        {
            Label = Option.Some<LabelLook>(new LabelLook.Shown(new LabelText.GhostLabel(), LibraryText.Default, LabelAnchor.Above, 0, Must(Metres.Create(30f)))),
        })));
        bench.Settle(1_000_000);
        bench.Clock.Step = TimeSpan.FromMilliseconds(2);
        bench.Styles.View(new WorldPoint(100f, 12f, 100f));
        for (var i = 0; i < 200; i++)
            bench.Frame();

        const int Frames = 500;
        var worst = 0.0;
        var total = Stopwatch.StartNew();
        for (var i = 0; i < Frames; i++)
        {
            var watch = Stopwatch.StartNew();
            bench.Frame();
            worst = Math.Max(worst, watch.Elapsed.TotalMilliseconds);
        }

        var mean = total.Elapsed.TotalMilliseconds / Frames;
        Assert.True(mean <= 1.0, $"a steady frame took {mean:F3} ms on average (worst {worst:F3} ms)");
        Allocations.AssertNone(bench.Frame, "a steady frame over 20,000 ghosts");
    }

    [Fact]
    public void A_hover_pick_inside_a_dense_chunk_takes_at_most_five_hundredths_of_a_millisecond()
    {
        var bench = new StyleBench();
        var drafts = new List<GhostDraft>(5_000);
        for (var i = 0; i < 16_384; i++)
        {
            var x = i % 64;
            var z = i / 64 % 64;
            var y = i / 4_096;
            drafts.Add(bench.Draft(y % 2 == 0 ? "StructureCableStraight" : "StructurePipeStraight", new Cell(5 + 5 * x, 5 + 5 * y, 5 + 5 * z)));
            if (drafts.Count == 5_000 || i == 16_383)
            {
                bench.T.Placed(drafts.ToArray());
                drafts.Clear();
            }
        }

        bench.Settle(1_000_000);
        var rays = new PickRay[64];
        for (var i = 0; i < rays.Length; i++)
            rays[i] = Must(PickRay.Create(new WorldPoint(3f + i * 0.4f, 6f, 1f + i * 0.3f), new WorldVector(0.3f, -0.4f, 1f), 40f));
        for (var i = 0; i < 1_000; i++)
            bench.Styles.Pick(rays[i % rays.Length], PickOptions.Default);

        const int Picks = 20_000;
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < Picks; i++)
            bench.Styles.Pick(rays[i % rays.Length], PickOptions.Default);
        var each = watch.Elapsed.TotalMilliseconds / Picks;

        Assert.True(each <= 0.05, $"a pick took {each:F4} ms");
    }
}
