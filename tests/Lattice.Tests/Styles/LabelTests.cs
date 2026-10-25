using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.TestKit.Allocations;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// Label choice: nearest in range by priority, overlaps culled through the renderer's
/// rectangles, at most 48 shown; a shown label stays until 20 % past its range or 8 places past the cap; leavers fade
/// out, at most 16; chosen 10 times a second, allocating nothing.
/// </summary>
public sealed class LabelTests
{
    private static Style Labelled(int priority, float range, float vanish = 0f) => new()
    {
        Label = Option.Some<LabelLook>(new LabelLook.Shown(new LabelText.GhostLabel(), LibraryText.Default, LabelAnchor.Above, priority, Must(Metres.Create(range)))),
        Vanish = Option.Some(Must(Duration.Create(vanish))),
    };

    /// <summary>Frames in a row along x, 2 m apart, starting at x = 1 m.</summary>
    private static List<Ghost> Row(StyleBench bench, int count)
    {
        var drafts = new GhostDraft[count];
        for (var i = 0; i < count; i++)
            drafts[i] = bench.Draft("StructureFrame", new Cell(10 + 20 * i, 10, 10));
        var placed = bench.Place(drafts).Added;
        return new List<Ghost>(placed);
    }

    private static void Tick(StyleBench bench, WorldPoint camera)
    {
        bench.Styles.View(camera);
        bench.Clock.Advance(LabelChooser.Period);
        bench.Frame();
    }

    private static List<GhostId> Shown(StyleBench bench, bool fading = false)
    {
        var shown = new List<GhostId>();
        for (var i = 0; i < bench.Styles.Plan.LabelCount; i++)
        {
            var label = bench.Styles.Plan.LabelAt(i);
            if (label.Fading == fading)
                shown.Add(label.Thing.Id);
        }

        return shown;
    }

    [Fact]
    public void The_nearest_labels_in_range_show_higher_priority_first_up_to_the_cap()
    {
        var bench = new StyleBench();
        var ghosts = Row(bench, 120);
        bench.Add(LayerOf("labels", StyleBand.Theme, 0,
            Rule(new StyleMatch.Everything(), Labelled(0, 500f)),
            Rule(new StyleMatch.Picks(Lattice.Tests.Board.TestBoard.Ids(ghosts[119].Id)), Labelled(9, 500f))));
        bench.Settle();

        Tick(bench, new WorldPoint(0f, 1f, 1f));

        var shown = Shown(bench);
        Assert.Equal(LabelChooser.Cap, shown.Count);
        Assert.Contains(ghosts[119].Id, shown);
        Assert.Contains(ghosts[0].Id, shown);
        Assert.DoesNotContain(ghosts[100].Id, shown);
    }

    [Fact]
    public void A_shown_label_stays_until_twenty_percent_past_its_range()
    {
        var bench = new StyleBench();
        var ghost = Row(bench, 1)[0];
        bench.Add(LayerOf("labels", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(0, 10f))));
        bench.Settle();

