using System;
using Lattice.Api;

namespace Lattice.Tests.Styles;

/// <summary>The style contract's value objects refuse invalid values with a result, and its records compare by content.</summary>
public sealed class StyleValueTests
{
    private static readonly ModId Mod = Ok(ModId.Create("mymod"));
    private static readonly WorldPoint Origin = new(0f, 0f, 0f);
    private static readonly WorldVector Up = new(0f, 1f, 0f);

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    public void A_fraction_outside_0_to_1_is_refused(float value) => Refused(Fraction.Create(value));

    [Fact]
    public void A_fraction_keeps_its_ends() =>
        Assert.Equal((0f, 1f), (Ok(Fraction.Create(0f)).Value, Ok(Fraction.Create(1f)).Value));

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Negative_or_non_finite_durations_and_distances_are_refused(float value)
    {
        Refused(Duration.Create(value));
        Refused(Metres.Create(value));
    }

    [Fact]
    public void Zero_is_a_duration_and_a_distance()
    {
        Assert.Equal(Duration.Zero, Ok(Duration.Create(0f)));
        Assert.Equal(0f, Ok(Metres.Create(0f)).Value);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-2f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void A_period_or_line_width_of_zero_or_less_is_refused(float value)
    {
        Refused(Period.Create(value));
        Refused(Pixels.Create(value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Repeats_below_one_are_refused(int count) => Refused(Repeats.Create(count));

    [Fact]
    public void A_fade_that_ends_before_it_starts_is_refused() =>
        Refused(FadeRange.Create(Ok(Metres.Create(10f)), Ok(Metres.Create(5f))));

    [Fact]
    public void A_fade_may_start_and_end_at_one_distance() =>
        Assert.Equal(5f, Ok(FadeRange.Create(Ok(Metres.Create(5f)), Ok(Metres.Create(5f)))).End.Value);

    [Fact]
    public void A_clip_plane_with_a_zero_or_non_finite_normal_is_refused()
    {
        Refused(ClipShape.Plane.Create(Origin, new WorldVector(0f, 0f, 0f)));
        Refused(ClipShape.Plane.Create(Origin, new WorldVector(float.NaN, 1f, 0f)));
        Refused(ClipShape.Plane.Create(new WorldPoint(float.PositiveInfinity, 0f, 0f), Up));
    }

    [Fact]
    public void A_clip_box_with_a_non_finite_corner_is_refused() =>
        Refused(ClipShape.Box.Create(Origin, new WorldPoint(1f, float.NaN, 1f)));

    [Fact]
    public void A_clip_box_takes_its_corners_in_any_order()
    {
        var box = Ok(ClipShape.Box.Create(new WorldPoint(4f, 0f, -1f), new WorldPoint(1f, 2f, 3f)));

        Assert.Equal((new WorldPoint(1f, 0f, -1f), new WorldPoint(4f, 2f, 3f)), (box.Min, box.Max));
        Assert.Equal(box, Ok(ClipShape.Box.Create(new WorldPoint(1f, 2f, 3f), new WorldPoint(4f, 0f, -1f))));
    }

    [Fact]
    public void A_pick_ray_needs_a_direction_and_a_reach()
    {
        Refused(PickRay.Create(Origin, new WorldVector(0f, 0f, 0f), 10f));
        Refused(PickRay.Create(Origin, Up, 0f));
        Refused(PickRay.Create(Origin, Up, float.PositiveInfinity));
        Refused(PickRay.Create(new WorldPoint(float.NaN, 0f, 0f), Up, 10f));
    }

    [Theory]
    [InlineData("Bad Key")]
    [InlineData("")]
    [InlineData("other/owner")]
    public void A_style_key_that_is_not_lowercase_words_is_refused(string name)
    {
        Refused(PaletteKey.Create(Mod, name));
        Refused(PaletteId.Create(Mod, name));
        Refused(MaterialKey.Create(Mod, name));
        Refused(StyleLayerId.Create(Mod, name));
        Refused(PieceFamily.Create(Mod, name));
    }

    [Fact]
    public void A_mod_style_key_carries_its_owner_and_lattice_keys_are_bare()
    {
        Assert.Equal("mymod/hazard", Ok(PaletteKey.Create(Mod, "hazard")).Value);
        Assert.Equal("mymod/x-ray", Ok(ClaimId.Create(Mod, "x-ray")).Value);
        Assert.Equal("fail", LibraryPalette.Fail.Value);
        Assert.Equal("mymod/night", Ok(PaletteId.Create(Mod, "night")).Value);
        Assert.Equal("colour-safe", LibraryPalettes.ColourSafe.Value);
        Assert.Equal("faded", MaterialKeys.Faded.Value);
    }

    [Fact]
    public void Styles_compare_by_their_opinions()
    {
        AssertEqualPair(FailingStyle(), FailingStyle());
        Assert.NotEqual(FailingStyle(), FailingStyle() with { Glow = Option.Some<GlowLook>(new GlowLook.None()) });
        Assert.Equal(Style.Empty, new Style());
    }

    [Fact]
    public void Layer_descriptors_compare_by_rules_and_palette()
    {
        AssertEqualPair(Layer(), Layer());
        Assert.NotEqual(Layer(), Layer() with { Rules = ValueList<StyleRule>.Empty });
        Assert.NotEqual(Layer(), Layer() with { Enabled = false });
    }

    [Fact]
    public void Palettes_compare_by_colours_and_ramp()
    {
        AssertEqualPair(Hazard(), Hazard());
        Assert.NotEqual(Hazard(), Hazard() with { OwnerRamp = ValueList.Of(new Rgba(1, 2, 3, 255)) });
    }

    [Fact]
    public void Composite_matches_compare_by_their_parts()
    {
        AssertEqualPair(Failing(), Failing());
        Assert.NotEqual(Failing(), new StyleMatch.All(ValueList.From(new StyleMatch[] { new StyleMatch.Mine(), new StyleMatch.Draft() })));
        Assert.NotEqual<StyleMatch>(Failing(), new StyleMatch.Any(Failing().Of));
    }

    [Fact]
    public void Style_stories_compare_by_sources_and_facets()
    {
        AssertEqualPair(Story(), Story());
        Assert.NotEqual(Story(), Story() with { Sources = ValueMap.Of<StyleProperty, StyleSource>(StyleProperty.Glow, new StyleSource.Default()) });
    }

    [Fact]
    public void Highlight_targets_compare_by_their_lists()
    {
        var piece = new StructureRef(new ReferenceId(5), new PrefabKey(10), new Cell(0, 0, 0));

        AssertEqualPair(new HighlightTargets.Ghosts(ValueList.From(new[] { new GhostId(1), new GhostId(2) })),
            new HighlightTargets.Ghosts(ValueList.From(new[] { new GhostId(1), new GhostId(2) })));
        AssertEqualPair(new HighlightTargets.Structures(ValueList.Of(piece)), new HighlightTargets.Structures(ValueList.Of(piece)));
        Assert.NotEqual(new HighlightTargets.Ghosts(ValueList.Of(new GhostId(1))), new HighlightTargets.Ghosts(ValueList.Of(new GhostId(2))));
    }

    private static StyleMatch.All Failing() =>
        new(ValueList.From(new StyleMatch[] { new StyleMatch.Mine(), new StyleMatch.Finding(FindingKind.WillFail, Option<ReasonCode>.None) }));

    private static Style FailingStyle() => new()
    {
        FillPaint = Option.Some<Paint>(new Paint.Token(LibraryPalette.Fail)),
        Glow = Option.Some<GlowLook>(new GlowLook.Lit(new Paint.Token(LibraryPalette.Fail), Ok(Fraction.Create(0.5f)))),
    };

    private static Palette Hazard() =>
        new(Ok(PaletteId.Create(Mod, "hazard")), ValueMap.Of(LibraryPalette.Fail, new Rgba(255, 0, 0, 255)));

    private static StyleLayerDescriptor Layer() =>
        new(Ok(StyleLayerId.Create(Mod, "failures")), StyleBand.Meaning, 0, ValueList.Of(new StyleRule(Failing(), FailingStyle())))
        {
            Palette = Option.Some(Hazard()),
        };

    private static StyleStory Story() =>
        new(new GhostId(7), FailingStyle(),
            ValueMap.Of<StyleProperty, StyleSource>(StyleProperty.Glow, new StyleSource.Layer(StyleBand.Meaning, Ok(StyleLayerId.Create(Mod, "failures")), 0)),
            ValueList.Of<StyleMatch>(Failing()));

    private static void AssertEqualPair<T>(T a, T b)
    {
        Assert.Equal(a, b);
        Assert.Equal(a!.GetHashCode(), b!.GetHashCode());
    }

    private static T Ok<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(v => v, r => throw new InvalidOperationException(r.Code.Value));

    private static void Refused<T>(Result<T, Reason> result) where T : notnull =>
        Assert.Equal(ReasonCodes.InvalidValue, result.Match(v => throw new InvalidOperationException($"accepted {v}"), r => r).Code);
}
