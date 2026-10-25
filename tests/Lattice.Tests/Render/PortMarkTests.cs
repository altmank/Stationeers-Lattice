using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Render.Marks;
using Lattice.TestKit.Allocations;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Board;
using Lattice.Tests.Styles;
using UnityEngine;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Render;

/// <summary>
/// Port and open-end marks (<see cref="Style.Ports"/>, <see cref="Style.OpenEnds"/>): a look that shows ports marks
/// every port of each drawn ghost, one that shows open ends marks only the ports nothing joins; a port a neighbouring
/// ghost's or built structure's port meets on a shared network is joined, a built structure a remove ghost takes away
/// joins nothing; a modded piece's ports are marked from its catalogue facts; marks follow a neighbour placed or
/// removed later; an overlay on a built structure marks that structure's ports; a steady frame with marks allocates
/// nothing.
/// </summary>
public sealed class PortMarkTests
{
    private const string Pipe = "StructurePipeStraight";
    private const string Tank = "StructureTankSmall";

    private static readonly Cell Origin = MarkScene.Origin;

    private static Cell Along(Face face, int cells) => MarkScene.Along(face, cells);

    private sealed class Scene(ShowMode ports, ShowMode openEnds)
        : MarkScene(new Style { Ports = Option.Some(ports), OpenEnds = Option.Some(openEnds) });

    /// <summary>The centre of a mark: its matrix's translation, in decimetres rounded, for comparing with cells.</summary>
    private static (int X, int Y, int Z) Centre(Matrix4x4 m) =>
        ((int)Math.Round(m.m03 * 100f), (int)Math.Round(m.m13 * 100f), (int)Math.Round(m.m23 * 100f));

    /// <summary>Where an open-end mark of a port at a cell, facing a way, is centred: on the cell's face, in centimetres.</summary>
    private static (int X, int Y, int Z) OpenAt(Cell from, Face toward)
    {
        var d = MarkShapes.Unit(toward);
        return ((int)Math.Round(from.X * 10f + d.x * 25f), (int)Math.Round(from.Y * 10f + d.y * 25f), (int)Math.Round(from.Z * 10f + d.z * 25f));
    }

    [Fact]
    public void Ports_shown_marks_every_port_of_each_drawn_ghost_and_open_ends_none_when_not_shown()
    {
        var scene = new Scene(ShowMode.Shown, ShowMode.None);
        scene.Bench.Place(scene.Bench.Draft(Pipe, Origin), scene.Bench.Draft(Pipe, Along(Face.PlusZ, 1)));
        scene.Open();

        scene.Draw();

        Assert.Equal(4, scene.Count(MarkKind.Port));
        Assert.Equal(0, scene.Count(MarkKind.Open));
    }

    [Fact]
    public void Open_ends_shown_marks_only_the_ports_nothing_joins()
    {
        var scene = new Scene(ShowMode.None, ShowMode.Shown);
        var far = Along(Face.PlusZ, 1);
        scene.Bench.Place(scene.Bench.Draft(Pipe, Origin), scene.Bench.Draft(Pipe, far));
        scene.Open();

        scene.Draw();

        Assert.Equal(0, scene.Count(MarkKind.Port));
        var open = scene.Rig.Gpu.Marks.Select(Centre).OrderBy(static c => c.Z).ToList();
        Assert.Equal(new[] { OpenAt(Origin, Face.MinusZ), OpenAt(far, Face.PlusZ) }, open);
    }

    [Fact]
    public void Neither_shown_draws_no_marks()
    {
        var scene = new Scene(ShowMode.None, ShowMode.None);
        scene.Bench.Place(scene.Bench.Draft(Pipe, Origin));
        scene.Open();

        scene.Draw();

        Assert.Empty(scene.Rig.Gpu.Marks);
        Assert.NotEmpty(scene.Rig.Gpu.Draws);
    }

