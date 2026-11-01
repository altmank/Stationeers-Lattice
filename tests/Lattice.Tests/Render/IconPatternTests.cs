using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.Render.Gpu;
using Lattice.Tests.Board;
using Lattice.Tests.Styles;
using Lattice.Tests.Styles.Library;
using UnityEngine;
using Renderer = Lattice.Render.Renderer;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Render;

/// <summary>
/// The icon pattern (<see cref="Style.PatternIcon"/>): a registered icon reaches the fill's icon pattern copy with its
/// texture, tile size and paint (a finish's colour or its fallback); a look without one draws exactly as before; an icon
/// not registered, or an override fill material without the keyword, draws no pattern and is logged once.
/// </summary>
public sealed class IconPatternTests
{
    private static readonly Rgba Teal = new(10, 120, 130, 255);
    private static readonly IconKey Spray = Must(IconKey.Create(Mod, "spray-can"));
    private static readonly Cell At = new(10, 10, 10);

    private static IconPattern.Tiled Tiled(float? size = null) =>
        new(Spray, new Paint.FinishColour(LibraryPalette.Planned)) { Size = size is { } s ? Option.Some(Must(Metres.Create(s))) : Option<Metres>.None };

    private static Style Patterned(IconPattern pattern, bool edge = false) => new()
    {
        PatternIcon = Option.Some(pattern),
        Edge = edge ? Option.Some<EdgeLook>(new EdgeLook.Line(LineStyle.Solid, Must(Pixels.Create(2f)), new Paint.Token(LibraryPalette.Fail))) : Option<EdgeLook>.None,
    };

    private sealed class Paints(int index, Rgba colour) : IPaintColours
    {
        public Option<Rgba> Colour(PaintColour paint) => paint.Index == index ? Option.Some(colour) : Option<Rgba>.None;
    }

    private sealed class Scene
    {
        public required StyleBench Bench { get; init; }

        public required RenderRig Rig { get; init; }

        public required Renderer Renderer { get; init; }

        public required GhostId Ghost { get; init; }

        public void Frame()
        {
            RenderRig.Settle(Bench, Renderer);
            Rig.Gpu.NewFrame();
            Renderer.Frame(0.1f);
        }

        /// <summary>The first piece's fill draw.</summary>
        public DrawCall Fill => Rig.Gpu.Draws.First(static d => d.Mesh is FakeMesh { Name: "cube" });
    }

