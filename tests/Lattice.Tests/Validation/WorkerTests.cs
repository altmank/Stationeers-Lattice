using System;
using System.Threading;
using Lattice.Core.Validation;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Validation;

/// <summary>Lattice's own worker and the world's end.</summary>
public sealed class WorkerTests
{
    [Fact]
    public void The_worker_runs_posted_work_in_order_on_its_own_named_thread_and_stop_joins_it()
    {
        var worker = new ThreadWorker();
        var names = new string?[2];
        var order = new int[2];
        var next = 0;
        using var done = new ManualResetEventSlim(false);
        worker.Post(() =>
        {
            names[0] = Thread.CurrentThread.Name;
            order[0] = ++next;
        });
        worker.Post(() =>
        {
            names[1] = Thread.CurrentThread.Name;
            order[1] = ++next;
            done.Set();
        });

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        worker.Stop(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "Lattice validation", "Lattice validation" }, names);
        Assert.Equal(new[] { 1, 2 }, order);
    }

    [Fact]
    public void Ending_the_world_stops_the_worker_and_stops_following_changes()
    {
        var bench = new ValidationBench().Start();
        bench.Ghost("StructureFrameIron", Large(0, 0, 0));
        bench.Pump();

        bench.Service.End();
        bench.Ghost("StructureFrameIron", Large(4, 0, 0));
        bench.Events.Flush();

        Assert.True(bench.Worker.Stopped);
        Assert.False(bench.Service.Busy);
    }
}
