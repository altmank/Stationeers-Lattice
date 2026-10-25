using System.Collections.Generic;
using Lattice.Api;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// The cascade: per property, the last setter among enabled layers' matching rules in (band, order,
/// registration, position) order wins; unset properties take Lattice's default; Explain names the one rule.
/// </summary>
public sealed class CascadeTests
{
    private static readonly StyleMatch All = new StyleMatch.Everything();

    public static IEnumerable<object[]> Ties() => new[]
    {
        // name, first layer (band, order), second layer (band, order), which edge wins ("first"/"second")
        new object[] { "higher band wins whatever the order", StyleBand.Theme, 0, StyleBand.Meaning, 9, "first" },
        new object[] { "higher band added first still wins", StyleBand.Meaning, 0, StyleBand.Selection, 0, "second" },
        new object[] { "within a band the higher order wins", StyleBand.View, 5, StyleBand.View, 1, "first" },
        new object[] { "same band and order: the later registration wins", StyleBand.View, 2, StyleBand.View, 2, "second" },
        new object[] { "highlight band over selection", StyleBand.Highlight, 0, StyleBand.Selection, 0, "first" },
    };

    [Theory]
    [MemberData(nameof(Ties))]
    public void Order_alone_decides_ties(string name, StyleBand firstBand, int firstOrder, StyleBand secondBand, int secondOrder, string winner)
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("first", firstBand, firstOrder, Rule(All, Edge(LineStyle.Dashed, LibraryPalette.Fail))));
        bench.Add(LayerOf("second", secondBand, secondOrder, Rule(All, Edge(LineStyle.Dotted, LibraryPalette.Warn))));
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));

        var story = bench.Story(ghost.Id);

        var expected = winner == "first" ? new StyleSource.Layer(firstBand, Layer("first"), 0) : new StyleSource.Layer(secondBand, Layer("second"), 0);
        Assert.True(expected == story.Sources[StyleProperty.Edge], name);
    }

    [Fact]
    public void Within_a_layer_the_later_rule_wins_and_explain_names_its_position()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("one", StyleBand.Theme, 0,
            Rule(All, Edge(LineStyle.Dashed, LibraryPalette.Fail)),
            Rule(new StyleMatch.Intent(IntentKind.Build), Edge(LineStyle.Dotted, LibraryPalette.Warn)),
            Rule(new StyleMatch.Intent(IntentKind.Remove), Edge(LineStyle.Solid, LibraryPalette.Remove))));
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));

        var story = bench.Story(ghost.Id);

        Assert.Equal(new StyleSource.Layer(StyleBand.Theme, Layer("one"), 1), story.Sources[StyleProperty.Edge]);
        Assert.Equal(LineStyle.Dotted, Assert.IsType<EdgeLook.Line>(story.Resolved.Edge.Reduce(new EdgeLook.None())).Style);
    }

    [Fact]
    public void An_explicit_none_clears_a_value_set_lower_down()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("meaning", StyleBand.Meaning, 0, Rule(All, Edge(LineStyle.Dashed, LibraryPalette.Fail))));
        bench.Add(LayerOf("view", StyleBand.View, 0, Rule(All, new Style { Edge = Option.Some<EdgeLook>(new EdgeLook.None()) })));
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));

        var story = bench.Story(ghost.Id);

        Assert.IsType<EdgeLook.None>(story.Resolved.Edge.Reduce(new EdgeLook.Line(LineStyle.Solid, Must(Pixels.Create(1f)), new Paint.OwnerColour())));
        Assert.Equal(new StyleSource.Layer(StyleBand.View, Layer("view"), 0), story.Sources[StyleProperty.Edge]);
    }

    [Fact]
    public void Unset_properties_take_lattices_default_ghost_fill_planned_token_and_three_tenths()
    {
        var bench = new StyleBench();
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));

        var story = bench.Story(ghost.Id);

        Assert.Equal(FillMode.Ghost, story.Resolved.Fill.Reduce(FillMode.None));
        Assert.Equal(new Paint.Token(LibraryPalette.Planned), story.Resolved.FillPaint.Reduce(new Paint.OwnerColour()));
        Assert.Equal(0.3f, story.Resolved.FillOpacity.Reduce(Fraction.One).Value, 3);
        Assert.Equal(new StyleSource.Default(), story.Sources[StyleProperty.Fill]);
        foreach (StyleProperty property in System.Enum.GetValues(typeof(StyleProperty)))
            Assert.True(story.Sources.ContainsKey(property), $"{property} has a source");
    }

    [Fact]
    public void A_disabled_layer_does_not_apply_and_enabling_it_brings_it_back()
    {
        var bench = new StyleBench();
        var layer = bench.Add(LayerOf("fail", StyleBand.Theme, 0, Rule(All, Edge(LineStyle.Dashed, LibraryPalette.Fail))));
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));

        layer.Disable();
        Assert.Equal(new StyleSource.Default(), bench.Story(ghost.Id).Sources[StyleProperty.Edge]);
        layer.Enable();
        Assert.Equal(new StyleSource.Layer(StyleBand.Theme, Layer("fail"), 0), bench.Story(ghost.Id).Sources[StyleProperty.Edge]);
    }

    [Fact]
    public void Matches_combine_all_of_none_holds_any_of_none_does_not_and_not_inverts()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("combine", StyleBand.Theme, 0,
            Rule(new StyleMatch.All(ValueList<StyleMatch>.Empty), new Style { Pattern = Option.Some(Pattern.Hatch) }),
            Rule(new StyleMatch.Any(ValueList<StyleMatch>.Empty), new Style { Pattern = Option.Some(Pattern.Dots) }),
            Rule(new StyleMatch.Not(new StyleMatch.Draft()), new Style { Dim = Option.Some(Must(Fraction.Create(0.5f))) }),
            Rule(new StyleMatch.Not(new StyleMatch.Mine()), new Style { Dim = Option.Some(Must(Fraction.Create(0.2f))) })));
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));

        var resolved = bench.Story(ghost.Id).Resolved;

        Assert.Equal(Pattern.Hatch, resolved.Pattern.Reduce(Pattern.None));
        Assert.Equal(0.5f, resolved.Dim.Reduce(Fraction.One).Value, 3);
    }

    [Fact]
    public void Palettes_overlay_key_by_key_in_cascade_order_and_paints_turn_into_colours_after_the_cascade()
    {
        var bench = new StyleBench();
        var red = new Rgba(200, 0, 0, 255);
        var green = new Rgba(0, 200, 0, 255);
        var low = new Palette(Must(PaletteId.Create(Mod, "low")), ValueMap.Of(LibraryPalette.Fail, red).With(LibraryPalette.Planned, red));
        var high = new Palette(Must(PaletteId.Create(Mod, "high")), ValueMap.Of(LibraryPalette.Fail, green));
        bench.Add(LayerOf("high", StyleBand.View, 0) with { Palette = Option.Some(high) });
        bench.Add(LayerOf("low", StyleBand.Theme, 0, Rule(All, Edge(LineStyle.Solid, LibraryPalette.Fail))) with { Palette = Option.Some(low) });
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));

        var look = bench.Look(ghost.Id);

        Assert.Equal(green, look.Edge);
        Assert.Equal(red with { A = (byte)(255 * 0.3f + 0.5f) }, look.Fill);
    }

    [Fact]
    public void A_ghost_tint_paints_the_fill_and_a_ghost_without_one_takes_the_fallback()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("tint", StyleBand.Theme, 0, Rule(All, new Style
        {
            FillPaint = Option.Some<Paint>(new Paint.GhostTint(LibraryPalette.Warn)),
            FillOpacity = Option.Some(Fraction.One),
        })));
        var tinted = bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)) with { Tint = Option.Some(new Tint(1, 2, 3, 255)) }).Added[0];
        var plain = bench.PlaceOne("StructureFrame", new Cell(30, 10, 10));

        Assert.Equal(new Rgba(1, 2, 3, 255), bench.Look(tinted.Id).Fill);
        Assert.Equal(new Rgba(245, 200, 60, 255), bench.Look(plain.Id).Fill);
    }

    [Fact]
    public void Motion_is_the_last_enabled_layers_and_reduced_draws_effects_steady_with_eases_capped()
    {
        var bench = new StyleBench();
        var lively = new Style
        {
            Effect = Option.Some<EffectLook>(new EffectLook.Pulse(Must(Period.Create(1f)), Must(Fraction.Create(0.3f)))),
            Enter = Option.Some<EnterLook>(new EnterLook.Flash(Must(Repeats.Create(2)), Must(Period.Create(0.2f)))),
            Ease = Option.Some(Must(Duration.Create(0.5f))),
            Appear = Option.Some(Must(Duration.Create(0.05f))),
        };
        bench.Add(LayerOf("lively", StyleBand.Meaning, 0, Rule(All, lively)));
        var reduced = bench.Add(LayerOf("reduced", StyleBand.Theme, 0) with { Motion = Option.Some(Motion.Reduced) });
        var off = bench.Add(LayerOf("off", StyleBand.Theme, 1) with { Motion = Option.Some(Motion.Off), Enabled = false });
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));

        var calm = bench.Story(ghost.Id).Resolved;
        Assert.IsType<EffectLook.Steady>(calm.Effect.Reduce(new EffectLook.Steady()));
        Assert.IsType<EnterLook.None>(calm.Enter.Reduce(new EnterLook.None()));
        Assert.Equal(0.1f, calm.Ease.Reduce(Duration.Zero).Seconds, 3);
        Assert.Equal(0.05f, calm.Appear.Reduce(Duration.Zero).Seconds, 3);

        off.Enable();
        Assert.Equal(0f, bench.Story(ghost.Id).Resolved.Appear.Reduce(Duration.Zero).Seconds);

        off.Disable();
        reduced.Disable();
        Assert.IsType<EffectLook.Pulse>(bench.Story(ghost.Id).Resolved.Effect.Reduce(new EffectLook.Steady()));
    }

    [Fact]
    public void Others_ghosts_take_their_owner_colour_from_the_ramp_by_owner()
    {
        var bench = new StyleBench();
        var ramp = new Palette(Must(PaletteId.Create(Mod, "owners")), ValueMap<PaletteKey, Rgba>.Empty)
        {
            OwnerRamp = ValueList.From(new[] { new Rgba(10, 0, 0, 255), new Rgba(20, 0, 0, 255), new Rgba(30, 0, 0, 255) }),
        };
        bench.Add(LayerOf("others", StyleBand.Theme, 0, Rule(new StyleMatch.Not(new StyleMatch.Mine()), new Style
        {
            FillPaint = Option.Some<Paint>(new Paint.OwnerColour()),
            FillOpacity = Option.Some(Fraction.One),
        })) with { Palette = Option.Some(ramp) });
        var bob = Lattice.Tests.Board.TestBoard.Applied(bench.T.Apply(
            new Command.Place(ValueList.Of(bench.Draft("StructureFrame", new Cell(10, 10, 10)))), Lattice.Tests.Board.TestBoard.Bob)).Added[0];
        var mine = bench.PlaceOne("StructureFrame", new Cell(30, 10, 10));
        bench.Settle();

        Assert.Contains(bench.Look(bob.Id).Fill, ramp.OwnerRamp);
        Assert.NotEqual(bench.Look(bob.Id).Fill, bench.Look(mine.Id).Fill);
    }

    [Fact]
    public void Explain_refuses_a_ghost_the_viewer_is_not_shown()
    {
        var bench = new StyleBench();

        Assert.Equal(ReasonCodes.NotFound, bench.Styles.Explain(new GhostId(404)).Match(static _ => default, static why => why.Code));
    }

    [Fact]
    public void Explain_lists_the_facets_that_hold_as_atomic_matches()
    {
        var bench = new StyleBench();
        bench.Looks.Family("StructureFrame", PieceFamilies.Frame);
        bench.Add(LayerOf("reads", StyleBand.Theme, 0,
            Rule(new StyleMatch.Family(PieceFamilies.Frame), Style.Empty),
            Rule(new StyleMatch.State(StateKind.Planned), Style.Empty),
            Rule(new StyleMatch.Draft(), Style.Empty)));
        var ghost = bench.PlaceOne("StructureFrame", new Cell(10, 10, 10));

        var facets = bench.Story(ghost.Id).Facets;

        Assert.Contains(new StyleMatch.Family(PieceFamilies.Frame), facets);
        Assert.Contains(new StyleMatch.State(StateKind.Planned), facets);
        Assert.DoesNotContain(new StyleMatch.Draft(), facets);
    }
}
