using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.Render;
using Lattice.Render.Gpu;
using Lattice.TestKit.Allocations;
using Lattice.Tests.Styles;
using UnityEngine;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Render;

/// <summary>
/// Submission: one draw per draw group (a piece and look across the
/// chunks in view), look layer, part and submesh; procedural from the group's instance buffer for Lattice's own
/// shaders, with fill and edge in one material; instanced pages of 1023 only for an override; chunks out of view draw
/// nothing; matrices place each part at its ghost's cell and turn; a ghost shows its piece as it will be built; a piece
/// with no meshes is skipped and logged once; a modded piece draws from its own parts; a steady frame allocates nothing.
/// </summary>
public sealed class SubmissionTests
{
    private static List<Batch> Batches(StyleBench bench) =>
        Enumerable.Range(0, bench.Styles.Plan.BatchCount).Select(bench.Styles.Plan.BatchAt).Where(static b => b.Count > 0).ToList();

    private static PrefabKey Piece(StyleBench bench, string name)
    {
        var ghost = bench.PlaceOne(name, new Cell(-5000, -5000, -5000));
        return ((Intent.Build)ghost.Intent).Target.Piece;
    }

    [Fact]
    public void One_draw_per_group_part_and_submesh_across_the_chunks_in_view()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        rig.Pieces.Fallback = new[]
        {
            new RenderPart(new FakeMesh("body", submeshes: 2), Matrix4x4.identity, PartShows.Every),
            new RenderPart(new FakeMesh("trim"), Matrix4x4.identity, PartShows.Every),
        };
        bench.Place(
            bench.Draft("StructureFrame", new Cell(10, 10, 10)),
            bench.Draft("StructureFrame", new Cell(30, 10, 10)),
            bench.Draft("StructureFrame", new Cell(10 + 320, 10, 10)));
        var renderer = rig.Over(bench);

        renderer.Frame(0f);

