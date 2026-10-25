using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Plans;

/// <summary>
/// The six facts of one plan from what the modules answered for it, and the state that follows. Pure: no board, no
/// world. Linear in the plan's ghosts and findings.
/// </summary>
internal static class PlanFactsMaker
{
    /// <summary>
    /// The facts. A failing finding (<see cref="Finding.WillFail"/>) counts against <see cref="PlanFacts.ValidInWorld"/>
    /// when the world is what fails it: the ghost is stale, or the game's own check refuses it against what is built
    /// (<see cref="ReasonCodes.GameRefused"/>); every other failure (support, a cycle, a rule, an unknown kind) counts
    /// against <see cref="PlanFacts.ValidAlone"/>. Buildable next: planned or started ghosts whose finding passes with
    /// nothing to wait for, in id order. Progress: <paramref name="done"/>, the started ghosts, and the rest (planned or
    /// stale) as remaining.
    /// </summary>
    public static PlanFacts Make(
        PlanId plan, Revision at, ValueList<Ghost> ghosts, int done, ValidationReport report, Bill cost, ValueList<SafetyWarning> consequences)
    {
        var run = new PlanFactsRun(ghosts, report);
        run.Step(int.MaxValue);
        return run.Facts(plan, at, done, cost, consequences);
    }

    /// <summary>
    /// The state the facts give: no ghosts on the board and none done is empty, none left and some done is complete;
    /// otherwise any failing ghost is has errors, any done or started is in progress, else valid.
    /// </summary>
    public static PlanState StateOf(PlanFacts facts)
    {
        var left = facts.Progress.Started + facts.Progress.Remaining;
        if (left == 0)
            return facts.Progress.Done == 0 ? new PlanState.Empty() : new PlanState.Complete();
        if (facts.ValidAlone is Validity.Invalid || facts.ValidInWorld is Validity.Invalid)
            return new PlanState.HasErrors();
        return facts.Progress.Done > 0 || facts.Progress.Started > 0 ? new PlanState.InProgress() : new PlanState.Valid();
    }
}

/// <summary>
/// <see cref="PlanFactsMaker.Make"/> a few ghosts at a time, for the main thread's slices: the ghosts once for their
/// states, the findings once, the ghosts again for the buildable ones in id order. A unit is one ghost or one finding.
/// </summary>
internal sealed class PlanFactsRun(ValueList<Ghost> ghosts, ValidationReport report)
{
    private const int Passes = 3;

    private readonly HashSet<GhostId> _stale = [];
    private readonly HashSet<GhostId> _buildable = [];
    private readonly List<Finding> _alone = [];
    private readonly List<Finding> _inWorld = [];
    private readonly List<GhostId> _sorted = [];
    private int _started;
    private int _pass;
    private int _next;

    /// <summary>Whether every pass is done.</summary>
    public bool Done => _pass == Passes;

    /// <summary>Works up to <paramref name="units"/> units; true once every pass is done.</summary>
    public bool Step(int units)
    {
        while (units > 0 && !Done)
        {
            if (_next == (_pass == 1 ? report.Findings.Count : ghosts.Count))
            {
                _pass++;
                _next = 0;
                continue;
            }

            switch (_pass)
            {
                case 0:
                    Count(ghosts[_next]);
                    break;
                case 1:
                    Sort(report.Findings[_next]);
                    break;
                default:
                    if (_buildable.Contains(ghosts[_next].Id))
                        _sorted.Add(ghosts[_next].Id);
                    break;
            }

            _next++;
            units--;
        }

        return Done;
    }

    /// <summary>The facts; whole once <see cref="Done"/>.</summary>
    public PlanFacts Facts(PlanId plan, Revision at, int done, Bill cost, ValueList<SafetyWarning> consequences) => new(
        plan, at, Validity(_alone), Validity(_inWorld), ValueList.From(_sorted),
        new Progress(done, _started, ghosts.Count - _started), consequences, cost);

    private void Count(Ghost ghost)
    {
        switch (ghost.State)
        {
            case GhostState.Started:
                _started++;
                break;
            case GhostState.Stale:
                _stale.Add(ghost.Id);
                break;
        }
    }

    private void Sort(Finding finding)
    {
        switch (finding)
        {
            case Finding.WillFail failing when _stale.Contains(failing.Ghost) || failing.Why.Code == ReasonCodes.GameRefused:
                _inWorld.Add(failing);
                break;
            case Finding.WillFail failing:
                _alone.Add(failing);
                break;
            case Finding.Passes passes when !_stale.Contains(passes.Ghost):
                _buildable.Add(passes.Ghost);
                break;
        }
    }

    private static Validity Validity(List<Finding> failing) =>
        failing.Count == 0 ? new Validity.Valid() : new Validity.Invalid(ValueList.From(failing));
}
