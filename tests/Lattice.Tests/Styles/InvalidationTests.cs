using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;
using Lattice.TestKit.Allocations;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// What each change costs: set membership re-signatures only the things entering
/// and leaving, in the call; a toggle re-resolves live signatures only; a clip move re-signatures nothing; change sets,
/// verdicts and statuses are worked out in drawing-budget slices; a highlight costs its targets; leaves nobody uses are
/// retired and compacted away at the next generation.
/// </summary>
public sealed class InvalidationTests
{
    private static readonly StyleSetId Hovered = LibrarySets.Hovered;

    private static StyleLayerDescriptor HoverLayer() => LayerOf("hover", StyleBand.Selection, 0, Rule(new StyleMatch.InSet(Hovered), new Style
    {
        Glow = Option.Some<GlowLook>(new GlowLook.Lit(new Paint.Token(LibraryPalette.Select), Fraction.One)),
        Ease = Option.Some(Must(Duration.Create(0.15f))),
    }));

    /// <summary>Frames on a grid of large cells, one plan per 5,000.</summary>
    private static List<Ghost> Frames(StyleBench bench, int count)
    {
        var placed = new List<Ghost>(count);
        var drafts = new List<GhostDraft>(5_000);
        for (var i = 0; i < count; i++)
        {
            drafts.Add(bench.Draft("StructureFrame", new Cell(10 + 20 * (i % 50), 10 + 20 * (i / 2_500), 10 + 20 * (i / 50 % 50))));
            if (drafts.Count == 5_000 || i == count - 1)
            {
                placed.AddRange(bench.T.Placed(drafts.ToArray()).Added);
                drafts.Clear();
            }
        }

        bench.Settle();
        return placed;
    }

    [Fact]
    public void A_hover_put_re_signatures_the_ghost_entering_and_the_one_leaving_only()
    {
        var bench = new StyleBench();
        bench.Add(HoverLayer());
        var ghosts = Frames(bench, 200);
        var hover = Must(bench.Styles.Set(Hovered));
        var before = bench.Styles.Counters.Resignatures;

        hover.Put(ghosts[0].Id);
        Assert.Equal(1, bench.Styles.Counters.Resignatures - before);

        var resolves = bench.Styles.Resolves;
        hover.Put(ghosts[1].Id);
        Assert.Equal(3, bench.Styles.Counters.Resignatures - before);
        Assert.Equal(0, bench.Styles.Resolves - resolves);
        Assert.True(bench.Look(ghosts[1].Id).Glow.A > 0);
        Assert.Equal(0, bench.Look(ghosts[0].Id).Glow.A);
    }

    [Fact]
    public void Hover_put_and_clear_allocate_nothing()
    {
        var bench = new StyleBench();
        bench.Add(HoverLayer());
        var ghosts = Frames(bench, 50);
        var hover = Must(bench.Styles.Set(Hovered));
        var next = 0;

        Allocations.AssertNone(() =>
        {
            hover.Put(ghosts[next++ % ghosts.Count].Id);
            hover.Clear();
        }, "hover put and clear");
    }

    [Fact]
    public void An_idle_frame_allocates_nothing()
    {
        var bench = new StyleBench();
        bench.Add(HoverLayer());
        Frames(bench, 100);
        bench.Styles.View(new WorldPoint(0f, 0f, 0f));

        Allocations.AssertNone(bench.Frame, "an idle styles frame");
    }

    [Fact]
    public void A_toggle_re_resolves_each_live_signature_once_and_re_signatures_nothing()
    {
        var bench = new StyleBench();
        bench.Looks.Family("StructureFrame", PieceFamilies.Frame).Family("StructureWallIron", PieceFamilies.Wall);
        var layer = bench.Add(LayerOf("families", StyleBand.Theme, 0,
            Rule(new StyleMatch.Family(PieceFamilies.Frame), Edge(LineStyle.Solid, LibraryPalette.Next)),
            Rule(new StyleMatch.Family(PieceFamilies.Wall), Edge(LineStyle.Dashed, LibraryPalette.Warn))));
        var frames = Frames(bench, 30);
        var wall = bench.PlaceOne("StructureWallIron", new Cell(10, 400, 10));
        var cable = bench.PlaceOne("StructureCableStraight", new Cell(2, 402, 2));
        var signatures = bench.Styles.Newest.SignatureCount;
        var resignatures = bench.Styles.Counters.Resignatures;
        var resolves = bench.Styles.Resolves;

        layer.Disable();
        foreach (var ghost in frames)
            bench.Look(ghost.Id);
        bench.Look(wall.Id);
        bench.Look(cable.Id);

        Assert.Equal(3, signatures);
        Assert.Equal(signatures, bench.Styles.Resolves - resolves);
        Assert.Equal(resignatures, bench.Styles.Counters.Resignatures);
    }

