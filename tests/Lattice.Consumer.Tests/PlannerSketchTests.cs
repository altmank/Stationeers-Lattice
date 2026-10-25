using System;
using Lattice.Api;
using static Lattice.Consumer.Tests.Make;

namespace Lattice.Consumer.Tests;

/// <summary>
/// The shape a planner UI needs for drawing a very large plan, against the contract alone: overlay work queued in Lattice's
/// drawing budget, a few pieces per step, cancelled when the plan is hidden.
/// </summary>
internal sealed class OverlaySketch(IGhostSystem lattice, int pieces)
{
    private const int PiecesPerStep = 64;

    private IDisposable? _drawing;

    public int Drawn { get; private set; }

    public void Show() =>
        _drawing = Ok(lattice.Slices.Schedule(new SlicedWorkDescriptor(Ok(SliceId.Create(Mod("planner"), "plan-overlay")), DrawSome)
        {
            Priority = SlicePriority.High,
        }));

    public void Hide() => _drawing?.Dispose();

    private bool DrawSome()
    {
        Drawn = Math.Min(pieces, Drawn + PiecesPerStep);
        return Drawn < pieces;
    }
}

public sealed class PlannerSketchTests
{
    [Fact]
    public void Overlay_work_runs_in_steps_until_drawn()
    {
        var lattice = new StubSystem(isHost: true);
        var overlay = new OverlaySketch(lattice, 1_000);

        overlay.Show();
        for (var frame = 0; frame < 20 && lattice.Drawing.Count > 0; frame++)
            lattice.RunDrawingFrame();

        Assert.Equal(1_000, overlay.Drawn);
        Assert.Empty(lattice.Drawing);
    }

    [Fact]
    public void Hiding_the_plan_cancels_its_drawing()
    {
        var lattice = new StubSystem(isHost: true);
        var overlay = new OverlaySketch(lattice, 1_000);
        overlay.Show();
        lattice.RunDrawingFrame();

        overlay.Hide();
        lattice.RunDrawingFrame();

        Assert.Equal(64, overlay.Drawn);
    }
}
