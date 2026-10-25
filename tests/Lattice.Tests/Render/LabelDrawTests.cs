using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.Render.Labels;
using Lattice.TestKit.Allocations;
using Lattice.Tests.Styles;
using UnityEngine;
using static Lattice.Tests.Styles.StyleBench;
using Renderer = Lattice.Render.Renderer;

namespace Lattice.Tests.Render;

/// <summary>
/// Labels and glyph icons: Core's chosen labels draw from a fixed pool of
/// text objects, words and styles set only when they change; a label sits above its thing's footprint, eases in, and
/// its slot hides when it leaves; a glyph icon leads its words, a glyph the font lacks shows its plain stand-in; Render's
/// screen rectangles feed Core's overlap cull; a steady labelled frame allocates nothing.
/// </summary>
public sealed class LabelDrawTests
{
    private static readonly TextStyle Plain = new(null!, 24f, new Rgba(255, 255, 255, 255));

    private sealed class Rig
    {
        public StyleBench Bench { get; } = new();

        public RenderRig Render { get; } = new();

        public FakeLabelSurface Surface { get; } = new();

        public FakeLabelFacts Facts { get; } = new();

        public Renderer Renderer { get; private set; } = null!;

        public LabelDraw Labels => Renderer.Labels!;

        public Rig Open()
        {
            Renderer = RenderResults.Ok(Renderer.Over(Bench.Styles.Plan, Render.Ports, Bench.Scheduler, Bench.Styles.Watch, new LabelPorts(Surface, Facts, Plain, Bench.Clock)));
            Bench.Styles.Projector = Renderer.Labels;
            RenderRig.Settle(Bench, Renderer);
            return this;
        }

        /// <summary>A label choice from a camera, then Lattice's frame.</summary>
        public void Tick(WorldPoint camera)
        {
            Bench.Styles.View(camera);
            Bench.Clock.Advance(LabelChooser.Period);
            Bench.Frame();
            Renderer.Frame(0f);
        }

        public List<FakeLabelSlot> Visible => Surface.Slots.Where(static s => s.Visible).ToList();
    }

    private static Style Labelled(LabelText says, float appear = 0f, IconKey icon = default) => new()
    {
        Label = Option.Some<LabelLook>(new LabelLook.Shown(says, LibraryText.Default, LabelAnchor.Above, 0, Must(Metres.Create(500f)))),
        Appear = Option.Some(Must(Duration.Create(appear))),
        Icon = icon.Value.Length > 0 ? Option.Some<IconLook>(new IconLook.Shown(icon)) : Option<IconLook>.None,
    };

    private static Label Words(string text) => Must(Label.Create(text));

    [Fact]
    public void Shown_labels_draw_from_the_pool_above_their_thing_with_words_set_once()
    {
        var rig = new Rig();
        rig.Bench.Place(rig.Bench.Draft("StructureFrame", new Cell(10, 10, 10)), rig.Bench.Draft("StructureFrame", new Cell(50, 10, 10)));
        rig.Bench.Add(LayerOf("labels", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(new LabelText.Literal(Words("planned"))))));
        rig.Open();

        for (var i = 0; i < 5; i++)
            rig.Tick(new WorldPoint(0f, 1f, 1f));

