using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;
using Lattice.Render;
using Lattice.Render.Claims;
using Lattice.Render.Gpu;
using Lattice.Tests.Styles;

namespace Lattice.Tests.Render;

/// <summary>A sort step reads at most one piece never drawn before; known pieces cost no read and sort 128 batches a step.</summary>
public sealed class PieceReadTests
{
    private static readonly string[] Kinds = { "StructureFrame", "StructureCableStraight", "StructureCableCorner" };

    private static Cell InChunk(int index) => new(10 + 320 * index, 10, 10);

    private sealed class Sorting
    {
        private readonly Rebuilds _rebuilds;

        public Sorting(StyleBench bench, RenderRig rig)
        {
            Bench = bench;
            Rig = rig;
            State = new DrawState(rig.Ports);
            _rebuilds = new Rebuilds(bench.Styles.Plan, rig.Camera, State, new ClaimTable(bench.Styles.Watch));
        }

        public StyleBench Bench { get; }

        public RenderRig Rig { get; }

        public DrawState State { get; }

        /// <summary>Steps until the sort has seen every batch, noting the most pieces any one step read; returns the steps taken.</summary>
        public int SortSteps(out int mostReadInAStep, int? visible = null)
        {
            mostReadInAStep = 0;
            var plan = Bench.Styles.Plan;
            for (var steps = 1; steps < 10_000; steps++)
            {
                var before = Rig.Pieces.Asks;
                _rebuilds.Step();
                mostReadInAStep = System.Math.Max(mostReadInAStep, Rig.Pieces.Asks - before);
                if (_rebuilds.Visible == (visible ?? plan.BatchCount))
                    return steps;
            }

            throw new Xunit.Sdk.XunitException("the sort never ended");
        }

        /// <summary>Steps until Render has nothing left to do.</summary>
        public void Drain()
        {
            for (var i = 0; i < 10_000; i++)
                if (_rebuilds.Step() == StepOutcome.NextFrame && !_rebuilds.Busy)
                    return;
            throw new Xunit.Sdk.XunitException("the rebuilds never settled");
        }
    }

    [Fact]
    public void A_plan_of_new_piece_kinds_reads_one_piece_a_sort_step()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Place(bench.Draft(Kinds[0], InChunk(0)), bench.Draft(Kinds[1], InChunk(1)), bench.Draft(Kinds[2], InChunk(2)));
        var sorting = new Sorting(bench, rig);

        var steps = sorting.SortSteps(out var most);

        Assert.True(steps >= 3, $"the sort took {steps} step(s)");
        Assert.Equal(1, most);
        Assert.Equal(3, rig.Pieces.Asks);
        Assert.Equal(3, sorting.State.Groups.Count);
    }

    [Fact]
    public void Pieces_already_read_cost_no_read_and_sort_128_batches_a_step()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Place(bench.Draft(Kinds[0], InChunk(0)));
        var sorting = new Sorting(bench, rig);
        sorting.Drain();
        Assert.Equal(1, rig.Pieces.Asks);

        var more = new GhostDraft[299];
        for (var i = 0; i < more.Length; i++)
            more[i] = bench.Draft(Kinds[0], InChunk(i + 1));
        bench.Place(more);

        var steps = sorting.SortSteps(out var most);

        Assert.Equal(3, steps);
        Assert.Equal(0, most);
        Assert.Equal(1, rig.Pieces.Asks);
    }

    [Fact]
    public void A_piece_that_fails_to_read_is_skipped_and_the_others_are_drawn()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        rig.Pieces.Fallback = null;
        bench.Place(bench.Draft(Kinds[0], InChunk(0)), bench.Draft(Kinds[1], InChunk(1)), bench.Draft(Kinds[2], InChunk(2)));
        rig.Pieces.With(bench.Styles.Plan.BatchAt(0).Piece, new RenderPart(new FakeMesh("body"), UnityEngine.Matrix4x4.identity, PartShows.Every));
        var sorting = new Sorting(bench, rig);

        var steps = sorting.SortSteps(out var most, visible: 1);

        Assert.True(steps >= 3);
        Assert.Equal(1, most);
        Assert.Single(sorting.State.Groups);
        Assert.Equal(2, sorting.State.UnknownPieces);
    }

    [Fact]
    public void A_plan_edited_during_the_reads_converges_to_every_piece_drawn()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Place(bench.Draft(Kinds[0], InChunk(0)), bench.Draft(Kinds[1], InChunk(1)));
        var sorting = new Sorting(bench, rig);
        sorting.SortSteps(out _);

        bench.Place(bench.Draft(Kinds[2], InChunk(2)));
        sorting.Drain();

        Assert.Equal(3, sorting.State.Groups.Count);
        Assert.Equal(3, rig.Pieces.Asks);
    }

    [Fact]
    public void The_drawing_after_the_reads_is_one_group_per_piece()
    {
        var bench = new StyleBench();
        var rig = new RenderRig();
        bench.Place(bench.Draft(Kinds[0], InChunk(0)), bench.Draft(Kinds[1], InChunk(1)), bench.Draft(Kinds[2], InChunk(2)), bench.Draft(Kinds[0], InChunk(3)));
        var renderer = rig.Over(bench);

        renderer.Frame(0f);

        Assert.Equal(3, rig.Gpu.Draws.Count);
        var instances = 0;
        foreach (var draw in rig.Gpu.Draws)
            instances += draw.Count;
        Assert.Equal(4, instances);
    }
}
