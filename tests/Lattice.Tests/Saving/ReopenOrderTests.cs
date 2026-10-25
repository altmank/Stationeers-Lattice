using System;
using System.IO;
using Lattice.Api;
using Lattice.Core.Saving;
using Lattice.Tests.Board;
using Lattice.Tests.FinishedPlans;
using static Lattice.Tests.FinishedPlans.FinishedBench;

namespace Lattice.Tests.Saving;

/// <summary>
/// Reopen writes in an order a stop at any step survives: the file comes back from the archive, the reopening line is
/// written, and only then does the plan leave the finished index, so until the line is on disk the index matches the
/// last save, where the plan is finished.
/// </summary>
public sealed class ReopenOrderTests
{
    [Fact]
    public void The_plan_stays_in_the_finished_index_until_its_reopening_line_is_written()
    {
        using var b = new FinishedBench();
        var plan = b.NewPlan("Hall");
        b.Construct(b.Plan(plan, "ModPanel", Large(0)));
        b.Finish(plan);
        b.Reload(Option.Some(b.Save()));
        var finishedText = b.PlanText(plan);

        TestBoard.Applied(b.Board.Apply(new CommandId(Guid.NewGuid()), new Command.Reopen(plan), TestBoard.Alice));
        b.T.Events.Flush();
        b.Saving.Step();

        Assert.True(File.Exists(b.Folder.PlanPath(plan)));
        Assert.Equal(finishedText, File.ReadAllText(b.Folder.PlanPath(plan)));
        Assert.Contains(b.Folder.Finished(), f => f.Plan.Id == plan);

        b.Saving.Step();

        Assert.NotEqual(finishedText, File.ReadAllText(b.Folder.PlanPath(plan)));
        Assert.Contains(b.Folder.Finished(), f => f.Plan.Id == plan);

        b.Saving.Step();

        Assert.DoesNotContain(b.Folder.Finished(), f => f.Plan.Id == plan);
        Assert.DoesNotContain(b.Saving.Finished, f => f.Id == plan);
    }
}