        Tick(bench, new WorldPoint(1f + 11f, 1f, 1f));
        Assert.Empty(Shown(bench));
        Tick(bench, new WorldPoint(1f + 9f, 1f, 1f));
        Assert.Equal(new[] { ghost.Id }, Shown(bench));
        Tick(bench, new WorldPoint(1f + 11.5f, 1f, 1f));
        Assert.Equal(new[] { ghost.Id }, Shown(bench));
        Tick(bench, new WorldPoint(1f + 12.5f, 1f, 1f));
        Assert.Empty(Shown(bench));
    }

    [Fact]
    public void A_shown_label_stays_up_to_eight_places_past_the_cap()
    {
        var bench = new StyleBench();
        var ghosts = Row(bench, 70);
        bench.Add(LayerOf("labels", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(0, 500f))));
        bench.Settle();
        Tick(bench, new WorldPoint(1f + 2f * 47, 1f, 1f));
        var before = Shown(bench);

        Tick(bench, new WorldPoint(1f + 2f * 50, 1f, 1f));

        Assert.Equal(LabelChooser.Cap, Shown(bench).Count);
        Assert.Equal(before.Count, Shown(bench).Count);
        Assert.Equal(new HashSet<GhostId>(before), new HashSet<GhostId>(Shown(bench)));
    }

    [Fact]
    public void Overlapping_labels_are_culled_by_the_renderers_rectangles()
    {
        var bench = new StyleBench();
        Row(bench, 10);
        bench.Add(LayerOf("labels", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(0, 500f))));
        bench.Settle();
        var projector = new SameSpot();
        bench.Styles.Projector = projector;

        Tick(bench, new WorldPoint(0f, 1f, 1f));

        Assert.Single(Shown(bench));
        Assert.True(projector.Asked <= LabelChooser.Cap + LabelChooser.Slack);
    }

    [Fact]
    public void A_label_that_leaves_fades_out_for_its_vanish_time()
    {
        var bench = new StyleBench();
        var ghost = Row(bench, 1)[0];
        bench.Add(LayerOf("labels", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(0, 10f, vanish: 0.25f))));
        bench.Settle();
        Tick(bench, new WorldPoint(1f, 1f, 5f));
        Assert.Equal(new[] { ghost.Id }, Shown(bench));

        Tick(bench, new WorldPoint(1f, 1f, 100f));
        Assert.Empty(Shown(bench));
        Assert.Equal(new[] { ghost.Id }, Shown(bench, fading: true));
        Tick(bench, new WorldPoint(1f, 1f, 100f));
        Tick(bench, new WorldPoint(1f, 1f, 100f));
        Assert.Equal(new[] { ghost.Id }, Shown(bench, fading: true));
        Tick(bench, new WorldPoint(1f, 1f, 100f));
        Assert.Empty(Shown(bench, fading: true));
    }

    [Fact]
    public void Labels_are_chosen_ten_times_a_second()
    {
        var bench = new StyleBench();
        var ghost = Row(bench, 1)[0];
        bench.Add(LayerOf("labels", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(0, 10f))));
        bench.Settle();
        Tick(bench, new WorldPoint(1f, 1f, 50f));
        Assert.Empty(Shown(bench));

        bench.Styles.View(new WorldPoint(1f, 1f, 2f));
        bench.Clock.Advance(TimeSpan.FromMilliseconds(40));
        bench.Frame();
        Assert.Empty(Shown(bench));
        bench.Clock.Advance(TimeSpan.FromMilliseconds(70));
        bench.Frame();
        Assert.Equal(new[] { ghost.Id }, Shown(bench));
    }

    [Fact]
    public void Choosing_labels_on_an_idle_board_allocates_nothing()
    {
        var bench = new StyleBench();
        Row(bench, 80);
        bench.Add(LayerOf("labels", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(0, 40f, vanish: 0.2f))));
        bench.Settle();
        bench.Styles.Projector = new SameSpot();
        var x = 0f;

        Allocations.AssertNone(() =>
        {
            x = (x + 7f) % 160f;
            Tick(bench, new WorldPoint(x, 1f, 1f));
        }, "label choice");
    }

    private static Style Iconed(IconKey icon) => new() { Icon = Option.Some<IconLook>(new IconLook.Shown(icon)) };

    /// <summary>Frames in a 10 by 10 square on the floor, 2 m apart, from (1, 1, 1) m: all within 26 m of a corner.</summary>
    private static List<Ghost> Square(StyleBench bench)
    {
        var drafts = new GhostDraft[100];
        for (var i = 0; i < 100; i++)
            drafts[i] = bench.Draft("StructureFrame", new Cell(10 + 20 * (i % 10), 10, 10 + 20 * (i / 10)));
        return new List<Ghost>(bench.Place(drafts).Added);
    }

    private static LabelLook.Shown LabelOf(StyleBench bench, GhostId id)
    {
        for (var i = 0; i < bench.Styles.Plan.LabelCount; i++)
            if (bench.Styles.Plan.LabelAt(i).Thing.Id == id)
                return bench.Styles.Plan.LabelAt(i).Label;
        throw new InvalidOperationException("no label on " + id);
    }

    [Fact]
    public void An_icon_without_a_label_shows_as_a_label_that_says_nothing()
    {
        var bench = new StyleBench();
        var ghost = Row(bench, 1)[0];
        bench.Add(LayerOf("icons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Iconed(LibraryIcons.Remove))));
        bench.Settle();

        Tick(bench, new WorldPoint(1f, 1f, 5f));

        Assert.Equal(new[] { ghost.Id }, Shown(bench));
        var label = LabelOf(bench, ghost.Id);
        Assert.Equal("", Assert.IsType<LabelText.Literal>(label.Says).Words.Value);
        Assert.Equal(LibraryText.Default, label.Style);
        Assert.Equal(LabelAnchor.Above, label.Anchor);
        Assert.Equal(0, label.Priority);
        Assert.Equal(30f, label.Range.Value);
    }

    [Fact]
    public void An_icon_only_label_shows_within_thirty_metres()
    {
        var bench = new StyleBench();
        var ghost = Row(bench, 1)[0];
        bench.Add(LayerOf("icons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Iconed(LibraryIcons.Fails))));
        bench.Settle();

        Tick(bench, new WorldPoint(1f, 1f, 1f + 31f));
        Assert.Empty(Shown(bench));
        Tick(bench, new WorldPoint(1f, 1f, 1f + 29f));
        Assert.Equal(new[] { ghost.Id }, Shown(bench));
        Tick(bench, new WorldPoint(1f, 1f, 1f + 37f));
        Assert.Empty(Shown(bench));
    }

    [Fact]
    public void A_look_with_a_label_and_an_icon_shows_its_own_label()
    {
        var bench = new StyleBench();
        var ghost = Row(bench, 1)[0];
        bench.Add(LayerOf("both", StyleBand.Theme, 0,
            Rule(new StyleMatch.Everything(), Labelled(3, 500f) with { Icon = Option.Some<IconLook>(new IconLook.Shown(LibraryIcons.Warns)) })));
        bench.Settle();

        Tick(bench, new WorldPoint(1f, 1f, 100f));

        Assert.Equal(new[] { ghost.Id }, Shown(bench));
        Assert.Equal(3, LabelOf(bench, ghost.Id).Priority);
        Assert.IsType<LabelText.GhostLabel>(LabelOf(bench, ghost.Id).Says);
    }

    [Fact]
    public void A_look_with_neither_icon_nor_label_shows_nothing()
    {
        var bench = new StyleBench();
        Row(bench, 3);
        bench.Add(LayerOf("plain", StyleBand.Theme, 0,
            Rule(new StyleMatch.Everything(), new Style { Icon = Option.Some<IconLook>(new IconLook.None()) })));
        bench.Settle();

        Tick(bench, new WorldPoint(1f, 1f, 2f));

        Assert.Empty(Shown(bench));
    }

    [Fact]
    public void Icon_only_labels_share_the_cap_and_give_way_to_labels_of_higher_priority()
    {
        var bench = new StyleBench();
        var ghosts = Square(bench);
        var far = ghosts[99];
        bench.Add(LayerOf("icons", StyleBand.Theme, 0,
            Rule(new StyleMatch.Everything(), Iconed(LibraryIcons.Stale)),
            Rule(new StyleMatch.Picks(Lattice.Tests.Board.TestBoard.Ids(far.Id)), Labelled(1, 500f))));
        bench.Settle();

        Tick(bench, new WorldPoint(1f, 1f, 1f));

        var shown = Shown(bench);
        Assert.Equal(LabelChooser.Cap, shown.Count);
        Assert.Contains(far.Id, shown);
        Assert.Contains(ghosts[0].Id, shown);
        Assert.Equal(1, LabelOf(bench, far.Id).Priority);
    }

    [Fact]
    public void Overlapping_icon_only_labels_are_culled_like_labels()
    {
        var bench = new StyleBench();
        Row(bench, 10);
        bench.Add(LayerOf("icons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Iconed(LibraryIcons.Waiting))));
        bench.Settle();
        bench.Styles.Projector = new SameSpot();

        Tick(bench, new WorldPoint(0f, 1f, 1f));

        Assert.Single(Shown(bench));
    }

    [Fact]
    public void An_icon_on_a_modded_piece_shows()
    {
        var bench = new StyleBench();
        var modded = bench.PlaceOne(Modded, new Cell(5, 5, 5));
        bench.Add(LayerOf("icons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Iconed(LibraryIcons.Paused))));
        bench.Settle();

        Tick(bench, new WorldPoint(1f, 1f, 4f));

        Assert.Equal(new[] { modded.Id }, Shown(bench));
    }

    [Fact]
    public void Choosing_icon_only_labels_on_an_idle_board_allocates_nothing()
    {
        var bench = new StyleBench();
        Row(bench, 80);
        bench.Add(LayerOf("icons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Iconed(LibraryIcons.Swap))));
        bench.Settle();
        bench.Styles.Projector = new SameSpot();
        var x = 0f;

        Allocations.AssertNone(() =>
        {
            x = (x + 7f) % 160f;
            Tick(bench, new WorldPoint(x, 1f, 1f));
        }, "icon-only label choice");
    }

    private sealed class SameSpot : ILabelProjector
    {
        public int Asked { get; private set; }

        public bool Project(Thing thing, LabelLook.Shown label, out ScreenRect rect)
        {
            Asked++;
            rect = new ScreenRect(100f, 100f, 50f, 10f);
            return true;
        }
    }
}
