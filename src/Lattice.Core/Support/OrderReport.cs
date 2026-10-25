using Lattice.Api;

namespace Lattice.Core.Support;

/// <summary>
/// One build order across every plan, and what support says about each ghost in it. <see cref="Stages"/> run in
/// order; the ghosts of one stage may be done in one game tick, with at most one pipe-network join among them (two
/// merges in one tick lose gas). <see cref="PipeJoins"/> are the ghosts
/// whose steps carry <see cref="Constraint.OnePipeJoinPerTick"/>. Findings run in build order; ghosts in a cycle
/// come last.
/// </summary>
/// <param name="Stages">The ghosts, stage by stage.</param>
/// <param name="Findings">Per ghost: passes, waits for, will fail; and warnings.</param>
/// <param name="PipeJoins">The ghosts that join two or more pipe networks.</param>
internal sealed record OrderReport(ValueList<ValueList<GhostId>> Stages, ValueList<Finding> Findings, ValueSet<GhostId> PipeJoins);

/// <summary>The order, or the placements the game's check must answer first.</summary>
internal abstract record OrderOutcome
{
    private OrderOutcome() { }

    /// <summary>The order and findings.</summary>
    /// <param name="Report">The report.</param>
    public sealed record Ready(OrderReport Report) : OrderOutcome;

    /// <summary>The game's check has not answered for these placements yet; ask it on the main thread, then again.</summary>
    /// <param name="Ask">The placements.</param>
    public sealed record Pending(ValueList<Placement> Ask) : OrderOutcome;
}
