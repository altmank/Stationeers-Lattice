using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.TestKit.Fixtures;
using Lattice.TestKit.World;
using Lattice.Tests.EntryPoint;
using Xunit.Abstractions;

namespace Lattice.Tests.Validation.RealBase;

/// <summary>
/// Validation and plan state over a large real base (Vulcan2_4, 36,306 structures) read offline, in the flow of the live big-base run: the
/// base's own pieces planned as 5,000 ghosts over an empty world, the world loaded again from the save, 5,000 ghosts
/// planned over the standing base, and a new wing of 5,000 frames. Every plan's answers become ready, and the checking
/// work the scheduler runs in a frame stays within the checking budget plus one step's allowance (as the 10,000-ghost
/// scale test allows) at the 95th percentile, every frame counted with the collector pauses in it. The 99th percentile
/// and the worst frames, with their pauses, are reported, not asserted: on a heap holding a 36,000-structure base a full
/// collection takes tens of milliseconds, set mostly by what the base keeps alive, and a run of a few dozen frames on a
/// loaded machine has a frame or two where the thread waits a scheduler quantum (about 15 ms) on next to no work.
/// A 500-ghost plan in a world of its own runs first, so the timing starts with the code compiled (the game's runtime
/// compiles a method once, on its first call; a cold test host compiles and recompiles hot methods on the main thread
/// for the first few thousand ghosts). The whole-frame numbers (with the
/// board's event bursts) are reported, not asserted. The lines go to <c>tests/fixtures/generated/realbase-perf.txt</c>.
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class RealBaseTests(ITestOutputHelper output)
{
    private const int PlanSize = 5000;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);
    private static readonly double Allowance = (FrameBudgets.Default.Checking.Length + TimeSpan.FromMilliseconds(2)).TotalMilliseconds;

    [RealBaseFact("Vulcan2_4.save")]
    [Trait(Perf.Trait, Perf.Value)]
    public void Five_thousand_ghost_plans_on_a_real_base_get_their_answers_with_checking_inside_the_budget()
    {
        using var bench = RealBaseBench.Load("Vulcan2_4.save");
        var lines = new List<string>(bench.Lines);
        var phases = new List<(string Name, BaseFrameTimes Times)>();
        try
        {
            var built = bench.Built();
            bench.Start(Option<SaveKey>.None, new FakeWorld(bench.Catalogue));
            lines.Add($"warm-up, 500 ghosts in a world of its own: {Phase(bench, new Command.CreatePlan(RealBaseBench.Plan("Warm")) { Ghosts = Drafts(built.Skip(3 * PlanSize).Take(500)) })}");

            var onBuilt = Drafts(built.Take(PlanSize));
            bench.Start(Option<SaveKey>.None, new FakeWorld(bench.Catalogue));
            phases.Add(("base pieces over an empty world", Phase(bench, new Command.CreatePlan(RealBaseBench.Plan("The base")) { Ghosts = onBuilt })));

            var key = bench.GameSave();
            bench.Start(Option.Some(key));
            Warm(bench, built);
            phases.Add(("load from the save", bench.Settle(Limit)));

            var over = Drafts(built.Skip(PlanSize).Take(PlanSize));
            phases.Add(("over the standing base", Phase(bench, new Command.CreatePlan(RealBaseBench.Plan("Over")) { Ghosts = over })));

            phases.Add(("a new wing of frames", Phase(bench, new Command.CreatePlan(RealBaseBench.Plan("Wing")) { Ghosts = Wing(bench, built) })));
        }
        finally
        {
            foreach (var (name, times) in phases)
                lines.Add($"{name}: {times}");
            lines.AddRange(bench.Lines.Skip(1));
            lines.Add(bench.Diagnose());
            foreach (var line in lines)
                output.WriteLine(line);
            File.WriteAllLines(Path.Combine(Fixtures.Generated, "realbase-perf.txt"), lines);
        }

        Assert.All(phases, phase => Assert.True(phase.Times.Settled, $"{phase.Name}: answers not ready in {Limit.TotalSeconds} s"));
        foreach (var (name, times) in phases)
            Assert.True(times.P95Checking <= Allowance, $"{name}: {times}");
    }

    private static BaseFrameTimes Phase(RealBaseBench bench, Command command)
    {
        bench.World.NetworkOf(new ReferenceId(1));
        Assert.IsType<Outcome.Applied>(bench.Submit(command));
        return bench.Settle(Limit);
    }

    /// <summary>The fake world builds its networks on the first ask, where the game reads a field: asked once before the timing.</summary>
    private static void Warm(RealBaseBench bench, List<StructureInfo> built) => bench.World.NetworkOf(built[0].Ref.Id);

    private static ValueList<GhostDraft> Drafts(IEnumerable<StructureInfo> structures) =>
        ValueList.From(structures.Select(s => RealBaseBench.Draft(new Placement(s.Ref.Piece, s.Ref.At, s.Turn))).ToList());

    /// <summary>Frames on a grid two large cells apart, 20 m above the base's highest large piece.</summary>
    private static ValueList<GhostDraft> Wing(RealBaseBench bench, List<StructureInfo> built)
    {
        var large = built.Where(s => Math.Abs(s.Ref.At.X % 20) == 10).ToList();
        var (minX, minZ, y) = (large.Min(s => s.Ref.At.X), large.Min(s => s.Ref.At.Z), large.Max(s => s.Ref.At.Y) + 200);
        var side = (int)Math.Ceiling(Math.Sqrt(PlanSize));
        var frame = bench.Catalogue.Named("StructureFrame").Key;
        return ValueList.From(Enumerable.Range(0, PlanSize)
            .Select(i => RealBaseBench.Draft(new Placement(frame, new Cell(minX + 20 * (i % side), y, minZ + 20 * (i / side)), default)))
            .ToList());
    }
}
