using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;
using Lattice.Render;
using Lattice.Render.Gpu;
using Lattice.Tests.Styles;
using UnityEngine;

namespace Lattice.Tests.Render;

/// <summary>
/// Batch rebuilds in the drawing budget: a large rebuild spreads over frames, a frame draws a batch's last
/// complete pages until its rebuild is done, and Render's rebuilds run before a consumer's drawing work.
/// </summary>
public sealed class RebuildTests
{
    private static GhostDraft[] FramesInOneChunk(StyleBench bench, int count)
    {
        var drafts = new GhostDraft[count];
        for (var i = 0; i < count; i++)
            drafts[i] = bench.Draft("StructureFrame", new Cell(10 + 20 * (i % 16), 10 + 20 * (i / 256), 10 + 20 * (i / 16 % 16)));
        return drafts;
    }

    private static RenderRig TwoParts()
    {
        var rig = new RenderRig();
        rig.Pieces.Fallback = new[]
        {
            new RenderPart(new FakeMesh("body"), Matrix4x4.identity, PartShows.Every),
            new RenderPart(new FakeMesh("trim"), Matrix4x4.identity, PartShows.Every),
        };
        return rig;
    }

    [Fact]
    public void A_large_rebuild_spreads_over_frames_and_never_draws_half_a_batch()
    {
        var bench = new StyleBench();
        var rig = TwoParts();
        bench.Place(FramesInOneChunk(bench, 2_560));
        var renderer = rig.Over(bench);
        renderer.Frame(0f);
        Assert.Equal(2_560, renderer.Counts.Instances);

        // Every clock read costs 1 ms: the 2 ms drawing budget holds one or two steps a frame.
        bench.Clock.Step = TimeSpan.FromMilliseconds(1);
        bench.T.Placed(bench.Draft("StructureFrame", new Cell(10, 210, 10)));
        var seen = new HashSet<int>();
        var rebuildingFrames = 0;
        for (var frame = 0; frame < 2_000 && (frame < 2 || renderer.Rebuilding || bench.Styles.Busy); frame++)
        {
            bench.Frame();
            if (renderer.Rebuilding)
                rebuildingFrames++;
            rig.Gpu.NewFrame();
            renderer.Frame(frame * 0.016f);
            seen.Add(renderer.Counts.Instances);
            Assert.Equal(2, renderer.Counts.Calls);
        }

        Assert.True(rebuildingFrames >= 2, $"the rebuild took {rebuildingFrames} frame(s)");
        Assert.Subset(new HashSet<int> { 2_560, 2_561 }, seen);
        Assert.Equal(2_561, renderer.Counts.Instances);
    }

    [Fact]
    public void Renders_rebuilds_run_before_a_consumers_drawing_work_in_the_same_frame()
    {
        var bench = new StyleBench();
        var rig = TwoParts();
        var renderer = rig.Over(bench);
        var seen = new List<(int Planned, int Drawn)>();
        var consumer = new Probe(() =>
        {
            rig.Gpu.NewFrame();
            renderer.Frame(0f);
            seen.Add((Planned(bench), renderer.Counts.Instances));
        });
        Assert.True(bench.Scheduler.ScheduleConsumer(WorkKind.Drawing, consumer, Priority.High).IsOk);

        bench.T.Placed(FramesInOneChunk(bench, 10));
        for (var i = 0; i < 5; i++)
            bench.Frame();

        // In the frame the styles plan the ghosts, Render has built them by the time the consumer's work runs.
        Assert.Contains((10, 10), seen);
        Assert.All(seen, static s => Assert.Equal(s.Planned, s.Drawn));
    }

    private static int Planned(StyleBench bench)
    {
        var plan = bench.Styles.Plan;
        var count = 0;
        for (var b = 0; b < plan.BatchCount; b++)
            count += plan.BatchAt(b).Count;
        return count;
    }

    private sealed class Probe(Action step) : ISlicedWork
    {
        public StepOutcome Step()
        {
            step();
            return StepOutcome.NextFrame;
        }
    }
}
