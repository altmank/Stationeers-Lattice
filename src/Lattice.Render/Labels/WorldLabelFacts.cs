using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Render.Labels;

/// <summary>
/// Label facts from a running world: a plan's name, a ghost's place in its plan's validation report (build order), and
/// how many of the plan's ghosts are of the ghost's piece. Worked out on first ask and kept until the board changes;
/// a repeated ask on an unchanged board allocates nothing. While a plan's report is pending, the last one answers.
/// Main thread.
/// </summary>
internal sealed class WorldLabelFacts(IPlans plans, IValidation validation, IGhostBoard board) : ILabelFacts
{
    private readonly Dictionary<PlanId, Steps> _steps = new(8);
    private readonly Dictionary<(PlanId Plan, PrefabKey Piece), int> _counts = new(32);
    private Revision _countsAt = new(-1);

    public bool PlanTitle(PlanId plan, out string title)
    {
        if (plans.Find(plan).TryGet(out var found))
        {
            title = found.Name.Value;
            return title.Length > 0;
        }

        title = "";
        return false;
    }

    public bool Step(GhostId ghost, out int step)
    {
        step = 0;
        if (board.Find(ghost).TryGet(out var record))
            return StepsOf(record.Plan).At.TryGetValue(ghost, out step);
        return false;
    }

    public bool BillCount(PlanId plan, PrefabKey piece, out int count)
    {
        var now = board.Revision;
        if (now != _countsAt)
        {
            _counts.Clear();
            _countsAt = now;
        }

        if (!_counts.TryGetValue((plan, piece), out count))
        {
            count = board.Select(new Selector.All(ValueList.From(new Selector[] { new Selector.InPlan(plan), new Selector.OfPiece(piece) })))
                .TryGet(out var ghosts) ? ghosts.Count : 0;
            _counts[(plan, piece)] = count;
        }

        return count > 0;
    }

    private Steps StepsOf(PlanId plan)
    {
        if (!_steps.TryGetValue(plan, out var steps))
            _steps[plan] = steps = new Steps(new Selector.InPlan(plan));
        var now = board.Revision;
        if (steps.Asked == now)
            return steps;
        if (validation.Check(steps.Which).TryGet(out var answer) && answer is Answer<ValidationReport>.Ready ready)
        {
            steps.Asked = now;
            if (ready.Value.At != steps.Report)
            {
                steps.Report = ready.Value.At;
                steps.At.Clear();
                var findings = ready.Value.Findings;
                for (var i = 0; i < findings.Count; i++)
                    steps.At[findings[i].Ghost] = i + 1;
            }
        }

        return steps;
    }

    private sealed class Steps(Selector which)
    {
        public Selector Which { get; } = which;

        public Revision Asked = new(-1);
        public Revision Report = new(-1);
        public readonly Dictionary<GhostId, int> At = new(64);
    }
}
