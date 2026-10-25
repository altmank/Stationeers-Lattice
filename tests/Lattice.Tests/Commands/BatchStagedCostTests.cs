using System;
using System.Linq;
using Lattice.Api;
using static Lattice.Tests.Board.TestBoard;
using static Lattice.Tests.Runs.RunBench;

namespace Lattice.Tests.Commands;

/// <summary>
/// What staging a batch's earlier steps costs, in bytes allocated, not time: the same trunk-and-branches batch on a
/// board of a few ghosts and on one of many, the many far from the batch. The staging prepares and stages only the
/// earlier steps, so the bytes do not grow with the board.
/// </summary>
public sealed class BatchStagedCostTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Few = 50;
    private const int Many = 5000;

    // A board of `others` cable ghosts in a far row, the batch laid once elsewhere to warm up; the bytes of laying it again.
    private static long Bytes(int others, out long oneByOne)
    {
        var b = new BatchStagedTests.Bench();
        b.T.Placed(Enumerable.Range(0, others).Select(i => b.T.Draft("StructureFrame", new Cell(10 + 20 * (i % 100), 1010, 10 + 20 * (i / 100)))).ToArray());
        Applied(b.Submit(Batch(b, At(0, 0, 40))));
        foreach (var step in Batch(b, At(0, 0, 80)).Steps)
            Applied(b.Submit(step));

        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var step in Batch(b, At(0, 0, 120)).Steps)
            Applied(b.Submit(step));
        oneByOne = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        Applied(b.Submit(Batch(b, At(0, 0, 160))));
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static Command.Batch Batch(BatchStagedTests.Bench b, Cell origin)
    {
        Cell At(int x, int z) => new(origin.X + 5 * x, origin.Y, origin.Z + 5 * z);
        return new Command.Batch(ValueList.From<Command>([b.Run(At(0, 0), At(6, 0)), b.Run(At(2, 0), At(2, 3)), b.Run(At(4, 0), At(4, -3))]));
    }

    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void Staging_a_batchs_earlier_steps_allocates_the_same_on_a_small_board_and_a_large_one()
    {
        var few = Bytes(Few, out var fewOneByOne);
        var many = Bytes(Many, out var manyOneByOne);

        output.WriteLine($"trunk and two branches as one batch: {few:N0} B on {Few} ghosts, {many:N0} B on {Many}; as three commands: {fewOneByOne:N0} B, {manyOneByOne:N0} B");
        Assert.True(many - few < Many - Few, $"{many:N0} B on {Many} ghosts against {few:N0} B on {Few}");
    }
}
