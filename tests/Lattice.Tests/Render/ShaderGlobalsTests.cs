using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Lattice.Api;
using Lattice.Render.Gpu;
using Lattice.Tests.Styles;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Render;

/// <summary>
/// A per-draw value Render sets through a property block reaches a Lattice shader only when the shader declares the
/// property: on the player, a property set as a global as well (the viewer's default <c>_LatticeBeyond</c>) is read
/// from the global for a material that lacks it, so a style's own value never arrives. Every Lattice shader declares
/// in its Properties block each per-draw value property it reads: in its own code, through the
/// <c>LatticeCommon.cginc</c> helpers it calls, or through the shaders whose passes it uses. Buffers and matrices
/// (<c>_LatticeInstances</c>, <c>_LatticeLocal</c>) cannot be declared there and are not value properties.
/// </summary>
public sealed class ShaderGlobalsTests
{
    private static string ShadersDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lattice.sln")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "Lattice.Render", "Shaders");
    }

    private static readonly ShaderProp[] NotValues = { ShaderProp.Instances, ShaderProp.Local };

    /// <summary>A frame with a clip and every look layer on: which properties Render sets on draw blocks, and which as globals.</summary>
    private static (HashSet<string> PerDraw, HashSet<string> Globals) Set() => Frame().Sets;

    /// <summary>The frame of <see cref="Set"/>, with the rig that recorded it.</summary>
    private static (RenderRig Rig, (HashSet<string> PerDraw, HashSet<string> Globals) Sets) Frame()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Add(LayerOf("all", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), new Style
        {
            BeyondClip = Option.Some<BeyondClipLook>(new BeyondClipLook.Hidden()),
            Pattern = Option.Some(Pattern.Hatch),
            Effect = Option.Some<EffectLook>(new EffectLook.Pulse(Must(Period.Create(1f)), Must(Fraction.Create(0.2f)))),
            SeeThrough = Option.Some<SeeThroughLook>(new SeeThroughLook.Through(Must(Fraction.Create(0.5f)), Must(Metres.Create(40f)))),
        }), Rule(new StyleMatch.Everything(), Edge(LineStyle.Dashed, LibraryPalette.Fail))));
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        using var clip = Must(bench.Styles.Clip(Must(ClipShape.Plane.Create(new WorldPoint(0f, 1f, 0f), new WorldVector(0f, 1f, 0f)))));
        var renderer = rig.Over(bench);
        rig.Gpu.NewFrame();
        renderer.BeforeCameras();
        renderer.Frame(0.5f);

        var perDraw = new HashSet<string>();
        var globals = new HashSet<string>();
        for (var i = 0; i < ShaderNames.Count; i++)
        {
            if (NotValues.Contains((ShaderProp)i))
                continue;
            if (rig.Gpu.Draws.Any(d => d.Block.IsSet[i]))
                perDraw.Add(ShaderNames.Of((ShaderProp)i));
            if (rig.Gpu.GlobalSet[i])
                globals.Add(ShaderNames.Of((ShaderProp)i));
        }

        return (rig, (perDraw, globals));
    }

    /// <summary>The top-level functions of the common include and the property names their bodies name, calls followed.</summary>
    private static Dictionary<string, HashSet<string>> HelperReads(IReadOnlyCollection<string> names)
    {
        var text = File.ReadAllText(Path.Combine(ShadersDir(), "LatticeCommon.cginc"));
        var bodies = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(text, @"^[A-Za-z0-9_]+\s+(?<name>Lattice[A-Za-z0-9_]+)\s*\([^)]*\)\s*\{", RegexOptions.Multiline))
        {
            var depth = 1;
            var at = m.Index + m.Length;
            while (depth > 0 && at < text.Length)
                depth += text[at++] switch { '{' => 1, '}' => -1, _ => 0 };
            bodies[m.Groups["name"].Value] = text.Substring(m.Index + m.Length, at - m.Index - m.Length);
        }

        var reads = new Dictionary<string, HashSet<string>>();
        foreach (var (name, body) in bodies)
            reads[name] = Names(body, names);
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (name, body) in bodies)
                foreach (var other in bodies.Keys)
                    if (other != name && Regex.IsMatch(body, @"\b" + other + @"\s*\("))
                        foreach (var n in reads[other])
                            changed |= reads[name].Add(n);
        }

        return reads;
    }

    private static HashSet<string> Names(string code, IReadOnlyCollection<string> names) =>
        names.Where(n => Regex.IsMatch(code, @"\b" + Regex.Escape(n) + @"\b")).ToHashSet();

    /// <summary>Every Lattice shader by its name, with the per-draw properties it reads and the ones its Properties block declares.</summary>
    private static Dictionary<string, (string File, HashSet<string> Reads, HashSet<string> Declared)> Shaders(IReadOnlyCollection<string> perDraw)
    {
        var helpers = HelperReads(perDraw);
        var shaders = new Dictionary<string, (string File, string Code, HashSet<string> Reads, HashSet<string> Declared)>();
        foreach (var file in Directory.GetFiles(ShadersDir(), "*.shader"))
        {
            var text = File.ReadAllText(file);
            var name = Regex.Match(text, "Shader\\s+\"(?<n>[^\"]+)\"").Groups["n"].Value;
            var properties = Regex.Match(text, @"Properties\s*\{(?<body>[^}]*)\}");
            Assert.True(properties.Success, $"{Path.GetFileName(file)} has no Properties block");
            var declared = perDraw.Where(n => Regex.IsMatch(properties.Groups["body"].Value, @"(^|\s)" + Regex.Escape(n) + @"\s*\(")).ToHashSet();
            var code = text.Substring(properties.Index + properties.Length);
            var reads = Names(code, perDraw);
            foreach (var (helper, names) in helpers)
                if (Regex.IsMatch(code, @"\b" + helper + @"\s*\("))
                    reads.UnionWith(names);
            shaders[name] = (Path.GetFileName(file), code, reads, declared);
        }

        foreach (var (name, shader) in shaders.ToList())
            foreach (Match use in Regex.Matches(shader.Code, "UsePass\\s+\"(?<s>[^\"]+)/[^/\"]+\""))
                if (shaders.TryGetValue(use.Groups["s"].Value, out var used))
                    shader.Reads.UnionWith(used.Reads);
        return shaders.ToDictionary(static p => p.Key, static p => (p.Value.File, p.Value.Reads, p.Value.Declared));
    }

    /// <summary>Every property the shaders declare as a scalar <c>float</c> (<c>float _Name;</c>).</summary>
    private static HashSet<string> ScalarFloats()
    {
        var floats = new HashSet<string>();
        foreach (var file in Directory.GetFiles(ShadersDir()).Where(static f => f.EndsWith(".shader", StringComparison.Ordinal) || f.EndsWith(".cginc", StringComparison.Ordinal)))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"^\s*float\s+(?<name>_[A-Za-z0-9_]+)\s*;", RegexOptions.Multiline))
                floats.Add(m.Groups["name"].Value);
        return floats;
    }

    [Fact]
    public void A_property_the_shaders_declare_as_a_float_is_set_as_a_float_never_as_a_vector()
    {
        // The engine keeps floats apart from vectors: a float uniform set through the vector setter reads 0, so a
        // clip set as a vector mode never clips.
        var (rig, _) = Frame();
        var floats = ScalarFloats();
        Assert.Contains("_LatticeClipMode", floats);
        var wrong = new List<string>();
        var seen = 0;
        for (var i = 0; i < ShaderNames.Count; i++)
        {
            var name = ShaderNames.Of((ShaderProp)i);
            if (!floats.Contains(name))
                continue;
            if (rig.Gpu.GlobalAsVector[i])
                wrong.Add(name + " as a global vector");
            if (rig.Gpu.Draws.Any(d => d.Block.AsVector[i]))
                wrong.Add(name + " as a per-draw vector");
            if (rig.Gpu.GlobalAsFloat[i] || rig.Gpu.Draws.Any(d => d.Block.AsFloat[i]))
                seen++;
        }

        Assert.True(wrong.Count == 0, "set as vectors: " + string.Join(", ", wrong));
        Assert.Equal(3, seen);
    }

    [Fact]
    public void The_beyond_look_is_set_per_draw_and_as_a_global()
    {
        var (perDraw, globals) = Set();
        Assert.Contains("_LatticeBeyond", perDraw);
        Assert.Contains("_LatticeBeyond", globals);
    }

    [Fact]
    public void Every_lattice_shader_declares_each_per_draw_property_it_reads()
    {
        var (perDraw, _) = Set();
        var shaders = Shaders(perDraw);
        Assert.Contains("_LatticeBeyond", shaders["Lattice/Ghost"].Reads);
        Assert.Contains("_LatticeBeyond", shaders["Lattice/GhostEdged"].Reads);
        var missing = new List<string>();
        foreach (var (_, shader) in shaders)
            foreach (var name in shader.Reads.Where(n => !shader.Declared.Contains(n)).OrderBy(static n => n, StringComparer.Ordinal))
                missing.Add($"{shader.File}: {name}");

        Assert.True(missing.Count == 0, "undeclared per-draw properties: " + string.Join(", ", missing));
    }
}
