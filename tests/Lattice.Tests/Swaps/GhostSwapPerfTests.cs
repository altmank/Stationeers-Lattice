using System;
using System.Diagnostics;
using Lattice.Api;
using Lattice.TestKit;
using Lattice.Tests.Board;
using Xunit.Abstractions;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Swaps;

/// <summary>
/// A whole-board swap is one edit like any other: the same cost class as a whole-board label, and per ghost no more
/// than <see cref="ExtraBytesPerGhost"/> above it (the new shape and intent).
/// </summary>
[Collection(nameof(Timing))]
public sealed class GhostSwapPerfTests(ITestOutputHelper output)
{
    private const int Side = 100;

    private const long ExtraBytesPerGhost = 1024;

    private static readonly Label Name = TestKitIds.Must(Label.Create("trunk"));

    private static (TimeSpan Took, long Bytes, ChangeSet Changes) Timed(GhostSwapBench bench, Command command)
    {
        Perf.Collect();
        var watch = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var outcome = bench.Boards.Apply(command);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        watch.Stop();
        return (watch.Elapsed, bytes, TestBoard.Applied(outcome));
    }

    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void A_whole_board_swap_of_ten_thousand_ghosts_costs_what_a_whole_board_label_does_plus_its_new_shapes()
    {
        var (swapBest, labelBest) = (TimeSpan.MaxValue, TimeSpan.MaxValue);
        var (swapBytes, labelBytes) = (0L, 0L);
        for (var run = 0; run < 3; run++)
        {
            var bench = new GhostSwapBench();
            var drafts = new GhostDraft[Side * Side];
            for (var x = 0; x < Side; x++)
                for (var z = 0; z < Side; z++)
                    drafts[x * Side + z] = bench.Boards.Draft("StructureCableStraight", At(x, 0, z));
            bench.Boards.Placed(drafts);

            var label = Timed(bench, new Command.Change(new Selector.Everything(), new GhostEdit.SetLabel(Option.Some(Name)), Option<Revision>.None));
            var swap = Timed(bench, bench.Swap(new Selector.Everything(), bench.Heavy));

            Assert.Equal(Side * Side, label.Changes.Changed.Count);
            Assert.Equal(Side * Side, swap.Changes.Changed.Count);
            (labelBest, labelBytes) = label.Took < labelBest ? (label.Took, label.Bytes) : (labelBest, labelBytes);
            (swapBest, swapBytes) = swap.Took < swapBest ? (swap.Took, swap.Bytes) : (swapBest, swapBytes);
        }

        var extra = (swapBytes - labelBytes) / (Side * Side);
        output.WriteLine($"{Side * Side} ghosts: swap {swapBest.TotalMilliseconds:F2} ms, {swapBytes:N0} B; " +
                         $"label {labelBest.TotalMilliseconds:F2} ms, {labelBytes:N0} B; {extra:N0} B a ghost above the label");
        Assert.True(extra <= ExtraBytesPerGhost, $"{extra:N0} B a ghost above the label");
        Assert.True(swapBest < labelBest * 4, $"swap {swapBest.TotalMilliseconds:F2} ms against label {labelBest.TotalMilliseconds:F2} ms");
    }
}
