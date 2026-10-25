using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Render.Marks;
using Lattice.Tests.Styles;
using UnityEngine;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Render;

/// <summary>What a mark is, by its shape.</summary>
internal enum MarkKind
{
    Unknown,
    Port,
    Open,
    Shaft,
    Head,
    Link,
    Tip,
}

/// <summary>
/// Styles over the test board with one look on everything, Render over them with marks worked out from the board, the
/// fake built world and the hand catalogue (<see cref="WorldMarkFacts"/>), and the marks a frame draws.
/// </summary>
internal class MarkScene
{
    public static readonly Cell Origin = Cell.FromWorld(new WorldPoint(10.25f, 5.25f, 10.25f), PieceGrid.Small);

    public MarkScene(Style look)
    {
        Bench = new StyleBench();
        Bench.Add(LayerOf("marks", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), look)));
        var catalogue = Bench.T.Catalogue;
        Rig.Facts = new WorldMarkFacts(
            new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue)),
            Bench.T.View, new Lattice.TestKit.World.FakeWorldQuery(Bench.World), Bench.T.Events);
    }

    public StyleBench Bench { get; }

    public RenderRig Rig { get; } = new();

    public Lattice.Render.Renderer? Renderer { get; private set; }

    /// <summary>The small cell <paramref name="cells"/> steps from <see cref="Origin"/> across a face.</summary>
    public static Cell Along(Face face, int cells)
    {
        var cell = Origin;
        for (var i = 0; i < cells; i++)
            cell = cell.Neighbour(face, PieceGrid.Small);
        return cell;
    }

    public void Open() => Renderer = Rig.Over(Bench);

    /// <summary>Settles the styles and rebuilds, then draws a frame.</summary>
    public void Draw()
    {
        RenderRig.Settle(Bench, Renderer!);
        Rig.Gpu.NewFrame();
        Renderer!.Frame(0f);
    }

    public List<DrawCall> MarkDraws(string material) =>
        Rig.Gpu.Draws.Where(d => ReferenceEquals(d.Mesh, Rig.Gpu.Marker) && d.Material.ToString()!.StartsWith(material, StringComparison.Ordinal)).ToList();

    public int Marks(string material) => MarkDraws(material).Sum(static d => d.Count);

    /// <summary>How many marks of a kind the frame drew, told apart by their shapes (<see cref="MarkShapes"/>).</summary>
    public int Count(MarkKind kind) => Rig.Gpu.Marks.Count(m => KindOf(m) == kind);

    /// <summary>The draw that drew marks of a kind (one per group and kind).</summary>
    public DrawCall DrawOf(MarkKind kind) => Rig.Gpu.Draws.Single(d => ReferenceEquals(d.Mesh, Rig.Gpu.Marker) && KindOf(d.First) is var k && Same(k, kind));

    // A flow shaft and head, or a waits bar and tip, draw in one call; the call's first matrix is either.
    private static bool Same(MarkKind a, MarkKind b) => a == b || Pair(a) == Pair(b);

    private static int Pair(MarkKind kind) => kind switch
    {
        MarkKind.Shaft or MarkKind.Head => 1,
        MarkKind.Link or MarkKind.Tip => 2,
        _ => 10 + (int)kind,
    };

    public static MarkKind KindOf(Matrix4x4 m)
    {
        var x = Length(m.GetColumn(0));
        var y = Length(m.GetColumn(1));
        var z = Length(m.GetColumn(2));
        bool Is(float a, float b) => Math.Abs(a - b) < 1e-4f;
        bool Cube(float size) => Is(x, size) && Is(y, size) && Is(z, size);
        bool Bar(float length, float width) => new[] { x, y, z }.Count(v => Is(v, length)) == 1 && new[] { x, y, z }.Count(v => Is(v, width)) == 2;
        if (Cube(MarkShapes.OpenSize))
            return MarkKind.Open;
        if (Cube(MarkShapes.HeadSize))
            return MarkKind.Head;
        if (Cube(MarkShapes.LinkHead))
            return MarkKind.Tip;
        if (Bar(MarkShapes.PortLength, MarkShapes.PortWidth))
            return MarkKind.Port;
        if (Bar(MarkShapes.ShaftLength, MarkShapes.ShaftWidth))
            return MarkKind.Shaft;
        return Is(x, MarkShapes.LinkWidth) && Is(y, MarkShapes.LinkWidth) ? MarkKind.Link : MarkKind.Unknown;
    }

    private static float Length(Vector4 v) => (float)Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);

    public PrefabKey Key(string piece) => Bench.T.Catalogue.Named(piece).Key;
}