        Assert.Equal(LabelDraw.Pool, rig.Surface.Slots.Count);
        var shown = rig.Visible;
        Assert.Equal(2, shown.Count);
        Assert.All(shown, static s => Assert.Equal("planned", s.Words));
        Assert.All(shown, static s => Assert.Equal(1, s.TextWrites));
        Assert.All(shown, static s => Assert.Same(Plain, s.Style));
        Assert.All(shown, static s => Assert.True(s.At.y > 1f, "above the frame's footprint"));
        Assert.Equal(new[] { 1f, 5f }, shown.Select(static s => (float)Math.Round(s.At.x)).OrderBy(static x => x).ToArray());
    }

    [Fact]
    public void A_step_number_is_rewritten_only_when_it_changes()
    {
        var rig = new Rig();
        var ghost = rig.Bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));
        rig.Bench.Add(LayerOf("steps", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(new LabelText.StepNumber()))));
        rig.Facts.Steps[ghost.Id] = 12;
        rig.Open();

        rig.Tick(new WorldPoint(0f, 1f, 1f));
        rig.Tick(new WorldPoint(0f, 1f, 1f));
        var slot = Assert.Single(rig.Visible);
        Assert.Equal("12", slot.Words);
        Assert.Equal(1, slot.TextWrites);

        rig.Facts.Steps[ghost.Id] = 3;
        rig.Tick(new WorldPoint(0f, 1f, 1f));
        Assert.Equal("3", slot.Words);
        Assert.Equal(2, slot.TextWrites);
    }

    [Fact]
    public void A_glyph_icon_leads_the_words_and_a_glyph_the_font_lacks_shows_its_plain_stand_in()
    {
        var rig = new Rig();
        rig.Bench.Place(rig.Bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        rig.Bench.Add(LayerOf("icons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(new LabelText.Literal(Words("wall")), icon: LibraryIcons.Fails))));
        rig.Open();
        rig.Tick(new WorldPoint(0f, 1f, 1f));
        Assert.Equal("! wall", Assert.Single(rig.Visible).Words);

        var lacking = new Rig();
        lacking.Surface.Missing.Add('⚠');
        lacking.Bench.Place(lacking.Bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        lacking.Bench.Add(LayerOf("icons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(new LabelText.Literal(Words("wall")), icon: LibraryIcons.Warns))));
        lacking.Open();
        lacking.Tick(new WorldPoint(0f, 1f, 1f));
        Assert.Equal("! wall", Assert.Single(lacking.Visible).Words);
    }

    [Fact]
    public void A_look_with_an_icon_and_no_label_shows_its_glyph_alone()
    {
        var rig = new Rig();
        rig.Bench.Place(rig.Bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        rig.Bench.Add(LayerOf("icons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), new Style { Icon = Option.Some<IconLook>(new IconLook.Shown(LibraryIcons.Remove)) })));
        rig.Open();

        rig.Tick(new WorldPoint(0f, 1f, 1f));

        Assert.Equal("×", Assert.Single(rig.Visible).Words);
    }

    [Fact]
    public void A_label_eases_in_over_its_appear_time()
    {
        var rig = new Rig();
        rig.Bench.Place(rig.Bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        rig.Bench.Add(LayerOf("labels", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(new LabelText.Literal(Words("planned")), appear: 1f))));
        rig.Open();

        rig.Tick(new WorldPoint(0f, 1f, 1f));
        var slot = Assert.Single(rig.Visible);
        var first = slot.Alpha;
        rig.Bench.Clock.Advance(TimeSpan.FromSeconds(0.5));
        rig.Renderer.Frame(0f);

        Assert.True(first < 0.5f, $"starts faint: {first}");
        Assert.InRange(slot.Alpha, first + 0.4f, first + 0.6f);
    }

    [Fact]
    public void A_label_that_leaves_hides_its_slot()
    {
        var rig = new Rig();
        rig.Bench.Place(rig.Bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        rig.Bench.Add(LayerOf("labels", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(new LabelText.Literal(Words("planned"))))));
        rig.Open();
        rig.Tick(new WorldPoint(0f, 1f, 1f));
        Assert.Single(rig.Visible);

        rig.Tick(new WorldPoint(5000f, 1f, 1f));
        rig.Tick(new WorldPoint(5000f, 1f, 1f));

        Assert.Empty(rig.Visible);
    }

    [Fact]
    public void Renders_screen_rectangles_cull_a_label_that_would_overlap_another()
    {
        var rig = new Rig();
        // The fake camera maps (x, z) to the screen, so two frames stacked in y land on one spot.
        rig.Bench.Place(rig.Bench.Draft("StructureFrame", new Cell(10, 10, 10)), rig.Bench.Draft("StructureFrame", new Cell(10, 30, 10)), rig.Bench.Draft("StructureFrame", new Cell(400, 10, 10)));
        rig.Bench.Add(LayerOf("labels", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(new LabelText.Literal(Words("planned"))))));
        rig.Open();

        rig.Tick(new WorldPoint(0f, 1f, 1f));

        Assert.Equal(2, rig.Visible.Count);
    }

    [Fact]
    public void A_text_style_with_no_size_or_a_key_already_registered_is_refused_and_a_registered_style_draws()
    {
        var rig = new Rig();
        rig.Bench.Place(rig.Bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        rig.Bench.Add(LayerOf("labels", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(new LabelText.Literal(Words("planned"))))));
        rig.Open();
        var planner = new TextStyle(null!, 30f, new Rgba(0, 0, 0, 255));

        Assert.Equal(ReasonCodes.InvalidValue, rig.Labels.Styles.Register(LibraryText.Default, planner with { Size = 0f }).Refusal().Code);
        RenderResults.Ok(rig.Labels.Styles.Register(LibraryText.Default, planner));
        Assert.Equal(ReasonCodes.AlreadyRegistered, rig.Labels.Styles.Register(LibraryText.Default, planner).Refusal().Code);
        rig.Tick(new WorldPoint(0f, 1f, 1f));

        Assert.Same(planner, Assert.Single(rig.Visible).Style);
    }

    [Fact]
    public void A_steady_labelled_frame_allocates_nothing()
    {
        var rig = new Rig();
        var drafts = new GhostDraft[60];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = rig.Bench.Draft("StructureFrame", new Cell(20 * i, 10, 10));
        var placed = rig.Bench.Place(drafts).Added;
        for (var i = 0; i < placed.Count; i++)
            rig.Facts.Steps[placed[i].Id] = i + 1;
        rig.Bench.Add(LayerOf("steps", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Labelled(new LabelText.StepNumber(), icon: LibraryIcons.Remove))));
        rig.Open();
        rig.Tick(new WorldPoint(0f, 1f, 1f));
        rig.Tick(new WorldPoint(0f, 1f, 1f));

        Allocations.AssertNone(() => rig.Renderer.Frame(0f), "a steady labelled frame");
        Assert.Contains(rig.Visible, static s => s.Words == "× 1");
    }
}
