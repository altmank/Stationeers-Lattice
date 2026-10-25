using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.Render.Marks;
using Lattice.TestKit.Allocations;
using Lattice.Tests.Board;
using UnityEngine;
using static Lattice.Tests.Render.MarkScene;

namespace Lattice.Tests.Render;

/// <summary>
/// Flow arrows (<see cref="Style.FlowArrows"/>) and waits links (<see cref="Style.WaitsLinks"/>). A device's ports with
/// a role carry an arrow into it at an input and out of it at an output; a run piece takes its flow from the run it is
/// in, away from a device output it reaches and toward a device input, and has no arrow when its flow is not known (a
/// lone run, a run fed from both ends). A ghost that waits for others is linked to each of them; the links follow a
/// finding that changes later. Neither draws when its look does not show it; a steady frame with both allocates nothing.
/// </summary>
public sealed class FlowWaitsTests
{
    private const string Apc = "StructureAreaPowerControl";
    private const string Cable = "StructureCableStraight";
    private const string Pipe = "StructurePipeStraight";

    private static readonly Turn AlongX = TestBoard.TurnSending(Direction.ZPlus, Direction.XPlus, new CubeGrid());
    private static readonly Turn Backward = TestBoard.TurnSending(Direction.XPlus, Direction.XMinus, new CubeGrid());

    private static MarkScene Flow(ShowMode shown = ShowMode.Shown) => new(new Style { FlowArrows = Option.Some(shown) });

    private static MarkScene Waits(ShowMode shown = ShowMode.Shown) => new(new Style { WaitsLinks = Option.Some(shown) });

    /// <summary>The arrow heads drawn, as centres in centimetres (the heads are the cube-shaped flow marks).</summary>
    private static List<(int X, int Y, int Z)> Heads(MarkScene scene) =>
        scene.Rig.Gpu.Marks.Where(static m => Math.Abs(m.m11 - MarkShapes.HeadSize) < 1e-4f && Math.Abs(m.m00 - MarkShapes.HeadSize) < 1e-4f)
            .Select(Centre).OrderBy(static c => c.X).ThenBy(static c => c.Y).ThenBy(static c => c.Z).ToList();

    private static (int X, int Y, int Z) Centre(Matrix4x4 m) =>
        ((int)Math.Round(m.m03 * 100f), (int)Math.Round(m.m13 * 100f), (int)Math.Round(m.m23 * 100f));

    /// <summary>A head's centre: a port's cell centre moved along x by so many centimetres.</summary>
    private static (int X, int Y, int Z) HeadAt(Cell cell, int alongX) =>
        ((int)Math.Round(cell.X * 10f) + alongX, (int)Math.Round(cell.Y * 10f), (int)Math.Round(cell.Z * 10f));

    // An outward head sits beyond the face (25 + 15 cm), an inward one inside it (25 - 15 cm).
    private const int Out = 40;
    private const int In = 10;

    [Fact]
    public void A_devices_ports_with_a_role_point_into_it_at_the_input_and_out_at_the_output()
    {
        var scene = Flow();
        scene.Bench.Place(scene.Bench.Draft(Apc, Origin));
        scene.Open();

        scene.Draw();

        Assert.Equal(new[] { HeadAt(Origin, -In), HeadAt(Origin, Out) }, Heads(scene));
        Assert.Equal(2, scene.Count(MarkKind.Shaft));
        Assert.Equal(2, scene.Count(MarkKind.Head));
    }

    [Fact]
    public void A_run_from_a_devices_output_flows_away_from_it_along_every_piece()
    {
        var scene = Flow();
        var first = Along(Face.PlusX, 1);
        var second = Along(Face.PlusX, 2);
        scene.Bench.Place(scene.Bench.Draft(Apc, Origin), scene.Bench.Draft(Cable, first, AlongX), scene.Bench.Draft(Cable, second, AlongX));
        scene.Open();

        scene.Draw();

        Assert.Equal(new[] { HeadAt(Origin, -In), HeadAt(Origin, Out), HeadAt(first, Out), HeadAt(second, Out) }, Heads(scene));
    }

    [Fact]
    public void A_run_into_a_devices_input_flows_toward_it()
    {
        var scene = Flow();
        var run = Along(Face.MinusX, 1);
        scene.Bench.Place(scene.Bench.Draft(Apc, Origin), scene.Bench.Draft(Cable, run, AlongX));
        scene.Open();

        scene.Draw();

        Assert.Contains(HeadAt(run, Out), Heads(scene));
        Assert.Equal(3, Heads(scene).Count);
    }

    [Fact]
    public void A_lone_run_has_no_known_flow_and_no_arrow()
    {
        var scene = Flow();
        scene.Bench.Place(scene.Bench.Draft(Cable, Origin, AlongX), scene.Bench.Draft(Pipe, Along(Face.PlusZ, 4)));
        scene.Open();

        scene.Draw();

        Assert.Empty(scene.Rig.Gpu.Marks);
        Assert.NotEmpty(scene.Rig.Gpu.Draws);
    }

    [Fact]
    public void A_run_fed_from_both_ends_has_no_known_flow()
    {
        var scene = Flow();
        var run = Along(Face.PlusX, 1);
        scene.Bench.Place(scene.Bench.Draft(Apc, Origin), scene.Bench.Draft(Cable, run, AlongX), scene.Bench.Draft(Apc, Along(Face.PlusX, 2), Backward));
        scene.Open();

        scene.Draw();

        var heads = Heads(scene);
        Assert.Equal(4, heads.Count);
        Assert.DoesNotContain(HeadAt(run, Out), heads);
        Assert.DoesNotContain(HeadAt(run, -Out), heads);
    }