    [Fact]
    public void Without_a_glow_open_ends_take_the_fill_colour_and_port_marks_the_edge_colour()
    {
        var scene = new Scene(ShowMode.Shown, ShowMode.Shown);
        scene.Bench.Add(LayerOf("edge", StyleBand.Theme, 1, Rule(new StyleMatch.Everything(), Edge(LineStyle.Solid, LibraryPalette.Fail))));
        scene.Bench.Place(scene.Bench.Draft(Pipe, Origin));
        scene.Open();

        scene.Draw();

        var fill = scene.Rig.Gpu.Draws.First(static d => d.Procedural).Block[Lattice.Render.Gpu.ShaderProp.Color];
        var port = scene.DrawOf(MarkKind.Port).Block[Lattice.Render.Gpu.ShaderProp.Color];
        var open = scene.DrawOf(MarkKind.Open).Block[Lattice.Render.Gpu.ShaderProp.Color];
        Assert.Equal((fill.x, fill.y, fill.z), (open.x, open.y, open.z));
        Assert.NotEqual((open.x, open.y, open.z), (port.x, port.y, port.z));
        Assert.True(open.w >= 0.85f && port.w >= 0.85f);
    }

    [Fact]
    public void Both_shown_marks_every_port_and_the_open_ones_again()
    {
        var scene = new Scene(ShowMode.Shown, ShowMode.Shown);
        scene.Bench.Place(scene.Bench.Draft(Pipe, Origin), scene.Bench.Draft(Pipe, Along(Face.PlusZ, 1)));
        scene.Open();

        scene.Draw();

        Assert.Equal(4, scene.Count(MarkKind.Port));
        Assert.Equal(2, scene.Count(MarkKind.Open));
        Assert.All(scene.MarkDraws(""), static d => Assert.StartsWith("solid", d.Material.ToString()));
    }

    [Fact]
    public void A_ports_mark_bar_points_out_of_its_cell_toward_the_face_it_leaves_through()
    {
        var scene = new Scene(ShowMode.Shown, ShowMode.None);
        scene.Bench.Place(scene.Bench.Draft(Pipe, Origin));
        scene.Open();

        scene.Draw();

        var marks = scene.Rig.Gpu.Marks.OrderBy(static m => m.m23).ToList();
        Assert.Equal(2, marks.Count);
        Assert.Equal(Origin.Z * 0.1f - 0.15f, marks[0].m23, 4);
        Assert.Equal(Origin.Z * 0.1f + 0.15f, marks[1].m23, 4);
        Assert.Equal(MarkShapes.PortLength, marks[1].m22, 4);
        Assert.Equal(MarkShapes.PortWidth, marks[1].m00, 4);
    }

    [Fact]
    public void A_runs_end_at_a_built_device_is_not_open()
    {
        var scene = new Scene(ShowMode.None, ShowMode.Shown);
        var tankAt = Along(Face.PlusY, 1);
        scene.Bench.World.Load(new StructureInfo(new StructureRef(new ReferenceId(7), scene.Key(Tank), tankAt), default, new BuildState.Built(),
            Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None));
        var upright = TestBoard.TurnSending(Direction.ZPlus, Direction.YPlus, new CubeGrid());
        scene.Bench.Place(scene.Bench.Draft(Pipe, Origin, upright));
        scene.Open();

        scene.Draw();

        var open = Assert.Single(scene.Rig.Gpu.Marks);
        Assert.Equal(OpenAt(Origin, Face.MinusY), Centre(open));
    }

    [Fact]
    public void A_built_structure_a_remove_ghost_takes_away_joins_nothing()
    {
        var scene = new Scene(ShowMode.None, ShowMode.Shown);
        var tankAt = Along(Face.PlusY, 1);
        var tank = new StructureRef(new ReferenceId(7), scene.Key(Tank), tankAt);
        scene.Bench.World.Load(new StructureInfo(tank, default, new BuildState.Built(), Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None));
        var upright = TestBoard.TurnSending(Direction.ZPlus, Direction.YPlus, new CubeGrid());
        scene.Bench.Place(scene.Bench.Draft(Pipe, Origin, upright), TestBoard.Draft(new Intent.Remove(tank)));
        scene.Open();

        scene.Draw();

        Assert.Contains(OpenAt(Origin, Face.PlusY), scene.Rig.Gpu.Marks.Select(Centre));
    }