    [Fact]
    public void A_clip_move_re_signatures_and_resolves_nothing()
    {
        var bench = new StyleBench();
        Frames(bench, 20);
        var clip = Must(bench.Styles.Clip(Must(ClipShape.Plane.Create(new WorldPoint(0f, 2f, 0f), new WorldVector(0f, 1f, 0f)))));
        var resignatures = bench.Styles.Counters.Resignatures;
        var resolves = bench.Styles.Resolves;

        for (var i = 0; i < 10; i++)
        {
            clip.Move(Must(ClipShape.Plane.Create(new WorldPoint(0f, 2f + i, 0f), new WorldVector(0f, 1f, 0f))));
            bench.Frame();
        }

        Assert.Equal(resignatures, bench.Styles.Counters.Resignatures);
        Assert.Equal(resolves, bench.Styles.Resolves);
    }

    [Fact]
    public void Adding_a_layer_re_signatures_every_thing_in_drawing_slices_while_the_old_looks_keep_drawing()
    {
        var bench = new StyleBench();
        var ghosts = Frames(bench, 2_000);
        bench.Clock.Step = TimeSpan.FromMilliseconds(0.05);
        var passed = bench.Styles.Counters.Passed;

        bench.Add(LayerOf("all", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Edge(LineStyle.Solid, LibraryPalette.Next))));
        bench.Frame();