    /// <summary>A ghost of <paramref name="piece"/> asking for finish paint 2, under one layer of <paramref name="style"/>.</summary>
    private static Scene Drawn(Style style, IPaintColours? paints = null, Action<Renderer>? before = null, string piece = "StructureFrame", FakeTexture? icon = null)
    {
        var bench = new StyleBench(true, null, paints);
        var rig = new RenderRig();
        bench.Add(LayerOf("icons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), style)));
        var placement = new Placement(bench.T.Catalogue.Named(piece).Key, At, default);
        var ghost = bench.Place(TestBoard.Draft(new Intent.Build(placement, Option.Some(LibraryResolveTests.Painted(2))))).Added[0].Id;
        var renderer = rig.Over(bench);
        if (icon is not null)
            RenderResults.Ok(renderer.Materials.Icons.Register(Spray, icon));
        before?.Invoke(renderer);
        var scene = new Scene { Bench = bench, Rig = rig, Renderer = renderer, Ghost = ghost };
        scene.Frame();
        return scene;
    }

    private static FakeMaterial Material(DrawCall draw) => (FakeMaterial)draw.Material;

    private static string BaseName(FakeMaterial material)
    {
        var at = material;
        while (at.CopyOf is { } of)
            at = of;
        return at.Name;
    }

    private static Vector4 Colour(Rgba c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    [Fact]
    public void A_registered_icon_reaches_the_fills_icon_material_with_its_texture_size_and_finish_colour()
    {
        var icon = new FakeTexture("spray");
        var scene = Drawn(Patterned(Tiled(0.75f)), new Paints(2, Teal), icon: icon);

        var fill = scene.Fill;
        Assert.True(Material(fill).IconPatterned);
        Assert.Equal("ghost", BaseName(Material(fill)));
        Assert.Same(icon, fill.Block.Textures[(int)ShaderProp.PatternIcon]);
        Assert.Equal(new Vector4(0.75f, 0f, 0f, 0f), fill.Block[ShaderProp.PatternIconSize]);
        Assert.True(fill.Block.AsFloat[(int)ShaderProp.PatternIconSize]);
        Assert.Equal(Colour(Teal), fill.Block[ShaderProp.PatternColor]);
        Assert.Equal(Vector4.zero, fill.Block[ShaderProp.Pattern]);
    }

    [Fact]
    public void Without_the_games_paint_colour_the_pattern_takes_the_finish_paints_fallback()
    {
        var scene = Drawn(Patterned(Tiled()), icon: new FakeTexture("spray"));

        var planned = scene.Bench.Look(scene.Ghost).Fill;
        var colour = scene.Fill.Block[ShaderProp.PatternColor];
        Assert.Equal((planned.R / 255f, planned.G / 255f, planned.B / 255f), (colour.x, colour.y, colour.z));
        Assert.Equal(1f, colour.w);
        Assert.NotEqual(Colour(Teal), colour);
    }

    [Fact]
    public void A_tile_size_of_none_or_zero_draws_half_a_metre()
    {
        Assert.Equal(0.5f, Tiled().SizeOrDefault.Value);
        Assert.Equal(0.5f, Tiled(0f).SizeOrDefault.Value);
        Assert.Equal(0.5f, IconPattern.Tiled.DefaultSize.Value);
        Assert.Equal(new Vector4(0.5f, 0f, 0f, 0f), Drawn(Patterned(Tiled()), icon: new FakeTexture("spray")).Fill.Block[ShaderProp.PatternIconSize]);
    }

    [Fact]
    public void A_modded_piece_shows_the_icon_pattern_too()
    {
        var scene = Drawn(Patterned(Tiled()), new Paints(2, Teal), piece: Modded, icon: new FakeTexture("spray"));

        Assert.True(Material(scene.Fill).IconPatterned);
        Assert.Equal(Colour(Teal), scene.Fill.Block[ShaderProp.PatternColor]);
    }

    [Fact]
    public void With_an_edge_the_one_fill_and_edge_material_draws_the_icon_pattern()
    {
        var scene = Drawn(Patterned(Tiled(), edge: true), icon: new FakeTexture("spray"));

        var draw = Assert.Single(scene.Rig.Gpu.Draws);
        Assert.True(Material(draw).IconPatterned);
        Assert.Equal("ghost-edged", BaseName(Material(draw)));
    }

    [Fact]
    public void A_look_without_an_icon_pattern_compares_and_submits_as_before()
    {
        var hatch = new Style { Pattern = Option.Some(Pattern.Hatch) };
        Assert.Equal(new Style { Pattern = Option.Some(Pattern.Hatch) }, hatch);
        Assert.Equal(new Style { Pattern = Option.Some(Pattern.Hatch) }.GetHashCode(), hatch.GetHashCode());
        Assert.Equal(Option<IconPattern>.None, hatch.PatternIcon);

        var scene = Drawn(hatch, icon: new FakeTexture("spray"));

        var fill = Assert.Single(scene.Rig.Gpu.Draws);
        Assert.False(Material(fill).IconPatterned);
        Assert.Equal("ghost", BaseName(Material(fill)));
        Assert.Equal(new Vector4(1f, 0f, 0f, 0f), fill.Block[ShaderProp.Pattern]);
        Assert.False(fill.Block.IsSet[(int)ShaderProp.PatternIcon]);
        Assert.False(fill.Block.IsSet[(int)ShaderProp.PatternIconSize]);
        Assert.Equal(0, scene.Renderer.Materials.IconPatternCopies);
    }

    [Fact]
    public void A_tiled_pattern_deconstructs_and_compares_by_value()
    {
        var (icon, paint) = Tiled();

        Assert.Equal(Spray, icon);
        Assert.Equal(new Paint.FinishColour(LibraryPalette.Planned), paint);
        Assert.Equal(Tiled(0.75f), Tiled(0.75f));
        Assert.NotEqual(Tiled(0.75f), Tiled());
    }

    [Fact]
    public void An_icon_none_from_a_later_layer_clears_the_pattern()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("icons", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Patterned(Tiled()))));
        bench.Add(LayerOf("plain", StyleBand.Theme, 1, Rule(new StyleMatch.Everything(), Patterned(new IconPattern.None()))));
        var ghost = bench.PlaceOne("StructureFrame", At);

        var story = bench.Story(ghost.Id);

        Assert.Equal(new IconPattern.None(), story.Resolved.PatternIcon.Reduce(Tiled()));
        Assert.Equal(new StyleSource.Layer(StyleBand.Theme, Layer("plain"), 0), story.Sources[StyleProperty.PatternIcon]);
        Assert.Equal(default, bench.Look(ghost.Id).PatternIcon);
    }

    [Fact]
    public void An_unregistered_icon_draws_no_pattern_and_is_logged_once()
    {
        var scene = Drawn(Patterned(Tiled()));
        scene.Bench.PlaceOne(Modded, new Cell(20, 10, 10));
        scene.Frame();
        scene.Frame();

        Assert.Equal(2, scene.Rig.Gpu.Draws.Count);
        foreach (var draw in scene.Rig.Gpu.Draws)
        {
            Assert.False(Material(draw).IconPatterned);
            Assert.Equal(Vector4.zero, draw.Block[ShaderProp.Pattern]);
            Assert.False(draw.Block.IsSet[(int)ShaderProp.PatternIcon]);
        }

        Assert.Single(scene.Rig.Log, static line => line.Contains("spray-can", StringComparison.Ordinal));
    }

