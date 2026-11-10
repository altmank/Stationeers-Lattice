using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Saving;
using Lattice.TestKit.Fixtures;
using Lattice.TestKit.World;
using Lattice.Tests.EntryPoint;
using Xunit.Abstractions;

namespace Lattice.Tests.Validation.RealBase;

/// <summary>
/// The bytes a 5,000-ghost plan allocates over a large generated base (lattice-gen-big), in the flow of <see cref="RealBaseTests"/>:
/// the base's pieces planned over an empty world, saved, loaded again, and 5,000 more planned over the standing base.
/// Each phase's bytes are split between the main thread (the test thread, which runs Lattice's frames) and every other
/// thread (Lattice's worker and rule threads). The main thread's worst phase is the first plan (board, styles, build
/// safety), the worker's the plan over the standing base (build order stages); each is held under its bound per ghost,
/// so garbage the collector must stop the game for cannot creep back. Allocation counts bytes, not time, so the bounds
/// hold on a loaded machine; the worker's varies with how many runs the board's changes batch into, hence the room.
/// The lines go to <c>tests/fixtures/generated/realbase-alloc.txt</c>.
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class AllocationTests(ITestOutputHelper output)
{
    private const int PlanSize = 5000;
    private const double MainPerGhost = 13_500;
    private const double WorkerPerGhost = 9_500;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);

    [RealBaseFact(RealBaseBench.BigBase)]
    [Trait(Perf.Trait, Perf.Value)]
    public void A_five_thousand_ghost_plan_on_a_real_base_allocates_within_its_bytes_per_ghost_on_each_thread()
    {
        using var bench = RealBaseBench.Load(RealBaseBench.BigBase);
        var built = bench.Built();
        bench.Start(Option<SaveKey>.None, new FakeWorld(bench.Catalogue));
        Plan(bench, "Warm", built.Skip(3 * PlanSize).Take(500));

        bench.Start(Option<SaveKey>.None, new FakeWorld(bench.Catalogue));
        RealBaseBench.RequirePlanHolds(bench.Catalogue, built.Take(PlanSize));
        RealBaseBench.RequirePlanHolds(bench.Catalogue, built.Skip(PlanSize).Take(PlanSize));
        var first = Measure(() => Plan(bench, "The base", built.Take(PlanSize)));
        var key = bench.GameSave();
        bench.Start(Option.Some(key));
        bench.World.NetworkOf(built[0].Ref.Id);
        Assert.True(bench.Settle(Limit).Settled);
        var over = Measure(() => Plan(bench, "Over", built.Skip(PlanSize).Take(PlanSize)));

        var lines = new List<string> { $"base pieces over an empty world: {first}", $"over the standing base: {over}" };
        foreach (var line in lines)
            output.WriteLine(line);
        File.WriteAllLines(Path.Combine(Fixtures.Generated, "realbase-alloc.txt"), lines);

        Assert.True(first.MainPerGhost <= MainPerGhost, lines[0]);
        Assert.True(over.OtherPerGhost <= WorkerPerGhost, lines[1]);
    }

    private static void Plan(RealBaseBench bench, string name, IEnumerable<StructureInfo> structures)
    {
        bench.World.NetworkOf(new ReferenceId(1));
        var drafts = ValueList.From(structures.Select(s => RealBaseBench.Draft(new Placement(s.Ref.Piece, s.Ref.At, s.Turn))).ToList());
        Assert.IsType<Outcome.Applied>(bench.Submit(new Command.CreatePlan(RealBaseBench.Plan(name)) { Ghosts = drafts }));
        Assert.True(bench.Settle(Limit).Settled, $"{name}: answers not ready in {Limit.TotalSeconds} s");
    }

    private static Bytes Measure(Action phase)
    {
        var before = Collector.Now();
        var all = GC.GetTotalAllocatedBytes(precise: true);
        var main = GC.GetAllocatedBytesForCurrentThread();
        phase();
        var mainBytes = GC.GetAllocatedBytesForCurrentThread() - main;
        var allBytes = GC.GetTotalAllocatedBytes(precise: true) - all;
        return new Bytes(mainBytes, allBytes - mainBytes, Collector.Now() - before);
    }

    private readonly record struct Bytes(long Main, long Other, Collector Collector)
    {
        public double MainPerGhost => Main / (double)PlanSize;

        public double OtherPerGhost => Other / (double)PlanSize;

        public override string ToString() =>
            $"main thread {Main / 1048576.0:F1} MB ({MainPerGhost:F0} B a ghost, bound {AllocationTests.MainPerGhost:F0}), " +
            $"other threads {Other / 1048576.0:F1} MB ({OtherPerGhost:F0} B a ghost, bound {WorkerPerGhost:F0}); {Collector}";
    }
}
