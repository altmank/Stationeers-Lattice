using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Runs;

/// <summary>
/// The run plans the run and route expanders worked out, kept per expanded command (by instance, so equal steps of a
/// batch keep their own) at the board revision they were laid at, so a preview of the command reads the run's cells,
/// pieces and first failure without laying it again. Dropped when the board moves. Main thread. Cost: a lookup.
/// </summary>
internal sealed class RunReportBook(GhostBoard board)
{
    private readonly Dictionary<Command, RunPlan> _plans = new(Instances.Instance);
    private Revision _at;

    /// <summary>Keeps the plan an expander laid, under the command it expanded to.</summary>
    public void Keep(Command expanded, RunPlan plan)
    {
        Moved();
        _plans[expanded] = plan;
    }

    /// <summary>The plan that expanded to the command at the board's revision; none when no expander laid it there.</summary>
    public Option<RunPlan> Find(Command expanded)
    {
        Moved();
        return _plans.TryGetValue(expanded, out var plan) ? Option.Some(plan) : Option<RunPlan>.None;
    }

    private sealed class Instances : IEqualityComparer<Command>
    {
        public static Instances Instance { get; } = new();

        public bool Equals(Command? x, Command? y) => ReferenceEquals(x, y);

        public int GetHashCode(Command command) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(command);
    }

    private void Moved()
    {
        if (board.Revision == _at)
            return;
        _plans.Clear();
        _at = board.Revision;
    }
}
