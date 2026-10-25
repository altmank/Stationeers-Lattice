using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Validation;

/// <summary>
/// The report for some ghosts out of one run's result, worked out a few ghosts at a time for the main thread's slices:
/// each ghost's findings in build order, a wait naming only the ghosts the viewer may
/// see. A unit is one ghost placed or one ghost's findings copied; the sort of the placed ghosts is one unit.
/// Main thread; the result does not change under it, the view is read as each unit runs.
/// </summary>
internal sealed class FindingsRun(ValidationResult result, ValueList<Ghost> ghosts, IGhostBoard view, Revision at)
{
    private readonly List<(int Start, int Count, int Order)> _places = new(ghosts.Count);
    private List<Finding>? _findings;
    private int _next;

    /// <summary>Whether the report is worked out.</summary>
    public bool Done => _findings is not null && _next == _places.Count;

    /// <summary>Works up to <paramref name="units"/> units; true once the report is worked out.</summary>
    public bool Step(int units)
    {
        for (; units > 0 && !Done; units--)
        {
            if (_findings is null)
            {
                Place();
                continue;
            }

            var (start, count, _) = _places[_next++];
            for (var i = start; i < start + count; i++)
                _findings.Add(Visible(result.Findings[i], view));
        }

        return Done;
    }

    /// <summary>The report; complete once <see cref="Done"/>.</summary>
    public ValidationReport Report() => new(at, ValueList.From(_findings ?? []));

    private void Place()
    {
        if (_next < ghosts.Count)
        {
            if (result.TryPlace(ghosts[_next++].Id, out var place))
                _places.Add(place);
            return;
        }

        _places.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        _findings = new List<Finding>(_places.Count);
        _next = 0;
    }

    private static Finding Visible(Finding finding, IGhostBoard view)
    {
        if (finding is not Finding.WaitsFor waits)
            return finding;
        var seen = new List<GhostId>(waits.Others.Count);
        foreach (var other in waits.Others)
            if (view.Find(other).IsSome)
                seen.Add(other);
        return seen.Count == waits.Others.Count ? finding : new Finding.WaitsFor(waits.Ghost, ValueList.From(seen));
    }
}
