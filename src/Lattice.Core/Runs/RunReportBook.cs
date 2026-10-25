using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Runs;

/// <summary>
/// The run plans the run and route expanders worked out, kept per command at the board revision they were laid at, so
/// a preview of the same command reads the run's cells, pieces and first failure without laying it again. Dropped when
/// the board moves. Main thread. Cost: a lookup.
/// </summary>
internal sealed class RunReportBook(GhostBoard board)
{
    private readonly Dictionary<Command, RunPlan> _plans = [];
    private Revision _at;

    /// <summary>Keeps the plan an expander laid for the command.</summary>
    public void Keep(Command command, RunPlan plan)
    {
        Moved();
        _plans[command] = plan;
    }

    /// <summary>The plan laid for the command at the board's revision; none when no expander laid it there.</summary>
    public Option<RunPlan> Find(Command command)
    {
        Moved();
        return _plans.TryGetValue(command, out var plan) ? Option.Some(plan) : Option<RunPlan>.None;
    }

    private void Moved()
    {
        if (board.Revision == _at)
            return;
        _plans.Clear();
        _at = board.Revision;
    }
}
