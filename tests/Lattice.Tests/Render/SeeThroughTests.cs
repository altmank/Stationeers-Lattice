using System;
using System.Linq;
using Lattice.Api;
using Lattice.Render;
using Lattice.Render.Gpu;
using Lattice.Tests.Styles;
using UnityEngine;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Render;

/// <summary>
/// See-through: the outline shows at the look's strength and a face seen square-on at the square-on share of it,
/// about a third up to strength 0.5 and all of it at 1. The hidden part takes the look's own opacity when it has one,
/// so a look with no fill can show through.
/// </summary>
public sealed class SeeThroughTests
{
    private static SeeThroughLook.Through Through(float strength, float range) =>
        new(Must(Fraction.Create(strength)), Must(Metres.Create(range)));

    private static Fraction F(float value) => Must(Fraction.Create(value));

    /// <summary>A frame of one frame ghost drawn with the style over the library's.</summary>
    private static RenderRig Frame(Style style)
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Add(LayerOf("look", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), style)));
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var renderer = rig.Over(bench);
        rig.Gpu.NewFrame();
        renderer.BeforeCameras();
        renderer.Frame(0.5f);
        return rig;
    }

    private static DrawCall[] Layer(RenderRig rig, string key) =>
        rig.Gpu.Draws.Where(d => ((FakeMaterial)d.Material).Name.Split('@')[0] == key).ToArray();

    private static Style Seen(SeeThroughLook.Through through, float fillOpacity = 0.3f) => new()
    {
        Fill = Option.Some(FillMode.Ghost),
        FillOpacity = Option.Some(F(fillOpacity)),
        SeeThrough = Option.Some<SeeThroughLook>(through),
    };

    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]
    [InlineData(0.5f)]
    public void Up_to_half_strength_a_face_seen_square_on_shows_about_a_third(float strength)
    {
        Assert.Equal(0.35f, SeeThroughCurve.SquareOn(strength), 5);
    }

    [Theory]
    [InlineData(0.6f, 0.4176f)]
    [InlineData(0.8f, 0.7712f)]
    [InlineData(1f, 1f)]
    public void Above_half_strength_the_share_rises_to_all_of_it_at_full(float strength, float share)
    {
        Assert.Equal(share, SeeThroughCurve.SquareOn(strength), 3);
    }

    [Fact]
    public void The_share_never_falls_as_strength_rises()
    {
        var last = SeeThroughCurve.SquareOn(0f);
        for (var i = 1; i <= 100; i++)
        {
            var share = SeeThroughCurve.SquareOn(i / 100f);
            Assert.True(share >= last, $"SquareOn({i / 100f}) = {share} < {last}");
            last = share;
        }
    }

    [Theory]
    [InlineData(0.8f)]
    [InlineData(0.5f)]
    public void The_see_through_block_carries_strength_range_and_the_square_on_share(float strength)
    {
        var rig = Frame(Seen(Through(strength, 40f)));

        var see = Assert.Single(Layer(rig, "see-through"));
        Assert.Equal(new Vector4(strength, 40f, SeeThroughCurve.SquareOn(strength), 0f), see.Block[ShaderProp.SeeThrough]);
    }

    [Fact]
    public void At_half_strength_the_block_holds_todays_share()
    {
        var see = Assert.Single(Layer(Frame(Seen(Through(0.5f, 40f))), "see-through"));

        Assert.Equal(0.35f, see.Block[ShaderProp.SeeThrough].z, 5);
    }

    [Fact]
    public void The_looks_own_opacity_replaces_the_fills_in_the_fill_paint()
    {
        var rig = Frame(Seen(Through(1f, 40f) with { Opacity = Option.Some(F(0.7f)) }));

        var fill = Assert.Single(Layer(rig, "ghost")).Block[ShaderProp.Color];
        var see = Assert.Single(Layer(rig, "see-through")).Block[ShaderProp.Color];
        Assert.Equal(0.7f, see.w, 4);
        Assert.Equal((fill.x, fill.y, fill.z), (see.x, see.y, see.z));
    }

    [Fact]
    public void Dim_applies_to_the_looks_own_opacity()
    {
        var style = Seen(Through(1f, 40f) with { Opacity = Option.Some(F(0.7f)) }) with { Dim = Option.Some(F(0.5f)) };

        var see = Assert.Single(Layer(Frame(style), "see-through")).Block[ShaderProp.Color];

        Assert.Equal(0.35f, see.w, 4);
    }

    [Fact]
    public void Without_its_own_opacity_the_hidden_part_takes_the_fills()
    {
        var rig = Frame(Seen(Through(1f, 40f)));

        var fill = Assert.Single(Layer(rig, "ghost")).Block[ShaderProp.Color];
        var see = Assert.Single(Layer(rig, "see-through")).Block[ShaderProp.Color];
        Assert.Equal(fill, see);
    }

    private static Style EdgesOnly(SeeThroughLook.Through through) =>
        Edge(LineStyle.Solid, LibraryPalette.Fail) with
        {
            Fill = Option.Some(FillMode.None),
            SeeThrough = Option.Some<SeeThroughLook>(through),
        };

    [Fact]
    public void A_look_with_no_fill_shows_through_at_its_own_opacity()
    {
        var rig = Frame(EdgesOnly(Through(1f, 40f) with { Opacity = Option.Some(F(0.5f)) }));

        Assert.Empty(Layer(rig, "ghost"));
        var see = Assert.Single(Layer(rig, "see-through")).Block[ShaderProp.Color];
        Assert.Equal(0.5f, see.w, 4);
    }

    [Fact]
    public void A_look_with_no_fill_and_no_opacity_of_its_own_draws_no_see_through()
    {
        var rig = Frame(EdgesOnly(Through(1f, 40f)));

        Assert.NotEmpty(Layer(rig, "edge"));
        Assert.Empty(Layer(rig, "see-through"));
    }
}
