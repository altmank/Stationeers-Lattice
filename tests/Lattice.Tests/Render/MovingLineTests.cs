using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lattice.Api;
using Lattice.Render.Gpu;
using Lattice.Tests.Styles;
using UnityEngine;
using Renderer = Lattice.Render.Renderer;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Render;

/// <summary>
/// The moving line: <see cref="EdgeLook.Line"/> built as before compares, deconstructs and submits as before; its dash
/// length and speed reach <c>_LatticeEdge</c> z and w; an under-line draws with the internal edge-under material only
/// for a look that has one, so a look without one keeps one combined call; the <c>ants</c> material is Lattice's and
/// cuts its dashes by the same rule.
/// </summary>
public sealed class MovingLineTests
{
    private static readonly Pixels Two = Must(Pixels.Create(2f));
    private static readonly Paint Fail = new Paint.Token(LibraryPalette.Fail);

    private static EdgeLook.Line Ants(float dash = 0.3f, float speed = 1.5f, float under = 0f)
    {
        var line = new EdgeLook.Line(LineStyle.Dashed, Must(Pixels.Create(3f)), Fail)
        {
            Dash = Option.Some(Must(Metres.Create(dash))),
            Speed = Must(Speed.Create(speed)),
        };
        return under > 0f ? line with { Under = Option.Some(new UnderLine(Must(Pixels.Create(under)), new Paint.Token(LibraryPalette.Stale))) } : line;
    }

