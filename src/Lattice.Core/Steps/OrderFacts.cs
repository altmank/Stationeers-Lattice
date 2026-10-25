using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Support;

namespace Lattice.Core.Steps;

/// <summary>What the build order says about a ghost's steps: the ghosts it waits for, and whether it joins pipe networks.</summary>
internal interface IOrderFacts
{
    ValueList<GhostId> WaitsFor(GhostId ghost);

    bool JoinsPipes(GhostId ghost);
}

/// <summary>No build order yet: no ghost waits, none joins pipe networks.</summary>
internal sealed class NoOrder : IOrderFacts
{
    public ValueList<GhostId> WaitsFor(GhostId ghost) => ValueList<GhostId>.Empty;

    public bool JoinsPipes(GhostId ghost) => false;
}

/// <summary>
/// One build order's facts: the waits its findings name, and its pipe joins (two joins in one game tick lose gas, so
/// those ghosts' steps carry <see cref="Constraint.OnePipeJoinPerTick"/>). Built once per report, linear
/// in its findings.
/// </summary>
internal sealed class ReportOrder : IOrderFacts
{
    private readonly Dictionary<GhostId, ValueList<GhostId>> _waits;
    private readonly ValueSet<GhostId> _joins;

    public ReportOrder(OrderReport report)
    {
        _waits = new Dictionary<GhostId, ValueList<GhostId>>(report.Findings.Count);
        foreach (var finding in report.Findings)
            if (finding is Finding.WaitsFor waits)
                _waits[waits.Ghost] = waits.Others;
        _joins = report.PipeJoins;
    }

    public ValueList<GhostId> WaitsFor(GhostId ghost) => _waits.TryGetValue(ghost, out var others) ? others : ValueList<GhostId>.Empty;

    public bool JoinsPipes(GhostId ghost) => _joins.Contains(ghost);
}