    [Fact]
    public void A_neighbour_placed_later_closes_the_end_it_meets_and_its_removal_opens_it_again()
    {
        var scene = new Scene(ShowMode.None, ShowMode.Shown);
        scene.Bench.Place(scene.Bench.Draft(Pipe, Origin));
        scene.Open();
        scene.Draw();
        Assert.Equal(2, scene.Rig.Gpu.Marks.Count);

        var neighbour = Assert.Single(scene.Bench.Place(scene.Bench.Draft(Pipe, Along(Face.PlusZ, 1))).Added);
        scene.Draw();
        Assert.Equal(2, scene.Rig.Gpu.Marks.Count);
        Assert.DoesNotContain(OpenAt(Origin, Face.PlusZ), scene.Rig.Gpu.Marks.Select(Centre));

        TestBoard.Applied(scene.Bench.T.Apply(new Command.Remove(TestBoard.Ids(neighbour.Id))));
        scene.Draw();
        Assert.Contains(OpenAt(Origin, Face.PlusZ), scene.Rig.Gpu.Marks.Select(Centre));
    }

    [Fact]
    public void A_modded_pieces_ports_are_marked_from_its_catalogue_facts()
    {
        var scene = new Scene(ShowMode.Shown, ShowMode.Shown);
        scene.Bench.Place(scene.Bench.Draft(Modded, Origin));
        scene.Open();

        scene.Draw();

        Assert.Equal(2, scene.Count(MarkKind.Port));
        Assert.Equal(2, scene.Count(MarkKind.Open));
    }

    [Fact]
    public void Pipes_on_different_networks_do_not_join()
    {
        var scene = new Scene(ShowMode.None, ShowMode.Shown);
        scene.Bench.Place(scene.Bench.Draft(Pipe, Origin), scene.Bench.Draft("StructureCableStraight", Along(Face.PlusZ, 1)));
        scene.Open();

        scene.Draw();

        Assert.Equal(4, scene.Rig.Gpu.Marks.Count);
    }

    [Fact]
    public void An_overlay_on_a_built_structure_marks_that_structures_ports()
    {
        var scene = new Scene(ShowMode.Shown, ShowMode.None);
        var pipe = new StructureRef(new ReferenceId(9), scene.Key(Pipe), Origin);
        scene.Bench.World.Load(new StructureInfo(pipe, default, new BuildState.Built(), Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None));
        scene.Bench.Place(TestBoard.Draft(new Intent.Remove(pipe)));
        scene.Open();

        scene.Draw();

        var marks = scene.Rig.Gpu.Marks.Select(static m => m.m23).OrderBy(static z => z).ToList();
        Assert.Equal(2, marks.Count);
        Assert.Equal(Origin.Z * 0.1f - 0.15f, marks[0], 4);
        Assert.Equal(Origin.Z * 0.1f + 0.15f, marks[1], 4);
    }

    [Fact]
    public void A_steady_frame_with_marks_allocates_nothing()
    {
        var scene = new Scene(ShowMode.Shown, ShowMode.Shown);
        var drafts = new GhostDraft[2_000];
        for (var i = 0; i < drafts.Length; i++)
            drafts[i] = scene.Bench.Draft(i % 2 == 0 ? Pipe : Tank, new Cell(Origin.X + 20 * (i % 50), Origin.Y, Origin.Z + 20 * (i / 50)));
        scene.Bench.Place(drafts);
        scene.Open();
        scene.Draw();
        Assert.Equal(3_000, scene.Count(MarkKind.Port));
        var now = 0f;

        Allocations.AssertNone(() =>
        {
            scene.Rig.Gpu.NewFrame();
            now += 0.016f;
            scene.Bench.Frame();
            scene.Renderer!.BeforeCameras();
            scene.Renderer.Frame(now);
        }, "a steady render frame with port and open-end marks");
    }
}