        Assert.Equal(2, renderer.Counts.Visible);
        Assert.Equal(1, renderer.Counts.Groups);
        Assert.Equal(3, renderer.Counts.Calls);
        Assert.Equal(renderer.Counts.Calls, rig.Gpu.Draws.Count);
        Assert.All(rig.Gpu.Draws, static d => Assert.True(d.Procedural));
        Assert.Equal(3, rig.Gpu.Draws.Single(static d => d.Mesh.ToString() == "trim").Count);
    }

    [Fact]
    public void A_chunk_outside_the_view_draws_nothing()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)), bench.Draft("StructureFrame", new Cell(10 + 3200, 10, 10)));
        rig.Camera.Planes = FakeCamera.Box(new Vector3(-50, -50, -50), new Vector3(50, 50, 50));
        var renderer = rig.Over(bench);

        renderer.Frame(0f);

        var draw = Assert.Single(rig.Gpu.Draws);
        Assert.Equal(1, draw.Count);
        Assert.Equal(2, renderer.Counts.Batches);
    }

    [Fact]
    public void A_procedural_draw_takes_the_whole_group_and_an_overrides_instanced_draws_split_at_1023()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        var drafts = new GhostDraft[2_500];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = bench.Draft("StructureCableStraight", new Cell(5 * (i % 50), 5, 5 * (i / 50)));
        bench.Place(drafts);
        var renderer = rig.Over(bench);

        renderer.Frame(0f);
        Assert.Equal(2_500, Assert.Single(rig.Gpu.Draws).Count);

        RenderResults.Ok(renderer.Materials.Override(MaterialKeys.Ghost, new FakeMaterial("planner-ghost")));
        renderer.Frame(0.1f);
        RenderRig.Settle(bench, renderer);
        rig.Gpu.NewFrame();
        renderer.Frame(0.2f);
        Assert.Equal(new[] { 1023, 1023, 454 }, rig.Gpu.Draws.Select(static d => d.Count).ToArray());
        Assert.All(rig.Gpu.Draws, static d => Assert.False(d.Procedural));
    }

    [Fact]
    public void A_parts_matrix_is_its_ghosts_cell_and_turn_times_its_local_placement()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        var local = Matrix4x4.Translate(new Vector3(0f, 0.5f, 0f));
        rig.Pieces.Fallback = new[] { new RenderPart(new FakeMesh("body"), local, PartShows.Every) };
        var turn = Must(Turn.Of(new SignedAxis(Axis.Y, AxisSign.Plus), new SignedAxis(Axis.X, AxisSign.Minus)));
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 20, 30), turn));
        var renderer = rig.Over(bench);

        renderer.Frame(0f);

        var m = Assert.Single(rig.Gpu.Draws).First;
        Assert.Equal(new Vector4(0f, 1f, 0f, 0f), m.GetColumn(0));
        Assert.Equal(new Vector4(-1f, 0f, 0f, 0f), m.GetColumn(1));
        // The part's origin: the cell (1, 2, 3) m plus its local 0.5 m up, turned so local up points along -X.
        Assert.Equal(new Vector4(0.5f, 2f, 3f, 1f), m * new Vector4(0f, 0f, 0f, 1f));
    }

    [Fact]
    public void A_ghost_shows_its_piece_as_it_will_be_built()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        rig.Pieces.Fallback = new[]
        {
            new RenderPart(new FakeMesh("frame-only"), Matrix4x4.identity, new PartShows.AtStep(0)),
            new RenderPart(new FakeMesh("panel"), Matrix4x4.identity, new PartShows.FromStep(1)),
            new RenderPart(new FakeMesh("bolts"), Matrix4x4.identity, PartShows.Every),
        };
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var renderer = rig.Over(bench);

        renderer.Frame(0f);

        Assert.Equal(new[] { "bolts", "panel" }, rig.Gpu.Draws.Select(static d => d.Mesh.ToString()!).OrderBy(static n => n).ToArray());
    }

    [Fact]
    public void A_modded_piece_draws_from_its_own_parts()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        var modded = Piece(bench, Modded);
        rig.Pieces.With(modded, new RenderPart(new FakeMesh("mod-conduit"), Matrix4x4.identity, PartShows.Every));
        bench.Place(bench.Draft(Modded, new Cell(10, 10, 10)), bench.Draft("StructureFrame", new Cell(30, 10, 10)));
        var renderer = rig.Over(bench);

        renderer.Frame(0f);

        Assert.Equal(2, rig.Gpu.Draws.Single(static d => d.Mesh.ToString() == "mod-conduit").Count);
        Assert.Equal(1, rig.Gpu.Draws.Single(static d => d.Mesh.ToString() == "cube").Count);
    }

    [Fact]
    public void A_piece_with_no_meshes_is_not_drawn_and_logged_once()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        rig.Pieces.Fallback = null;
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var renderer = rig.Over(bench);

        renderer.Frame(0f);
        renderer.Frame(0.1f);

        Assert.Empty(rig.Gpu.Draws);
        Assert.Single(rig.Log);
        Assert.Equal(1, renderer.Counts.UnknownPieces);
    }

    [Fact]
    public void Fill_and_edge_draw_in_one_call_of_lattices_fill_and_edge_material()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Add(LayerOf("edges", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Edge(LineStyle.Dashed, LibraryPalette.Fail))));
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var renderer = rig.Over(bench);

        renderer.Frame(0f);

        var draw = Assert.Single(rig.Gpu.Draws);
        var material = (FakeMaterial)draw.Material;
        Assert.Equal("ghost-edged", material.CopyOf!.Name);
        Assert.True(material.Procedural);
        Assert.Equal(RenderOrder.Default.Ghosts, material.Queue);
        Assert.Equal(new Vector4(1f, 2f, 0f, 0f), draw.Block[ShaderProp.Edge]);
        Assert.True(draw.Block.IsSet[(int)ShaderProp.EdgeColor]);
    }

    [Fact]
    public void With_an_edge_override_fill_and_edge_draw_apart_each_with_its_keys_material()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Add(LayerOf("edges", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Edge(LineStyle.Dashed, LibraryPalette.Fail))));
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var renderer = rig.Over(bench);
        var planner = new FakeMaterial("planner-edge");
        RenderResults.Ok(renderer.Materials.Override(MaterialKeys.Edge, planner));
        renderer.Frame(0f);
        RenderRig.Settle(bench, renderer);
        rig.Gpu.NewFrame();

        renderer.Frame(0.1f);

        var materials = rig.Gpu.Draws.Select(static d => ((FakeMaterial)d.Material).CopyOf!.Name).OrderBy(static n => n).ToArray();
        Assert.Equal(new[] { "ghost", "planner-edge" }, materials);
        var edge = rig.Gpu.Draws.Single(d => ((FakeMaterial)d.Material).CopyOf == planner);
        Assert.False(edge.Procedural);
        Assert.Equal(new Vector4(1f, 2f, 0f, 0f), edge.Block[ShaderProp.Edge]);
        Assert.True(rig.Gpu.Draws.Single(d => d.Material != edge.Material).Procedural);
    }

    [Fact]
    public void Without_a_material_for_a_layer_the_layer_is_skipped_and_counted()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        rig.Bundle.Remove(MaterialKeys.Edge);
        bench.Add(LayerOf("edges", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Edge(LineStyle.Solid, LibraryPalette.Fail))));
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var renderer = rig.Over(bench);

        renderer.Frame(0f);
        rig.Gpu.NewFrame();
        renderer.Frame(0.1f);

        Assert.Equal("ghost", ((FakeMaterial)Assert.Single(rig.Gpu.Draws).Material).CopyOf!.Name);
        Assert.Equal(1, renderer.Counts.SkippedLayers);
    }

    [Fact]
    public void An_override_draws_in_place_of_the_own_material_from_the_next_world_frame_with_no_frame_left_empty()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var renderer = rig.Over(bench);
        renderer.Frame(0f);
        var planner = new FakeMaterial("planner-ghost");

        var installed = RenderResults.Ok(renderer.Materials.Override(MaterialKeys.Ghost, planner));
        rig.Gpu.NewFrame();
        renderer.Frame(0.1f);
        Assert.Equal("ghost", ((FakeMaterial)Assert.Single(rig.Gpu.Draws).Material).CopyOf!.Name);
        bench.Frame();
        rig.Gpu.NewFrame();
        renderer.Frame(0.15f);
        Assert.Same(planner, ((FakeMaterial)Assert.Single(rig.Gpu.Draws).Material).CopyOf);

        installed.Dispose();
        rig.Gpu.NewFrame();
        renderer.Frame(0.2f);
        var own = (FakeMaterial)Assert.Single(rig.Gpu.Draws).Material;
        Assert.Equal("ghost", own.CopyOf!.Name);
        Assert.True(own.Procedural);
    }

    private static StructureRef BuiltFrame(StyleBench bench, Cell at) => new(new ReferenceId(7), bench.T.Catalogue.Named("StructureFrame").Key, at);

    [Fact]
    public void A_remove_ghost_on_a_built_structure_draws_from_the_overlay_copies_at_the_structure()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Place(Lattice.Tests.Board.TestBoard.Draft(new Intent.Remove(BuiltFrame(bench, new Cell(10, 20, 30)))));
        var renderer = rig.Over(bench);

        renderer.Frame(0f);

        var draw = Assert.Single(rig.Gpu.Draws);
        var material = (FakeMaterial)draw.Material;
        Assert.True(material.Overlay);
        Assert.True(material.Procedural);
        Assert.Equal(RenderOrder.Default.Overlays, material.Queue);
        Assert.Equal(1, draw.Count);
        Assert.Equal(new Vector4(1f, 2f, 3f, 1f), draw.First.GetColumn(3));
    }

    [Fact]
    public void A_build_beside_a_remove_with_the_same_look_draws_from_the_placement_copies()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Place(Lattice.Tests.Board.TestBoard.Draft(new Intent.Remove(BuiltFrame(bench, new Cell(10, 20, 30)))), bench.Draft("StructureFrame", new Cell(50, 20, 30)));
        var renderer = rig.Over(bench);

        renderer.Frame(0f);

        Assert.Equal(2, rig.Gpu.Draws.Count);
        var overlay = rig.Gpu.Draws.Single(static d => ((FakeMaterial)d.Material).Overlay);
        var placement = rig.Gpu.Draws.Single(static d => !((FakeMaterial)d.Material).Overlay);
        Assert.Equal(new Vector4(1f, 2f, 3f, 1f), overlay.First.GetColumn(3));
        Assert.Equal(RenderOrder.Default.Ghosts, ((FakeMaterial)placement.Material).Queue);
        Assert.Equal(new Vector4(5f, 2f, 3f, 1f), placement.First.GetColumn(3));
    }

    [Fact]
    public void A_batch_core_pools_after_a_new_generation_stops_drawing()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)));
        var renderer = rig.Over(bench);
        renderer.Frame(0f);
        Assert.Equal(1, renderer.Counts.Instances);

        bench.Add(LayerOf("edges", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), Edge(LineStyle.Dashed, LibraryPalette.Fail))));
        RenderRig.Settle(bench, renderer);
        rig.Gpu.NewFrame();
        renderer.Frame(0.1f);

        Assert.Equal(1, renderer.Counts.Instances);
        Assert.Equal("ghost-edged", ((FakeMaterial)Assert.Single(rig.Gpu.Draws).Material).CopyOf!.Name);
    }

    [Fact]
    public void A_pulse_animates_colour_per_batch_on_the_cpu()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Add(LayerOf("pulse", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), new Style
        {
            Effect = Option.Some<EffectLook>(new EffectLook.Pulse(Must(Period.Create(1f)), Must(Fraction.Create(0.2f)))),
        })));
        bench.Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)), bench.Draft("StructureFrame", new Cell(30, 10, 10)));
        var renderer = rig.Over(bench);

        renderer.Frame(0f);
        var full = Assert.Single(rig.Gpu.Draws).Block[ShaderProp.Color].w;
        rig.Gpu.NewFrame();
        renderer.Frame(0.5f);
        var low = Assert.Single(rig.Gpu.Draws).Block[ShaderProp.Color].w;

        Assert.Equal(0.2f * full, low, 3);
        Assert.Equal(new Vector4(1f, 1f, 0.2f, 0f), rig.Gpu.Draws[0].Block[ShaderProp.Effect]);
    }

    [Fact]
    public void Clip_globals_are_set_before_cameras_and_a_plane_moved_every_frame_is_in_that_frames_globals()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        var renderer = rig.Over(bench);
        var clip = Must(bench.Styles.Clip(Floor(0f)));

        for (var frame = 1; frame <= 5; frame++)
        {
            clip.Move(Floor(frame));
            renderer.BeforeCameras();
            Assert.Equal(new Vector4(0f, 1f, 0f, frame), rig.Gpu.Global(ShaderProp.ClipPlane));
            Assert.Equal(1f, rig.Gpu.Global(ShaderProp.ClipMode).x);
            renderer.Frame(frame);
            Assert.Equal(new Vector4(0f, 1f, 0f, frame), rig.Gpu.Global(ShaderProp.ClipPlane));
        }

        clip.Dispose();
        renderer.BeforeCameras();
        Assert.Equal(0f, rig.Gpu.Global(ShaderProp.ClipMode).x);
    }

    [Fact]
    public void A_steady_frame_allocates_nothing()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Add(LayerOf("pulse", StyleBand.Theme, 0, Rule(new StyleMatch.Everything(), new Style
        {
            Effect = Option.Some<EffectLook>(new EffectLook.Pulse(Must(Period.Create(1f)), Must(Fraction.Create(0.2f)))),
        })));
        var drafts = new GhostDraft[3_000];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = bench.Draft(i % 2 == 0 ? "StructureFrame" : "StructureCableStraight", new Cell(20 * (i % 60), 5, 20 * (i / 60)));
        bench.Place(drafts);
        var renderer = rig.Over(bench);
        var now = 0f;

        Allocations.AssertNone(() =>
        {
            rig.Gpu.NewFrame();
            now += 0.016f;
            bench.Frame();
            renderer.BeforeCameras();
            renderer.Frame(now);
        }, "a steady render frame");
    }

    private static ClipShape Floor(float y) => Must(ClipShape.Plane.Create(new WorldPoint(0f, y, 0f), new WorldVector(0f, 1f, 0f)));
}