        var first = bench.Styles.Counters.Passed - passed;
        Assert.InRange(first, 1, 1_999);
        Assert.True(bench.Scheduler.Spent(WorkKind.Drawing) <= TimeSpan.FromMilliseconds(2.1));
        Assert.True(bench.Styles.Draws(ghosts[^1].Id));
        bench.Settle();
        Assert.Equal(2_000, bench.Styles.Counters.Passed - passed);
        Assert.IsType<EdgeLook.Line>(bench.Story(ghosts[^1].Id).Resolved.Edge.Reduce(new EdgeLook.None()));
    }

    [Fact]
    public void A_highlight_re_signatures_its_three_targets_on_a_twenty_thousand_ghost_board_when_added_and_when_ended()
    {
        var bench = new StyleBench();
        var ghosts = Frames(bench, 20_000);
        var flash = new Style { Glow = Option.Some<GlowLook>(new GlowLook.Lit(new Paint.Token(LibraryPalette.Fail), Fraction.One)) };
        var targets = ValueList.From(new[] { ghosts[7].Id, ghosts[700].Id, ghosts[19_000].Id });
        var before = bench.Styles.Counters.Resignatures;

        var highlight = Must(bench.Styles.Highlight(new HighlightRequest(new HighlightTargets.Ghosts(targets), flash, Must(Duration.Create(5f)))));
        Assert.Equal(3, bench.Styles.Counters.Resignatures - before);
        Assert.Equal(new StyleSource.Highlight(), bench.Story(ghosts[700].Id).Sources[StyleProperty.Glow]);
        Assert.Equal(new StyleSource.Default(), bench.Story(ghosts[701].Id).Sources[StyleProperty.Glow]);

        highlight.Dispose();
        Assert.Equal(6, bench.Styles.Counters.Resignatures - before);
        Assert.False(bench.Styles.Busy);
        Assert.Equal(new StyleSource.Default(), bench.Story(ghosts[700].Id).Sources[StyleProperty.Glow]);
    }

    [Fact]
    public void A_highlight_ends_by_itself_after_its_time()
    {
        var bench = new StyleBench();
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        var flash = new Style { Dim = Option.Some(Must(Fraction.Create(0.5f))) };
        Must(bench.Styles.Highlight(new HighlightRequest(new HighlightTargets.Picked(new Selector.Everything()), flash, Must(Duration.Create(2f)))));
        Assert.Equal(new StyleSource.Highlight(), bench.Story(ghost.Id).Sources[StyleProperty.Dim]);

        bench.Clock.Advance(TimeSpan.FromSeconds(1));
        bench.Frame();
        Assert.Equal(new StyleSource.Highlight(), bench.Story(ghost.Id).Sources[StyleProperty.Dim]);
        bench.Clock.Advance(TimeSpan.FromSeconds(1.1));
        bench.Frame();
        Assert.Equal(new StyleSource.Default(), bench.Story(ghost.Id).Sources[StyleProperty.Dim]);
    }

    [Fact]
    public void A_five_thousand_ghost_change_set_is_worked_out_in_slices_not_in_the_changed_handler()
    {
        var bench = new StyleBench();
        bench.Clock.Step = TimeSpan.FromMilliseconds(0.05);
        var drafts = new GhostDraft[5_000];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = bench.Draft("StructureFrame", new Cell(10 + 20 * (i % 50), 10, 10 + 20 * (i / 50)));
        var placed = bench.T.Placed(drafts);
        var before = bench.Styles.Counters.Resignatures;

        bench.T.Events.Flush();
        Assert.Equal(before, bench.Styles.Counters.Resignatures);

        var frames = 0;
        while (bench.Styles.Busy)
        {
            bench.Frame();
            frames++;
            Assert.True(bench.Scheduler.Spent(WorkKind.Drawing) <= TimeSpan.FromMilliseconds(2.1), $"frame {frames} spent {bench.Scheduler.Spent(WorkKind.Drawing)}");
        }

        Assert.True(frames > 1);
        Assert.True(bench.Styles.Draws(placed.Added[^1].Id));
    }

    [Fact]
    public void Removed_ghosts_vanish_at_once_and_leave_their_sets()
    {
        var bench = new StyleBench();
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        var selected = Must(bench.Styles.Set(LibrarySets.Selected));
        selected.Replace(ValueList.Of(ghost.Id));

        bench.T.Apply(new Command.Remove(Lattice.Tests.Board.TestBoard.Ids(ghost.Id)));
        bench.T.Events.Flush();

        Assert.False(bench.Styles.Draws(ghost.Id));
        Assert.False(selected.Contains(ghost.Id));
    }

    [Fact]
    public void Replace_costs_the_ghosts_entering_and_leaving()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("selected", StyleBand.Selection, 0, Rule(new StyleMatch.InSet(LibrarySets.Selected), Edge(LineStyle.Solid, LibraryPalette.Select))));
        var ghosts = Frames(bench, 10);
        var selected = Must(bench.Styles.Set(LibrarySets.Selected));
        selected.Replace(ValueList.From(new[] { ghosts[0].Id, ghosts[1].Id }));
        var before = bench.Styles.Counters.Resignatures;

        selected.Replace(ValueList.From(new[] { ghosts[1].Id, ghosts[2].Id }));

        Assert.Equal(2, bench.Styles.Counters.Resignatures - before);
        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void A_verdict_change_re_signatures_that_ghost_only_in_the_next_slice()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("fails", StyleBand.Theme, 0, Rule(new StyleMatch.Finding(FindingKind.WillFail, Option<ReasonCode>.None), Edge(LineStyle.Dashed, LibraryPalette.Fail))));
        var ghosts = Frames(bench, 20);
        var before = bench.Styles.Counters.Resignatures;

        bench.Verdicts.Raise((ghosts[3].Id, new Finding.WillFail(ghosts[3].Id, Reason.Of(ReasonCodes.NotFound, Must(RuleId.Create(Mod, "rule"))))));
        Assert.Equal(before, bench.Styles.Counters.Resignatures);
        bench.Settle();

        Assert.Equal(1, bench.Styles.Counters.Resignatures - before);
        Assert.Contains(new StyleMatch.Finding(FindingKind.WillFail, Option<ReasonCode>.None), bench.Story(ghosts[3].Id).Facets);
    }

    [Fact]
    public void An_executor_status_re_signatures_that_ghost()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("working", StyleBand.Meaning, 0, Rule(new StyleMatch.Status(StatusKind.Working, Option<ExecutorId>.None), Edge(LineStyle.Solid, LibraryPalette.Next))));
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        var builder = new ExecutorInfo(Must(ExecutorId.Create(Mod, "builder")), Must(DisplayName.Create("Builder")));

        bench.Statuses.Set(ghost.Id, new ExecutorReport(builder, new ExecutorStatus.Working(1, 3)));
        bench.T.Events.RaiseStatusChanged(ghost.Id);
        bench.Settle();

        Assert.Equal(new StyleSource.Layer(StyleBand.Meaning, Layer("working"), 0), bench.Story(ghost.Id).Sources[StyleProperty.Edge]);
    }

    [Fact]
    public void Leaves_no_live_layer_or_highlight_uses_are_retired_and_compacted_at_the_next_generation()
    {
        var bench = new StyleBench();
        Frames(bench, 10);
        var rules = new StyleRule[100];
        for (var i = 0; i < rules.Length; i++)
            rules[i] = Rule(new StyleMatch.InSet(SetId("set-" + (char)('a' + i % 26) + "-" + (char)('a' + i / 26))), Style.Empty);
        var wide = bench.Add(LayerOf("wide", StyleBand.View, 0, rules));
        bench.Add(LayerOf("narrow", StyleBand.View, 1, Rule(new StyleMatch.Mine(), Style.Empty)));
        bench.Settle();
        Assert.Equal(101, bench.Styles.Newest.Leaves.Count);
        Assert.Equal(2, bench.Styles.Newest.Words);

        wide.Dispose();
        bench.Settle();

        Assert.Single(bench.Styles.Newest.Leaves);
        Assert.Equal(1, bench.Styles.Newest.Words);
    }

    [Fact]
    public void An_ended_highlights_leaf_is_gone_from_the_next_generation()
    {
        var bench = new StyleBench();
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        var highlight = Must(bench.Styles.Highlight(new HighlightRequest(new HighlightTargets.Ghosts(ValueList.Of(ghost.Id)), Style.Empty, Must(Duration.Create(1f)))));
        Assert.Single(bench.Styles.Newest.Leaves);

        highlight.Dispose();
        bench.Add(LayerOf("any", StyleBand.View, 0));
        bench.Settle();

        Assert.Empty(bench.Styles.Newest.Leaves);
    }

    [Fact]
    public void A_board_reset_works_every_ghost_out_again()
    {
        var bench = new StyleBench();
        var ghosts = Frames(bench, 30);
        var passed = bench.Styles.Counters.Passed;

        bench.T.Events.RaiseBoardReset();
        bench.Settle();

        Assert.True(bench.Styles.Counters.Passed - passed >= 30);
        Assert.True(bench.Styles.Draws(ghosts[29].Id));
    }
}