    [Fact]
    public void An_icon_registered_later_draws_from_the_next_frame_and_disposing_it_stops_it()
    {
        var scene = Drawn(Patterned(Tiled()));
        Assert.False(Material(scene.Fill).IconPatterned);

        var registration = RenderResults.Ok(scene.Renderer.Materials.Icons.Register(Spray, new FakeTexture("spray")));
        scene.Frame();
        Assert.True(Material(scene.Fill).IconPatterned);

        registration.Dispose();
        scene.Frame();
        Assert.False(Material(scene.Fill).IconPatterned);
    }

    [Fact]
    public void An_override_fill_without_the_keyword_draws_no_icon_makes_no_copy_and_is_logged_once()
    {
        var planner = new FakeMaterial("planner-ghost");
        var scene = Drawn(Patterned(Tiled()), before: r => RenderResults.Ok(r.Materials.Override(MaterialKeys.Ghost, planner)), icon: new FakeTexture("spray"));
        scene.Frame();

        var fill = scene.Fill;
        Assert.Equal("planner-ghost", BaseName(Material(fill)));
        Assert.False(Material(fill).IconPatterned);
        Assert.False(fill.Block.IsSet[(int)ShaderProp.PatternIcon]);
        Assert.Equal(Vector4.zero, fill.Block[ShaderProp.Pattern]);
        Assert.DoesNotContain(AllCopies(planner), static m => m.IconPatterned);
        Assert.Equal(0, scene.Renderer.Materials.IconPatternCopies);
        Assert.Single(scene.Rig.Log, static line => line.Contains("LATTICE_ICON_PATTERN", StringComparison.Ordinal));
    }

    [Fact]
    public void An_override_fill_with_the_keyword_draws_its_own_icon_copy_destroyed_with_the_override()
    {
        var planner = new FakeMaterial("planner-ghost", iconPattern: true);
        IDisposable? installed = null;
        var scene = Drawn(Patterned(Tiled()), before: r => installed = RenderResults.Ok(r.Materials.Override(MaterialKeys.Ghost, planner)), icon: new FakeTexture("spray"));
        scene.Frame();

        var fill = Material(scene.Fill);
        Assert.True(fill.IconPatterned);
        Assert.Equal("planner-ghost", BaseName(fill));
        Assert.Equal(0, planner.Writes);

        installed!.Dispose();
        scene.Frame();

        Assert.True(fill.Destroyed);
        Assert.Equal("ghost", BaseName(Material(scene.Fill)));
        Assert.True(Material(scene.Fill).IconPatterned);
    }

    [Fact]
    public void World_end_destroys_every_icon_copy_and_drops_the_icons()
    {
        var scene = Drawn(Patterned(Tiled()), icon: new FakeTexture("spray"));
        var fill = Material(scene.Fill);

        scene.Renderer.End();

        Assert.True(fill.Destroyed);
        Assert.Equal(0, scene.Renderer.Materials.Icons.Count);
        Assert.Equal(0, scene.Renderer.Materials.IconPatternCopies);
    }

    [Fact]
    public void The_fill_shaders_compile_the_icon_pattern_only_under_its_keyword_with_one_sample()
    {
        foreach (var file in new[] { "LatticeGhost.shader", "LatticeSolid.shader" })
        {
            var source = Shader(file);
            Assert.Contains("#pragma multi_compile_local __ LATTICE_ICON_PATTERN", source);
            Assert.Contains("LatticeApplyFillPattern(colour, i.worldPos, i.worldNormal)", source);
        }

        foreach (var file in new[] { "LatticeGhost.shader", "LatticeSolid.shader", "LatticeGhostEdged.shader" })
        {
            Assert.Contains("_LatticePatternIcon (", Shader(file));
            Assert.Contains("_LatticePatternIconSize (", Shader(file));
        }

        var common = Shader("LatticeCommon.cginc");
        Assert.Contains("#if defined(LATTICE_ICON_PATTERN)", common);
        Assert.Single(Regex.Matches(common, @"tex2D\w*\("));
    }

    [Fact]
    public void The_material_contract_names_the_icon_pattern_keyword_and_values()
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(MaterialKeys).Assembly.Location, ".xml"));
        var doc = string.Join(" ", docs.Descendants("member").Single(static m => (string?)m.Attribute("name") == "T:Lattice.Api.MaterialKeys").Value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        foreach (var named in new[] { "LATTICE_ICON_PATTERN", "_LatticePatternIcon", "_LatticePatternIconSize", "must declare the keyword" })
            Assert.Contains(named, doc);
    }

    private static IEnumerable<FakeMaterial> AllCopies(FakeMaterial material)
    {
        foreach (var copy in material.Copies)
        {
            yield return copy;
            foreach (var deeper in AllCopies(copy))
                yield return deeper;
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
