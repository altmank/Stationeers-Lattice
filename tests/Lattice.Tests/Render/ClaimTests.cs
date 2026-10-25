using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.Render;
using Lattice.Render.Gpu;
using Lattice.TestKit.Allocations;
using Lattice.Tests.Styles;
using UnityEngine;
using Renderer = Lattice.Render.Renderer;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Render;

/// <summary>
/// Renderer claims: a claimed thing's claimed parts leave Lattice's drawing
/// and the claim is called once a frame after Lattice's submission with its things in view; the highest order, then the
/// later registration, wins; a callback may dispose its own claim and register another mid-loop; a callback's exception
/// leaves the loop with Lattice's drawing submitted and the claim still registered; a steady claimed frame allocates
/// nothing.
/// </summary>
public sealed class ClaimTests
{
    private static ClaimId Id(string name) => Must(ClaimId.Create(Mod, name));

    private static RendererClaimDescriptor Claim(string name, StyleMatch match, ClaimParts parts, Action<IClaimFrame> draw, int order = 0) =>
        new(Id(name), match, parts, draw) { Order = order };

    private static (StyleBench Bench, RenderRig Rig, PrefabKey Frame, StyleMatch Frames) Base()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        var frame = ((Intent.Build)bench.Draft("StructureFrame", new Cell(0, 0, 0)).Intent).Target.Piece;
        rig.Pieces.With(frame, new RenderPart(new FakeMesh("frame"), Matrix4x4.identity, PartShows.Every));
        return (bench, rig, frame, new StyleMatch.Picks(new Selector.OfPiece(frame)));
    }

    /// <summary>Lattice's frame after a claim change: the next sort settles who wins, then the groups rebuild.</summary>
    private static void Next(StyleBench bench, RenderRig rig, Renderer renderer, float now)
    {
        RenderRig.Settle(bench, renderer);
        rig.Gpu.NewFrame();
        renderer.Frame(now);
    }

    [Fact]
    public void A_claim_on_everything_takes_its_things_out_of_lattices_drawing_and_gets_them_in_view()
    {
        var (bench, rig, frame, Frames) = Base();
        var placed = bench.Place(
            bench.Draft("StructureFrame", new Cell(10, 10, 10)),
            bench.Draft("StructureFrame", new Cell(30, 10, 10)),
            bench.Draft("StructureCableStraight", new Cell(50, 10, 10)));
        var renderer = rig.Over(bench);
        var seen = new List<ClaimedThing>();
        RenderResults.Ok(renderer.Claim(Claim("frames", Frames, ClaimParts.Everything, f =>
        {
            for (var i = 0; i < f.Count; i++)
                seen.Add(f[i]);
        })));

        Next(bench, rig, renderer, 0f);

        Assert.Equal(new[] { "cube" }, rig.Gpu.Draws.Select(static d => d.Mesh.ToString()!).ToArray());
        Assert.Equal(2, seen.Count);
        Assert.All(seen, t => Assert.Equal(frame, t.Piece));
        Assert.All(seen, static t => Assert.Equal(PartKind.Placement, t.Part));
        var frames = placed.Added.Where(g => ((Intent.Build)g.Intent).Target.Piece == frame).Select(static g => (DrawnThing)new DrawnThing.OfGhost(g.Id));
        Assert.Equal(frames.OrderBy(static t => t.ToString()), seen.Select(static t => t.Thing).OrderBy(static t => t.ToString()));
        Assert.Contains(seen, static t => t.Matrix.GetColumn(3) == new Vector4(1f, 1f, 1f, 1f));
    }

    [Fact]
    public void A_mesh_claim_leaves_lattice_the_edge_and_takes_the_fill()
    {
        var (bench, rig, _, Frames) = Base();
        bench.Add(LayerOf("edges", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Edge(LineStyle.Dashed, LibraryPalette.Fail))));
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var renderer = rig.Over(bench);
        var calls = 0;
        RenderResults.Ok(renderer.Claim(Claim("frames", Frames, ClaimParts.Mesh, f => calls += f.Count)));

        Next(bench, rig, renderer, 0f);

        var draw = Assert.Single(rig.Gpu.Draws);
        Assert.Equal("edge", ((FakeMaterial)draw.Material).CopyOf!.Name);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void The_higher_order_wins_then_the_later_registration_and_disposing_the_winner_hands_its_things_on()
    {
        var (bench, rig, _, Frames) = Base();
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var renderer = rig.Over(bench);
        var counts = new Dictionary<string, int> { ["low"] = 0, ["high"] = 0, ["late"] = 0 };
        Action<IClaimFrame> Count(string name) => f => counts[name] = f.Count;
        RenderResults.Ok(renderer.Claim(Claim("low", Frames, ClaimParts.Everything, Count("low"))));
        var high = RenderResults.Ok(renderer.Claim(Claim("high", Frames, ClaimParts.Everything, Count("high"), order: 5)));
        RenderResults.Ok(renderer.Claim(Claim("late", Frames, ClaimParts.Everything, Count("late"))));

        Next(bench, rig, renderer, 0f);
        Assert.Equal((0, 1, 0), (counts["low"], counts["high"], counts["late"]));

        high.Dispose();
        Next(bench, rig, renderer, 0.1f);
        Assert.Equal((0, 1), (counts["low"], counts["late"]));
        Assert.Empty(rig.Gpu.Draws);
    }

    [Fact]
    public void A_callback_that_disposes_its_own_claim_and_registers_another_leaves_the_loop_on_its_list()
    {
        var (bench, rig, _, Frames) = Base();
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var renderer = rig.Over(bench);
        var calls = new List<string>();
        IDisposable? first = null;
        first = RenderResults.Ok(renderer.Claim(Claim("first", Frames, ClaimParts.Everything, _ =>
        {
            calls.Add("first");
            first!.Dispose();
            RenderResults.Ok(renderer.Claim(Claim("second", Frames, ClaimParts.Everything, f => calls.Add("second " + f.Count))));
        })));
        RenderResults.Ok(renderer.Claim(Claim("after", new StyleMatch.InSet(SetId("nothing")), ClaimParts.Everything, _ => calls.Add("after"))));

        Next(bench, rig, renderer, 0f);
        Assert.Equal(new[] { "first", "after" }, calls);

        calls.Clear();
        Next(bench, rig, renderer, 0.1f);
        Assert.Equal(new[] { "after", "second 1" }, calls);
        Assert.Empty(rig.Gpu.Draws);
    }

    [Fact]
    public void A_throwing_callback_leaves_lattices_drawing_submitted_skips_later_claims_and_stays_registered()
    {
        var (bench, rig, _, Frames) = Base();
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)), bench.Draft("StructureCableStraight", new Cell(50, 10, 10)));
        var renderer = rig.Over(bench);
        var throws = 0;
        var later = 0;
        RenderResults.Ok(renderer.Claim(Claim("throws", Frames, ClaimParts.Everything, _ =>
        {
            throws++;
            throw new InvalidOperationException("a consumer's bug");
        })));
        RenderResults.Ok(renderer.Claim(Claim("later", new StyleMatch.InSet(SetId("nothing")), ClaimParts.Everything, _ => later++)));
        RenderRig.Settle(bench, renderer);
        rig.Gpu.NewFrame();

        Assert.Throws<InvalidOperationException>(() => renderer.Frame(0f));
        Assert.Equal(new[] { "cube" }, rig.Gpu.Draws.Select(static d => d.Mesh.ToString()!).ToArray());
        Assert.Equal(0, later);

        rig.Gpu.NewFrame();
        Assert.Throws<InvalidOperationException>(() => renderer.Frame(0.1f));
        Assert.Equal(2, throws);
        Assert.Single(rig.Gpu.Draws);
    }

    [Fact]
    public void A_second_claim_with_the_same_id_is_refused()
    {
        var (bench, rig, _, Frames) = Base();
        var renderer = rig.Over(bench);
        RenderResults.Ok(renderer.Claim(Claim("frames", Frames, ClaimParts.Mesh, static _ => { })));

        var refused = renderer.Claim(Claim("frames", new StyleMatch.Everything(), ClaimParts.Everything, static _ => { }));

        Assert.Equal(ReasonCodes.AlreadyRegistered, refused.Refusal().Code);
    }

    [Fact]
    public void When_a_matchs_answer_changes_its_things_return_to_lattices_drawing()
    {
        var (bench, rig, _, _) = Base();
        var placed = bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var claimed = SetId("claimed");
        var set = Must(bench.Styles.Set(claimed));
        set.Put(placed.Added[0].Id);
        var renderer = rig.Over(bench);
        var count = -1;
        RenderResults.Ok(renderer.Claim(Claim("claimed", new StyleMatch.InSet(claimed), ClaimParts.Everything, f => count = f.Count)));
        Next(bench, rig, renderer, 0f);
        Assert.Equal(1, count);
        Assert.Empty(rig.Gpu.Draws);

        set.Replace(ValueList<GhostId>.Empty);
        Next(bench, rig, renderer, 0.1f);

        Assert.Equal(0, count);
        Assert.Single(rig.Gpu.Draws);
    }

    [Fact]
    public void Claimed_things_out_of_view_are_not_in_the_frame()
    {
        var (bench, rig, _, Frames) = Base();
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)), bench.Draft("StructureFrame", new Cell(10 + 3200, 10, 10)));
        rig.Camera.Planes = FakeCamera.Box(new Vector3(-50, -50, -50), new Vector3(50, 50, 50));
        var renderer = rig.Over(bench);
        var count = -1;
        RenderResults.Ok(renderer.Claim(Claim("frames", Frames, ClaimParts.Everything, f => count = f.Count)));

        Next(bench, rig, renderer, 0f);

        Assert.Equal(1, count);
    }

    [Fact]
    public void A_steady_claimed_frame_allocates_nothing()
    {
        var (bench, rig, _, Frames) = Base();
        var drafts = new GhostDraft[2_000];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = bench.Draft(i % 2 == 0 ? "StructureFrame" : "StructureCableStraight", new Cell(20 * (i % 50), 5, 20 * (i / 50)));
        bench.Place(drafts);
        var renderer = rig.Over(bench);
        var sum = 0f;
        RenderResults.Ok(renderer.Claim(Claim("frames", Frames, ClaimParts.Everything, f =>
        {
            for (var i = 0; i < f.Count; i++)
                sum += f[i].Matrix.m03;
        })));
        Next(bench, rig, renderer, 0f);
        var now = 0f;

        Allocations.AssertNone(() =>
        {
            rig.Gpu.NewFrame();
            now += 0.016f;
            bench.Frame();
            renderer.BeforeCameras();
            renderer.Frame(now);
        }, "a steady claimed frame");
        Assert.NotEqual(0f, sum);
    }
}
