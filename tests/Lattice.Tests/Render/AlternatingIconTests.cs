using System;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.Render.Gpu;
using Lattice.Render.Materials;
using Lattice.Tests.Board;
using Lattice.Tests.Styles;
using Lattice.Tests.Styles.Library;
using UnityEngine;
using Renderer = Lattice.Render.Renderer;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Render;

/// <summary>
/// The alternating icon pattern (<see cref="IconPattern.Alternating"/>): both halves' icons and colours, the alternate
/// flag and the first half's size reach the fill's icon pattern copy; a single tiled icon submits none of them; a half
/// not registered draws clear and is logged once; the tile parity follows the documented checkerboard.
/// </summary>
public sealed class AlternatingIconTests
{
    private static readonly Rgba Teal = new(10, 120, 130, 255);
    private static readonly IconKey Spray = Must(IconKey.Create(Mod, "spray-can"));
    private static readonly IconKey Tag = Must(IconKey.Create(Mod, "name-tag"));
    private static readonly Cell At = new(10, 10, 10);

    private static IconPattern.Tiled Tiled(IconKey icon, Paint paint, float? size = null) =>
        new(icon, paint) { Size = size is { } s ? Option.Some(Must(Metres.Create(s))) : Option<Metres>.None };

    private static IconPattern.Alternating SprayAndTag(float? firstSize = 0.75f, float? secondSize = 2f) => new(
        Tiled(Spray, new Paint.FinishColour(LibraryPalette.Planned), firstSize),
        Tiled(Tag, new Paint.Token(LibraryPalette.Fail), secondSize));

    private sealed class Paints(int index, Rgba colour) : IPaintColours
    {
        public Option<Rgba> Colour(PaintColour paint) => paint.Index == index ? Option.Some(colour) : Option<Rgba>.None;
    }

    private sealed record Scene(StyleBench Bench, RenderRig Rig, Renderer Renderer, GhostId Ghost)
    {
        public void Frame()
        {
            RenderRig.Settle(Bench, Renderer);
            Rig.Gpu.NewFrame();
            Renderer.Frame(0.1f);
        }

        public DrawCall Fill => Rig.Gpu.Draws.First(static d => d.Mesh is FakeMesh { Name: "cube" });
    }