    private static (Renderer Renderer, RenderRig Rig) Drawn(EdgeLook.Line line, Action<Renderer>? before = null, StyleLayerDescriptor? extra = null)
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Add(LayerOf("edges", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), new Style { Edge = Option.Some<EdgeLook>(line) })));
        if (extra is not null)
            bench.Add(extra);
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var renderer = rig.Over(bench);
        before?.Invoke(renderer);
        renderer.Frame(0f);
        RenderRig.Settle(bench, renderer);
        rig.Gpu.NewFrame();
        renderer.Frame(0.1f);
        return (renderer, rig);
    }

    private static string Name(DrawCall draw) => ((FakeMaterial)draw.Material).CopyOf!.Name;

    [Fact]
    public void A_line_built_as_before_compares_and_deconstructs_as_before()
    {
        var a = new EdgeLook.Line(LineStyle.Dashed, Two, Fail);
        var b = new EdgeLook.Line(LineStyle.Dashed, Two, Fail);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        var (style, width, paint) = a;
        Assert.Equal((LineStyle.Dashed, Two, Fail), (style, width, paint));
        Assert.Equal(Option<Metres>.None, a.Dash);
        Assert.Equal(Speed.Zero, a.Speed);
        Assert.Equal(Option<UnderLine>.None, a.Under);
        Assert.NotEqual(a, a with { Speed = Must(Speed.Create(1f)) });
    }

    [Fact]
    public void A_line_built_as_before_submits_the_same_values_in_one_combined_call()
    {
        var (_, rig) = Drawn(new EdgeLook.Line(LineStyle.Dashed, Two, Fail));

        var draw = Assert.Single(rig.Gpu.Draws);
        Assert.Equal("ghost-edged", Name(draw));
        Assert.Equal(new Vector4(1f, 2f, 0f, 0f), draw.Block[ShaderProp.Edge]);
        Assert.False(draw.Block.IsSet[(int)ShaderProp.EdgeUnder]);
        Assert.False(draw.Block.IsSet[(int)ShaderProp.EdgeUnderColor]);
    }

    [Fact]
    public void Dash_length_and_speed_reach_the_edge_values()
    {
        var (_, rig) = Drawn(Ants(dash: 0.3f, speed: -1.5f));

        var draw = Assert.Single(rig.Gpu.Draws);
        Assert.Equal("ghost-edged", Name(draw));
        Assert.Equal(new Vector4(1f, 3f, 0.3f, -1.5f), draw.Block[ShaderProp.Edge]);
    }

    [Fact]
    public void An_under_line_draws_the_edge_with_the_edge_under_material_beside_the_fill()
    {
        var (_, rig) = Drawn(Ants(under: 5f));

        Assert.Equal(new[] { "edge-under", "ghost" }, rig.Gpu.Draws.Select(Name).OrderBy(static n => n, StringComparer.Ordinal).ToArray());
        var edge = rig.Gpu.Draws.Single(d => Name(d) == "edge-under");
        Assert.True(edge.Procedural);
        Assert.Equal(new Vector4(1f, 3f, 0.3f, 1.5f), edge.Block[ShaderProp.Edge]);
        Assert.Equal(new Vector4(5f, 0f, 0f, 0f), edge.Block[ShaderProp.EdgeUnder]);
        Assert.True(edge.Block[ShaderProp.EdgeUnderColor].w > 0f);
    }

    [Fact]
    public void With_an_edge_override_the_override_draws_the_edge_and_gets_the_under_line_values()
    {
        var planner = new FakeMaterial("planner-edge");
        var (_, rig) = Drawn(Ants(under: 5f), renderer => RenderResults.Ok(renderer.Materials.Override(MaterialKeys.Edge, planner)));

        Assert.DoesNotContain(rig.Gpu.Draws, d => Name(d) == "edge-under");
        var edge = rig.Gpu.Draws.Single(d => ((FakeMaterial)d.Material).CopyOf == planner);
        Assert.Equal(new Vector4(5f, 0f, 0f, 0f), edge.Block[ShaderProp.EdgeUnder]);
    }

    [Fact]
    public void Reduced_motion_draws_the_dashes_still()
    {
        var reduced = new StyleLayerDescriptor(Layer("calm"), StyleBand.Theme, 1, ValueList<StyleRule>.Empty) { Motion = Option.Some(Motion.Reduced) };
        var (_, rig) = Drawn(Ants(dash: 0.3f, speed: 2f), extra: reduced);

        Assert.Equal(new Vector4(1f, 3f, 0.3f, 0f), Assert.Single(rig.Gpu.Draws).Block[ShaderProp.Edge]);
    }

    [Fact]
    public void The_ants_material_is_lattices_own_and_a_consumer_reads_it()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        var renderer = rig.Over(bench);

        var ants = (FakeMaterial)RenderResults.Ok(renderer.Materials.Exposed(MaterialKeys.Ants));

        Assert.Equal("ants", ants.CopyOf!.Name);
    }

    [Fact]
    public void The_ants_doc_names_its_properties_and_vertex_layout()
    {
        var docs = XDocument.Load(Path.ChangeExtension(typeof(MaterialKeys).Assembly.Location, ".xml"));
        var doc = docs.Descendants("member").Single(static m => (string?)m.Attribute("name") == "F:Lattice.Api.MaterialKeys.Ants").Value;

        foreach (var named in new[] { "_Color", "_LatticeEdge", "POSITION", "TEXCOORD0", "TEXCOORD1.x", "metres" })
            Assert.Contains(named, doc);
    }

    private static string Shader(string file) => File.ReadAllText(Path.Combine(ShaderDir(), file));

    private static string ShaderDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Lattice.Render", "Shaders");
    }

    private static int Passes(string source) => Regex.Matches(source, @"^[ \t]*(Pass[ \t]*\r?$|UsePass\b)", RegexOptions.Multiline).Count;

    [Fact]
    public void The_shared_edge_shaders_keep_their_passes_and_only_edge_under_adds_one()
    {
        Assert.Equal(3, Passes(Shader("LatticeEdge.shader")));
        Assert.Equal(4, Passes(Shader("LatticeGhostEdged.shader")));
        Assert.Equal(4, Passes(Shader("LatticeEdgeUnder.shader")));
        Assert.DoesNotContain("UNDER", Shader("LatticeEdge.shader"));
        Assert.Contains("Name \"UNDER\"", Shader("LatticeEdgeUnder.shader"));
    }

    [Fact]
    public void Ghost_edges_and_ants_cut_their_dashes_with_the_one_shared_rule()
    {
        Assert.Contains("float LatticeDash(float s, float4 edge)", Shader("LatticeCommon.cginc"));
        Assert.Contains("LatticeDash(i.worldPos.x + i.worldPos.y + i.worldPos.z, _LatticeEdge)", Shader("LatticeEdge.shader"));
        var ants = Shader("LatticeAnts.shader");
        Assert.Contains("LatticeDash(i.along, _LatticeEdge)", ants);
        Assert.Contains("float4 other : TEXCOORD0;", ants);
        Assert.Contains("float2 along : TEXCOORD1;", ants);
        Assert.Contains("_Time.y", Shader("LatticeCommon.cginc"));
    }
}
