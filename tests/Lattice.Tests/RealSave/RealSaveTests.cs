using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Saving;
using Lattice.TestKit.Fixtures;
using Lattice.TestKit.World;
using Lattice.Tests.EntryPoint;
using Xunit.Abstractions;

namespace Lattice.Tests.RealSave;

/// <summary>
/// Lattice on a large real base, read offline (the modded Vulcan2_4 save does not load on a vanilla server):
/// the save loaded into a fake world, Lattice started over it with the game's runtime, a 5,000-ghost plan on built
/// structures matched at a reload, a 5,000-ghost new plan checked, and the frame costs of the steady state. Every
/// number is wall time on .NET 8, written to <c>generated/real-save-offline.txt</c> for the report; the
/// assertions hold the flows, the numbers are findings.
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class RealSaveTests(ITestOutputHelper output)
{
    private const string Save = "Vulcan2_4.save";
    private const int PlanSize = 5000;

    [RealSaveFact(Save)]
    public void A_base_LUs_size_loads_matches_checks_and_holds_its_frames()
    {
        using var b = RealSaveBench.Load(Save);
        try
        {
            Run(b);
        }
        finally
        {
            b.Lines.Add(b.Diagnose());
            foreach (var line in b.Log)
                b.Lines.Add("lattice log: " + line);
            Write(b.Lines);
        }
    }

    private static void Run(RealSaveBench b)
    {
        var built = b.Built();
        var onBuilt = built.Take(PlanSize).Select(s => RealSaveBench.Draft(new Placement(s.Ref.Piece, s.Ref.At, s.Turn))).ToList();

        // The base planned before it was built: the plan is placed in an empty world and saved; the save's world then
        // loads with it, and the load's matching pass judges every ghost.
        b.Lines.Add($"lattice start, empty world: {b.Start(Option<SaveKey>.None, new FakeWorld(b.Catalogue)).TotalMilliseconds:F0} ms");
        var watch = Stopwatch.StartNew();
        var planned = b.Submit(new Command.CreatePlan(RealSaveBench.Plan("The base")) { Ghosts = ValueList.From(onBuilt) });
        watch.Stop();
        b.Lines.Add($"plan of {onBuilt.Count} ghosts at the save's built placements ({built.Count} built in the save), empty world: {Kind(planned)} in {watch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.IsType<Outcome.Applied>(planned);
        b.Lines.Add("validation of it to idle: " + b.Settle(TimeSpan.FromSeconds(300)));
        var onBoard = b.Lattice.Ghosts.Index.Ghosts.Count;
        b.Lines.Add($"ghosts on the board before the save: {onBoard} (a ghost in a spot that holds one replaces it)");
        var key = b.GameSave();

        var restart = b.Start(Option.Some(key));
        b.Lines.Add($"load of the save's world with the plan (catalogue, mirror scan of {b.Save.Structures} structures, saving open, matching pass): {restart.TotalMilliseconds:F0} ms");
        b.Lines.Add($"match: {b.Lattice.Match.Map(static m => m.ToString()).Reduce("none")}");
        Assert.True(b.Lattice.Match.TryGet(out var match) && match.Judged == onBoard);
        b.Lines.Add("after the load: " + b.Settle(TimeSpan.FromSeconds(300)));
        b.Lines.Add(b.Diagnose());

        // A plan placed over the standing base: its ghosts match the structures already there.
        var over = built.Skip(PlanSize).Take(PlanSize).Select(s => RealSaveBench.Draft(new Placement(s.Ref.Piece, s.Ref.At, s.Turn))).ToList();
        watch.Restart();
        var overBase = b.Submit(new Command.CreatePlan(RealSaveBench.Plan("Over the base")) { Ghosts = ValueList.From(over) });
        watch.Stop();
        b.Lines.Add($"plan of {over.Count} ghosts over standing structures: {Kind(overBase)} in {watch.Elapsed.TotalMilliseconds:F0} ms");
        b.Lines.Add("after it: " + b.Settle(TimeSpan.FromSeconds(300)));
        b.Lines.Add(b.Diagnose());

        var fresh = FreshCells(built, PlanSize).Select(c => RealSaveBench.Draft(new Placement(b.Catalogue.Named("StructureFrame").Key, c, default))).ToList();
        watch.Restart();
        var checkedPlan = b.Submit(new Command.CreatePlan(RealSaveBench.Plan("New wing")) { Ghosts = ValueList.From(fresh) });
        watch.Stop();
        b.Lines.Add($"new plan of {fresh.Count} ghosts beside the base: {Kind(checkedPlan)} in {watch.Elapsed.TotalMilliseconds:F0} ms");
        Assert.IsType<Outcome.Applied>(checkedPlan);
        var checking = b.Settle(TimeSpan.FromSeconds(300));
        b.Lines.Add("validation of the new plan to idle: " + checking);
        Assert.True(checking.Settled);

        var steady = b.Settle(TimeSpan.FromSeconds(5));
        var idleAllocated = IdleFrameBytes(b);
        b.Lines.Add($"steady state: {steady}; one idle frame allocates {idleAllocated} bytes");
        var budgets = Lattice.Core.Slices.FrameBudgets.Default;
        var total = (budgets.Checking.Length + budgets.Sending.Length + budgets.Saving.Length + budgets.Drawing.Length).TotalMilliseconds;
        b.Lines.Add($"budget: Lattice's slices total {total:F1} ms a frame (16.7 ms at 60 fps); frames over it while checking: {checking.Milliseconds.Count(m => m > total)} of {checking.Milliseconds.Count}");

    }

    private static long IdleFrameBytes(RealSaveBench b)
    {
        for (var i = 0; i < 10; i++)
            b.Lattice.Frame();
        var before = GC.GetAllocatedBytesForCurrentThread();
        b.Lattice.Frame();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // Large cells in a layer 20 m over the base's highest large cell, starting at its lowest corner, one cell apart.
    private static IEnumerable<Cell> FreshCells(List<StructureInfo> built, int count)
    {
        var large = built.Where(s => s.Ref.At.X % 20 != 0 && Math.Abs(s.Ref.At.X % 20) == 10).ToList();
        var minX = large.Min(s => s.Ref.At.X);
        var minZ = large.Min(s => s.Ref.At.Z);
        var y = large.Max(s => s.Ref.At.Y) + 200;
        var side = (int)Math.Ceiling(Math.Sqrt(count));
        for (var i = 0; i < count; i++)
            yield return new Cell(minX + 20 * (i % side), y, minZ + 20 * (i / side));
    }

    private static string Kind(Outcome outcome) => outcome switch
    {
        Outcome.Applied => "applied",
        Outcome.Refused refused => "refused " + refused.Why[0].Why.Code.Value,
        _ => outcome.GetType().Name,
    };

    private void Write(List<string> lines)
    {
        var text = string.Join(Environment.NewLine, lines.Prepend($"real save offline: {Save}, .NET {Environment.Version}, {DateTime.Now:O}"));
        output.WriteLine(text);
        File.WriteAllText(Path.Combine(Fixtures.Generated, "real-save-offline.txt"), text + Environment.NewLine);
    }
}