    private static Scene Drawn(IconPattern pattern, FakeTexture? spray = null, FakeTexture? tag = null)
    {
        var bench = new StyleBench(true, null, new Paints(2, Teal));
        var rig = new RenderRig();
        bench.Add(LayerOf("icons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), new Style { PatternIcon = Option.Some(pattern) })));
        var placement = new Placement(bench.T.Catalogue.Named("StructureFrame").Key, At, default);
        var ghost = bench.Place(TestBoard.Draft(new Intent.Build(placement, Option.Some(LibraryResolveTests.Painted(2))))).Added[0].Id;
        var renderer = rig.Over(bench);
        if (spray is not null)
            RenderResults.Ok(renderer.Materials.Icons.Register(Spray, spray));
        if (tag is not null)
            RenderResults.Ok(renderer.Materials.Icons.Register(Tag, tag));
        var scene = new Scene(bench, rig, renderer, ghost);
        scene.Frame();
        return scene;
    }

    private static FakeMaterial Material(DrawCall draw) => (FakeMaterial)draw.Material;

    private static Vector4 Colour(Rgba c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    [Fact]
    public void Alternating_reaches_the_icon_material_with_both_textures_and_colours_the_flag_and_the_first_size()
    {
        var spray = new FakeTexture("spray");
        var tag = new FakeTexture("tag");
        var scene = Drawn(SprayAndTag(), spray, tag);

        var fill = scene.Fill;
        var look = scene.Bench.Look(scene.Ghost);
        Assert.True(Material(fill).IconPatterned);
        Assert.Same(spray, fill.Block.Textures[(int)ShaderProp.PatternIcon]);
        Assert.Same(tag, fill.Block.Textures[(int)ShaderProp.PatternIcon2]);
        Assert.Equal(Colour(Teal), fill.Block[ShaderProp.PatternColor]);
        Assert.Equal(Colour(look.PatternIcon2), fill.Block[ShaderProp.PatternColor2]);
        Assert.NotEqual(default, look.PatternIcon2);
        Assert.NotEqual(Colour(Teal), fill.Block[ShaderProp.PatternColor2]);
        Assert.Equal(new Vector4(0.75f, 0f, 0f, 0f), fill.Block[ShaderProp.PatternIconSize]);
        Assert.Equal(new Vector4(1f, 0f, 0f, 0f), fill.Block[ShaderProp.PatternAlternate]);
        Assert.True(fill.Block.AsFloat[(int)ShaderProp.PatternAlternate]);
        Assert.Equal(Vector4.zero, fill.Block[ShaderProp.Pattern]);
    }

    [Fact]
    public void The_first_halfs_default_size_draws_whatever_the_second_asks()
    {
        var scene = Drawn(SprayAndTag(firstSize: null, secondSize: 2f), new FakeTexture("spray"), new FakeTexture("tag"));

        Assert.Equal(new Vector4(0.5f, 0f, 0f, 0f), scene.Fill.Block[ShaderProp.PatternIconSize]);
    }

    [Fact]
    public void A_single_tiled_icon_submits_only_the_single_icon_values()
    {
        var spray = new FakeTexture("spray");
        var scene = Drawn(Tiled(Spray, new Paint.FinishColour(LibraryPalette.Planned), 0.75f), spray, new FakeTexture("tag"));

        var fill = scene.Fill;
        Assert.True(Material(fill).IconPatterned);
        Assert.Same(spray, fill.Block.Textures[(int)ShaderProp.PatternIcon]);
        Assert.Equal(new Vector4(0.75f, 0f, 0f, 0f), fill.Block[ShaderProp.PatternIconSize]);
        Assert.Equal(Colour(Teal), fill.Block[ShaderProp.PatternColor]);
        Assert.Equal(Vector4.zero, fill.Block[ShaderProp.Pattern]);
        Assert.False(fill.Block.IsSet[(int)ShaderProp.PatternIcon2]);
        Assert.False(fill.Block.IsSet[(int)ShaderProp.PatternColor2]);
        Assert.False(fill.Block.IsSet[(int)ShaderProp.PatternAlternate]);
        Assert.Equal(default, scene.Bench.Look(scene.Ghost).PatternIcon2);
    }

    [Fact]
    public void An_unregistered_second_half_draws_clear_in_its_tiles_and_is_logged_once()
    {
        var spray = new FakeTexture("spray");
        var scene = Drawn(SprayAndTag(), spray);
        scene.Frame();

        var fill = scene.Fill;
        Assert.True(Material(fill).IconPatterned);
        Assert.Equal(Colour(Teal), fill.Block[ShaderProp.PatternColor]);
        Assert.Equal(Vector4.zero, fill.Block[ShaderProp.PatternColor2]);
        Assert.Same(spray, fill.Block.Textures[(int)ShaderProp.PatternIcon2]);
        Assert.Equal(new Vector4(1f, 0f, 0f, 0f), fill.Block[ShaderProp.PatternAlternate]);
        Assert.Single(scene.Rig.Log, static line => line.Contains("name-tag", StringComparison.Ordinal));
        Assert.DoesNotContain(scene.Rig.Log, static line => line.Contains("spray-can", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unregistered_first_half_draws_clear_in_its_tiles_and_keeps_its_size()
    {
        var tag = new FakeTexture("tag");
        var scene = Drawn(SprayAndTag(), tag: tag);

        var fill = scene.Fill;
        Assert.True(Material(fill).IconPatterned);
        Assert.Equal(Vector4.zero, fill.Block[ShaderProp.PatternColor]);
        Assert.Same(tag, fill.Block.Textures[(int)ShaderProp.PatternIcon]);
        Assert.Same(tag, fill.Block.Textures[(int)ShaderProp.PatternIcon2]);
        Assert.NotEqual(Vector4.zero, fill.Block[ShaderProp.PatternColor2]);
        Assert.Equal(new Vector4(0.75f, 0f, 0f, 0f), fill.Block[ShaderProp.PatternIconSize]);
        Assert.Single(scene.Rig.Log, static line => line.Contains("spray-can", StringComparison.Ordinal));
    }

    [Fact]
    public void With_neither_half_registered_no_pattern_is_drawn_and_each_is_logged_once()
    {
        var scene = Drawn(SprayAndTag());
        scene.Frame();

        var fill = scene.Fill;
        Assert.False(Material(fill).IconPatterned);
        Assert.Equal(Vector4.zero, fill.Block[ShaderProp.Pattern]);
        Assert.False(fill.Block.IsSet[(int)ShaderProp.PatternIcon]);
        Assert.False(fill.Block.IsSet[(int)ShaderProp.PatternAlternate]);
        Assert.Equal(0, scene.Renderer.Materials.IconPatternCopies);
        Assert.Single(scene.Rig.Log, static line => line.Contains("spray-can", StringComparison.Ordinal));
        Assert.Single(scene.Rig.Log, static line => line.Contains("name-tag", StringComparison.Ordinal));
    }

    [Fact]
    public void A_second_half_registered_later_draws_from_the_next_frame()
    {
        var scene = Drawn(SprayAndTag(), new FakeTexture("spray"));
        Assert.Equal(Vector4.zero, scene.Fill.Block[ShaderProp.PatternColor2]);

        var tag = new FakeTexture("tag");
        RenderResults.Ok(scene.Renderer.Materials.Icons.Register(Tag, tag));
        scene.Frame();

        Assert.Same(tag, scene.Fill.Block.Textures[(int)ShaderProp.PatternIcon2]);
        Assert.NotEqual(Vector4.zero, scene.Fill.Block[ShaderProp.PatternColor2]);
    }

    [Fact]
    public void An_alternating_pattern_deconstructs_and_compares_by_value()
    {
        var (first, second) = SprayAndTag();

        Assert.Equal(Spray, first.Icon);
        Assert.Equal(Tag, second.Icon);
        Assert.Equal(SprayAndTag(), SprayAndTag());
        Assert.NotEqual(SprayAndTag(), new IconPattern.Alternating(second, first));
    }

    [Fact]
    public void Tile_i_j_draws_the_second_half_exactly_where_i_plus_j_is_odd()
    {
        for (var i = -7; i <= 7; i++)
        for (var j = -7; j <= 7; j++)
        {
            Assert.Equal(((i + j) % 2 + 2) % 2 == 1, IconTiles.Second(i, j));
            Assert.NotEqual(IconTiles.Second(i, j), IconTiles.Second(i + 1, j));
            Assert.NotEqual(IconTiles.Second(i, j), IconTiles.Second(i, j + 1));
        }
    }

    [Theory]
    [InlineData(0.25f, 0.25f, 0, 0, false)]
    [InlineData(0.75f, 0.25f, 1, 0, true)]
    [InlineData(0.75f, 0.75f, 1, 1, false)]
    [InlineData(-0.25f, 0.25f, -1, 0, true)]
    [InlineData(-0.25f, -0.25f, -1, -1, false)]
    [InlineData(1.25f, -0.75f, 2, -2, false)]
    public void A_top_face_point_falls_in_its_tile_by_floor_of_x_and_z_over_the_size(float x, float z, int i, int j, bool second)
    {
        var tile = IconTiles.Tile(IconTiles.Uv(new Vector3(x, 3.1f, z), Vector3.up, 0.5f));

        Assert.Equal((i, j), tile);
        Assert.Equal(second, IconTiles.Second(tile.I, tile.J));
    }

    [Fact]
    public void A_wall_tiles_on_its_horizontal_axis_and_height()
    {
        Assert.Equal((1, 2), IconTiles.Tile(IconTiles.Uv(new Vector3(9f, 1.2f, 0.7f), Vector3.right, 0.5f)));
        Assert.Equal((-2, 2), IconTiles.Tile(IconTiles.Uv(new Vector3(9f, 1.2f, 0.7f), Vector3.left, 0.5f)));
        Assert.Equal((-2, 2), IconTiles.Tile(IconTiles.Uv(new Vector3(0.7f, 1.2f, 9f), Vector3.forward, 0.5f)));
        Assert.Equal((1, 2), IconTiles.Tile(IconTiles.Uv(new Vector3(0.7f, 1.2f, 9f), Vector3.back, 0.5f)));
    }

    [Fact]
    public void The_shader_alternates_by_the_same_parity_and_samples_one_texture_behind_a_branch()
    {
        var common = Shader("LatticeCommon.cginc");

        Assert.Contains("float2 tile = floor(uv);", common);
        Assert.Contains("frac((tile.x + tile.y) * 0.5) > 0.25", common);
        Assert.Contains("[branch] if (second)", common);
        foreach (var file in new[] { "LatticeGhost.shader", "LatticeSolid.shader", "LatticeGhostEdged.shader" })
        {
            var source = Shader(file);
            Assert.Contains("_LatticePatternIcon2 (", source);
            Assert.Contains("_LatticePatternColor2 (", source);
            Assert.Contains("_LatticePatternAlternate (\"Icon pattern alternates\", Float) = 0", source);
        }
    }

    private static string Shader(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Lattice.Render", "Shaders", file));
    }
}
