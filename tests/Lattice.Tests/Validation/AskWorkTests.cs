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

    private const int Side = 45;

    /// <summary>
    /// How many times a measurement is taken. The first run in a test process can pay one-off runtime work (loading
    /// types, running static constructors, compiling methods) inside a measured step; that lands in one run at most,
    /// while an allocation in the code under test shows in every run, so the best run is kept.
    /// </summary>
    private const int Runs = 3;

    /// <summary>What one run of the large plan's asks measured, step by step.</summary>
    private readonly record struct Answering(long Most, int RoomSteps, bool AnsweredBeforeRoom, int Asks)
    {
        public bool Holds => Most <= MostPerStep && RoomSteps == 1 && !AnsweredBeforeRoom;
    }

    private static ValidationBench LargePlan()
    {
        var bench = new ValidationBench(clock: new OneStepClock()).Start();
        var pieces = new List<(string, Cell)>(Side * Side);
        for (var i = 0; i < Side; i++)
            for (var j = 0; j < Side; j++)
                pieces.Add(("StructureFrameIron", Large(2 * i, 0, 2 * j)));
        bench.Ghosts(pieces.ToArray());
        return bench;
    }

    private static Answering AnswerAll()
    {
        var bench = LargePlan();

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
        for (var step = 0; step < Side * Side + 8; step++)
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

        return new Answering(most, roomSteps, answeredBeforeRoom, bench.Placement.Calls.Count - 4097);
    }

    private static long TakeIn()
    {
        var bench = LargePlan();
        bench.Events.Flush();
        bench.Service.Frame();
        bench.Worker.RunAll();

        var before = GC.GetAllocatedBytesForCurrentThread();
        bench.Service.Frame();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(bench.Service.Busy);
        return bytes;
    }

    [Fact]
    public void Answering_the_asks_of_a_large_plan_allocates_only_small_records_in_each_step()
    {
        var run = AnswerAll();
        for (var again = 1; again < Runs && !run.Holds; again++)
            run = AnswerAll();

        Assert.True(run.Asks >= Side * Side, $"{run.Asks} asks answered");
        Assert.True(run.Most <= MostPerStep, $"a step allocated {run.Most} bytes in each of {Runs} runs");
        Assert.Equal(1, run.RoomSteps);
        Assert.False(run.AnsweredBeforeRoom, "an ask was answered before room was taken");
    }

    [Fact]
    public void Taking_in_a_large_runs_asks_outside_the_slices_allocates_nothing_the_size_of_the_plan()
    {
        var bytes = TakeIn();
        for (var again = 1; again < Runs && bytes > MostPerStep; again++)
            bytes = Math.Min(bytes, TakeIn());

        Assert.True(bytes <= MostPerStep, $"taking in {Side * Side} asks allocated {bytes} bytes in each of {Runs} runs");
    }

    /// <summary>A clock that moves a whole second each read, so every slice runs exactly one step.</summary>
    private sealed class OneStepClock : IClock
    {
        private TimeSpan _now;

        public TimeSpan Now => _now += TimeSpan.FromSeconds(1);
    }
}
