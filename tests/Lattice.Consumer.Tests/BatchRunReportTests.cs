using System.Linq;
using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A planner that sends its run with a paint step as one batch reads the run's report from the batch's preview: the
/// first report for its counts and mark, and one report per run step when a batch lays several.
/// </summary>
public sealed class BatchRunReportTests
{
    private static RunReport Report(int cells) =>
        new(ValueList.From(Enumerable.Range(0, cells).Select(static x => new Cell(5 * x, 0, 0)).ToArray()),
            ValueList<Placement>.Empty, Option<CellFailure>.None, Make.EmptyBill(), Make.EmptyBill());

    /// <summary>The cells a planner counts: every run step's.</summary>
    private static int CellsOf(Preview preview)
    {
        var cells = 0;
        foreach (var report in preview.Runs)
            cells += report.Cells.Count;
        return cells;
    }

    [Fact]
    public void A_planner_reads_the_first_report_and_counts_every_run_steps_cells()
    {
        var (trunk, branch) = (Report(7), Report(4));
        var preview = new Preview(
            Make.Changes(default, Make.Player(), ValueList<Ghost>.Empty), ValueList<Finding>.Empty, Make.EmptyBill(), Make.NoEffects(), Option.Some(trunk))
        {
            Runs = ValueList.From(new[] { trunk, branch }),
        };

        Assert.Equal(Option.Some(trunk), preview.Run);
        Assert.Equal(11, CellsOf(preview));
    }

    [Fact]
    public void A_preview_built_without_runs_has_none() =>
        Assert.Empty(new Preview(
            Make.Changes(default, Make.Player(), ValueList<Ghost>.Empty), ValueList<Finding>.Empty, Make.EmptyBill(), Make.NoEffects(), Option<RunReport>.None).Runs);
}
