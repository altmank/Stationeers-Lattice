using System;
using System.Diagnostics;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Styles;
using Lattice.Render;
using Lattice.Render.Gpu;
using Lattice.TestKit.Allocations;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Styles;
using static Lattice.Tests.Styles.StyleBench;
using Matrix4x4 = UnityEngine.Matrix4x4;

namespace Lattice.Tests.Render;

/// <summary>
/// The draw-call budget at 20,000 ghosts. A 20,000-structure sample of a large real base (Vulcan2_4)
/// (LatticeFixtures\bigbase\vulcan-structures.tsv) holds 150 to 165 pieces in 559 to 561 (piece, 32 m chunk) batches,
/// with 1.4 to 4.0 shown mesh parts per batch by a catalogue export's look counts. Here 300 pieces (Vulcan's
/// pieces at two looks each), 2.8 parts each, every ghost with a fill and an edge, all in view: one draw group per
/// piece and look across chunks and one call per part (fill and edge in one material), 840 calls against a budget of
/// 1,000. Unity's procedural draw costs about 0.55 us a call on Mono (measured in game), about 0.46 ms here. Also Lattice's
/// own main-thread submission per frame over a GPU that does nothing (at most 0.2 ms on .NET 8; Mono is slower), a steady world
/// frame with Render's sort at most 1.0 ms, and 0 B per frame. Wall-clock: run with
/// <c>./build.ps1 -Perf</c>.
/// </summary>
[Trait(Perf.Trait, Perf.Value)]
public sealed class RenderPerfTests
{
    private const int CallBudget = 1_000;
    private const int Pieces = 300;
    private const int Ghosts = 20_000;
    private static readonly int[] PartsCycle = { 1, 2, 3, 4, 4 };

    private static string NameOf(int piece) => "ModPerfPiece" + piece;

    private static NamedPiece[] Modded()
    {
        var straight = FakeCatalogue.Hand().Named("StructureCableStraight");
        var pieces = new NamedPiece[Pieces];
        for (var i = 0; i < Pieces; i++)
            pieces[i] = new NamedPiece(NameOf(i), ValueList<string>.Empty, straight with { Key = PrefabHash.Piece(NameOf(i)), Kits = ValueList<KitPlace>.Empty },
                ValueMap<ItemKey, string>.Empty);
        return pieces;
    }

    private static (StyleBench Bench, RenderRig Rig, Renderer Renderer, int Parts) VulcanShaped()
    {
        var bench = new StyleBench(true, Modded());
        var rig = new RenderRig();
        var parts = 0;
        for (var i = 0; i < Pieces; i++)
        {
            var count = PartsCycle[i % PartsCycle.Length];
            parts += count;
            var meshes = new RenderPart[count];
            for (var p = 0; p < count; p++)
                meshes[p] = new RenderPart(new FakeMesh(NameOf(i) + "/" + p), Matrix4x4.identity, PartShows.Every);
            rig.Pieces.With(bench.T.Catalogue.Named(NameOf(i)).Key, meshes);
        }

        bench.Add(LayerOf("everything", StyleBand.Meaning, 0, Rule(new StyleMatch.Everything(), new Style
        {
            FillPaint = Option.Some<Paint>(new Paint.GhostTint(LibraryPalette.Planned)),
            Edge = Option.Some<EdgeLook>(new EdgeLook.Line(LineStyle.Solid, Must(Pixels.Create(2f)), new Paint.Token(LibraryPalette.Next))),
        })));

        var drafts = new GhostDraft[Ghosts];
        for (var i = 0; i < Ghosts; i++)
            drafts[i] = bench.Draft(NameOf(i % Pieces), new Cell(5 + 10 * (i % 100), 5 + 10 * (i / 10_000), 5 + 10 * (i / 100 % 100)));
        for (var from = 0; from < drafts.Length; from += 5_000)
            bench.T.Placed(drafts.AsSpan(from, Math.Min(5_000, drafts.Length - from)).ToArray());
        bench.Settle(1_000_000);
        return (bench, rig, rig.Over(bench), parts);
    }

    [Fact]
    public void Twenty_thousand_ghosts_on_a_vulcan_shaped_base_stay_within_the_call_budget_at_0_B_a_frame()
    {
        var (bench, rig, renderer, parts) = VulcanShaped();
        renderer.Frame(0f);
        var counts = renderer.Counts;

        Assert.Equal(Ghosts, counts.Instances);
        Assert.Equal(Pieces, counts.Groups);
        Assert.Equal(parts, counts.Calls);
        Assert.True(counts.Calls <= CallBudget, $"{counts.Calls} draw calls a frame, budget {CallBudget}");

        var now = 0f;
        void Frame()
        {
            rig.Gpu.NewFrame();
            now += 0.016f;
            bench.Frame();
            renderer.BeforeCameras();
            renderer.Frame(now);
        }

        rig.Gpu.Recording = false;
        for (var i = 0; i < 50; i++)
            Frame();
        var submit = new Stopwatch();
        var world = new Stopwatch();
        var worst = 0.0;
        const int Frames = 200;
        for (var i = 0; i < Frames; i++)
        {
            rig.Gpu.NewFrame();
            now += 0.016f;
            world.Start();
            bench.Frame();
            world.Stop();
            var start = submit.Elapsed.TotalMilliseconds;
            submit.Start();
            renderer.BeforeCameras();
            renderer.Frame(now);
            submit.Stop();
            worst = Math.Max(worst, submit.Elapsed.TotalMilliseconds - start);
        }

        var mean = submit.Elapsed.TotalMilliseconds / Frames;
        var steady = world.Elapsed.TotalMilliseconds / Frames;
        Assert.True(mean <= 0.2, $"Lattice's submission took {mean:F3} ms a frame on average (worst {worst:F3} ms) for {counts.Calls} calls");
        Assert.True(steady <= 1.0, $"a steady world frame with Render's sort took {steady:F3} ms");
        Allocations.AssertNone(Frame, "a steady render frame over 20,000 ghosts");
    }
}
