using System;
using Lattice.Api;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Validation;

/// <summary>
/// What validation spends noting a spawn-sized burst of world changes (as the bridge flush raises in one frame):
/// counted in bytes allocated and passes over the answer book, not in time.
/// </summary>
public sealed class WorldNotingCostTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Side = 25;
    private const int Changes = Side * Side;

    // Ghosts two large cells apart on a square, so the game is asked about each and every one holds an answer.
    private static ValidationBench Square()
    {
        var bench = new ValidationBench().Start();
        for (var i = 0; i < Side; i++)
            for (var j = 0; j < Side; j++)
                bench.Ghost("StructureFrameIron", Large(2 * i, 0, 2 * j));
        bench.Pump();
        return bench;
    }

    // One structure within a large cell of each ghost (`beside` of a ghost's cell), all before validation's next
    // frame; the bytes the main thread allocated registering them.
    private static long Burst(ValidationBench bench, (int X, int Y, int Z) beside)
    {
        var infos = new StructureInfo[Changes];
        for (var i = 0; i < Side; i++)
            for (var j = 0; j < Side; j++)
                infos[i * Side + j] = bench.Structure("StructureFrameIron", Large(2 * i + beside.X, beside.Y, 2 * j + beside.Z));
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var info in infos)
            bench.World.Register(info);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    [Trait(Perf.Trait, Perf.Value)]
    public void Noting_a_burst_of_world_changes_allocates_nothing_a_change_and_drops_answers_once_a_frame()
    {
        var noted = Square();
        var bare = Square();
        bare.Service.End();
        Burst(noted, (1, 1, 0));
        Burst(bare, (1, 1, 0));
        noted.Pump();

        var withNoting = Burst(noted, (0, 1, 1));
        var without = Burst(bare, (0, 1, 1));
        var passes = noted.Service.DropPasses;
        noted.Frame();
        var line = $"noting {Changes} changes allocated {withNoting - without} bytes ({(withNoting - without) / Changes} a change); drop passes in the frame {noted.Service.DropPasses - passes}";
        output.WriteLine(line);

        Assert.True(withNoting - without <= 1024, line);
        Assert.Equal(passes + 1, noted.Service.DropPasses);
        noted.Pump();
        Assert.Equal(passes + 1, noted.Service.DropPasses);
    }
}