    [Fact]
    public void Flow_not_shown_draws_no_arrows()
    {
        var scene = Flow(ShowMode.None);
        scene.Bench.Place(scene.Bench.Draft(Apc, Origin), scene.Bench.Draft(Cable, Along(Face.PlusX, 1), AlongX));
        scene.Open();

        scene.Draw();

        Assert.Empty(scene.Rig.Gpu.Marks);
    }

    [Fact]
    public void A_ghost_that_waits_is_linked_to_each_ghost_it_waits_for()
    {
        var scene = Waits();
        var placed = scene.Bench.Place(scene.Bench.Draft(Pipe, Origin), scene.Bench.Draft(Pipe, Along(Face.PlusX, 4)), scene.Bench.Draft(Pipe, Along(Face.PlusZ, 4)));
        var (a, b, c) = (placed.Added[0], placed.Added[1], placed.Added[2]);
        scene.Bench.Verdicts.Raise((a.Id, new Finding.WaitsFor(a.Id, ValueList.From(new[] { b.Id, c.Id }))));
        scene.Bench.Settle();
        scene.Open();

        scene.Draw();

        var links = scene.Rig.Gpu.Marks.Where(static m => Math.Abs(m.m00 - MarkShapes.LinkHead) > 1e-4f).Select(Centre).ToList();
        Assert.Equal(2, links.Count);
        Assert.Contains(Mid(Origin, Along(Face.PlusX, 4)), links);
        Assert.Contains(Mid(Origin, Along(Face.PlusZ, 4)), links);
        Assert.Equal(2, scene.Count(MarkKind.Link));
        Assert.Equal(2, scene.Count(MarkKind.Tip));
    }

    [Fact]
    public void Links_follow_a_finding_that_changes_later()
    {
        var scene = Waits();
        var placed = scene.Bench.Place(scene.Bench.Draft(Pipe, Origin), scene.Bench.Draft(Pipe, Along(Face.PlusX, 4)));
        var (a, b) = (placed.Added[0], placed.Added[1]);
        scene.Open();
        scene.Draw();
        Assert.Empty(scene.Rig.Gpu.Marks);

        scene.Bench.T.Events.Queue(new AnswersReadyEvent(new Revision(1)));
        scene.Bench.Verdicts.Raise((b.Id, new Finding.WaitsFor(b.Id, ValueList.Of(a.Id))));
        scene.Draw();

        Assert.Equal(2, scene.Rig.Gpu.Marks.Count);
    }

    [Fact]
    public void Waits_not_shown_draws_no_links()
    {
        var scene = Waits(ShowMode.None);
        var placed = scene.Bench.Place(scene.Bench.Draft(Pipe, Origin), scene.Bench.Draft(Pipe, Along(Face.PlusX, 4)));
        scene.Bench.Verdicts.Raise((placed.Added[0].Id, new Finding.WaitsFor(placed.Added[0].Id, ValueList.Of(placed.Added[1].Id))));
        scene.Bench.Settle();
        scene.Open();

        scene.Draw();

        Assert.Empty(scene.Rig.Gpu.Marks);
    }

    [Fact]
    public void A_steady_frame_with_flow_arrows_and_waits_links_allocates_nothing()
    {
        var scene = new MarkScene(new Style { FlowArrows = Option.Some(ShowMode.Shown), WaitsLinks = Option.Some(ShowMode.Shown) });
        var drafts = new List<GhostDraft>(1_200);
        for (var i = 0; i < 600; i++)
        {
            var at = new Cell(Origin.X + 40 * (i % 30), Origin.Y, Origin.Z + 40 * (i / 30));
            drafts.Add(scene.Bench.Draft(Apc, at));
            drafts.Add(scene.Bench.Draft(Cable, at.Neighbour(Face.PlusX, PieceGrid.Small), AlongX));
        }

        var placed = scene.Bench.Place(drafts.ToArray()).Added;
        var waits = new (GhostId, Finding?)[100];
        for (var i = 0; i < waits.Length; i++)
            waits[i] = (placed[i].Id, new Finding.WaitsFor(placed[i].Id, ValueList.Of(placed[i + 100].Id)));
        scene.Bench.Verdicts.Raise(waits);
        scene.Bench.Settle();
        scene.Open();
        scene.Draw();
        Assert.Equal(600 * 3, scene.Count(MarkKind.Shaft));
        Assert.Equal(600 * 3, scene.Count(MarkKind.Head));
        Assert.Equal(100, scene.Count(MarkKind.Link));
        Assert.Equal(100, scene.Count(MarkKind.Tip));
        var now = 0f;

        Allocations.AssertNone(() =>
        {
            scene.Rig.Gpu.NewFrame();
            now += 0.016f;
            scene.Bench.Frame();
            scene.Renderer!.BeforeCameras();
            scene.Renderer.Frame(now);
        }, "a steady render frame with flow arrows and waits links");
    }

    private static (int X, int Y, int Z) Mid(Cell a, Cell b) =>
        ((int)Math.Round((a.X + b.X) * 5f), (int)Math.Round((a.Y + b.Y) * 5f), (int)Math.Round((a.Z + b.Z) * 5f));
}
