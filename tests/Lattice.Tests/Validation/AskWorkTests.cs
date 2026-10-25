using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Validation;

/// <summary>
/// The main thread's answers to the worker's asks (never stall a frame). A run's asks are
/// taken in at once outside the slices and counted against the book in the checking slices; room for their answers is
/// taken in one step before the first is answered, so answering one ask never grows a collection the size of the plan
/// inside a checking step (a large collection grown one ask at a time copies itself and, past 85 KB, asks the collector
/// for a large block: the 7-60 ms frames of the 10,000-ghost timing test).
/// </summary>
public sealed class AskWorkTests
{
    /// <summary>A step answers one ask: a few small records, never a copy of a plan-sized collection.</summary>
    private const long MostPerStep = 2048;

    [Fact]
    public void Answering_the_asks_of_a_large_plan_allocates_only_small_records_in_each_step()
    {
        var bench = new ValidationBench(clock: new OneStepClock()).Start();
        const int side = 45;
        var pieces = new List<(string, Cell)>(side * side);
        for (var i = 0; i < side; i++)
            for (var j = 0; j < side; j++)
                pieces.Add(("StructureFrameIron", Large(2 * i, 0, 2 * j)));
        bench.Ghosts(pieces.ToArray());

        // The fake records every call it answers; fill its record past the asks so its own growth is not counted.
        for (var i = 0; i < 4097; i++)
            bench.Placement.Check(new Placement(Pieces.Named("StructureFrameIron").Key, Large(0, 0, 0), default));

        bench.Events.Flush();
        bench.Service.Frame();
        bench.Worker.RunAll();
        bench.Service.Frame();
        Assert.True(bench.Service.Busy);

        var most = 0L;
        var roomSteps = 0;
        var answeredBeforeRoom = false;
        for (var step = 0; step < side * side + 8; step++)
        {
            var calls = bench.Placement.Calls.Count;
            var before = GC.GetAllocatedBytesForCurrentThread();
            bench.Scheduler.RunFrame();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            var answered = bench.Placement.Calls.Count > calls;
            if (!answered && bytes > MostPerStep)
                roomSteps++;
            else
                most = Math.Max(most, bytes);
            answeredBeforeRoom |= answered && roomSteps == 0;
        }

        Assert.True(bench.Placement.Calls.Count >= 4097 + side * side, $"{bench.Placement.Calls.Count - 4097} asks answered");
        Assert.True(most <= MostPerStep, $"a step allocated {most} bytes");
        Assert.Equal(1, roomSteps);
        Assert.False(answeredBeforeRoom, "an ask was answered before room was taken");
    }

    [Fact]
    public void Taking_in_a_large_runs_asks_outside_the_slices_allocates_nothing_the_size_of_the_plan()
    {
        var bench = new ValidationBench(clock: new OneStepClock()).Start();
        const int side = 45;
        var pieces = new List<(string, Cell)>(side * side);
        for (var i = 0; i < side; i++)
            for (var j = 0; j < side; j++)
                pieces.Add(("StructureFrameIron", Large(2 * i, 0, 2 * j)));
        bench.Ghosts(pieces.ToArray());
        bench.Events.Flush();
        bench.Service.Frame();
        bench.Worker.RunAll();

        var before = GC.GetAllocatedBytesForCurrentThread();
        bench.Service.Frame();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(bench.Service.Busy);
        Assert.True(bytes <= MostPerStep, $"taking in {side * side} asks allocated {bytes} bytes");
    }

    /// <summary>A clock that moves a whole second each read, so every slice runs exactly one step.</summary>
    private sealed class OneStepClock : IClock
    {
        private TimeSpan _now;

        public TimeSpan Now => _now += TimeSpan.FromSeconds(1);
    }
}
