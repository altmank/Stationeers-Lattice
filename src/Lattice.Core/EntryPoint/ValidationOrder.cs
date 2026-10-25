using Lattice.Api;
using Lattice.Core.Steps;
using Lattice.Core.Validation;

namespace Lattice.Core.EntryPoint;

/// <summary>
/// The build order as validation's newest complete run has it: the ghosts a ghost waits for, and whether it joins
/// pipe networks. Main thread; constant cost per ghost, no allocation.
/// </summary>
internal sealed class ValidationOrder(ValidationService validation) : IOrderFacts
{
    public ValueList<GhostId> WaitsFor(GhostId ghost)
    {
        if (validation.Latest is not { } result || !result.TryPlace(ghost, out var place))
            return ValueList<GhostId>.Empty;
        for (var i = place.Start; i < place.Start + place.Count; i++)
            if (result.Findings[i] is Finding.WaitsFor waits)
                return waits.Others;
        return ValueList<GhostId>.Empty;
    }

    public bool JoinsPipes(GhostId ghost) => validation.Latest is { } result && result.PipeJoins.Contains(ghost);
}
