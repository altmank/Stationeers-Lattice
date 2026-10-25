using Lattice.Api;

namespace Lattice.Core.Board;

/// <summary>
/// The world's installed access policy, or allow-everything. Its answers are asked plainly: what a policy throws leaves
/// the call that asked. <see cref="Epoch"/> changes whenever the answers may have changed (install, removal,
/// <see cref="IPolicyHandle.Changed"/>), so read caches built on earlier answers are dropped.
/// </summary>
internal sealed class AccessPolicies
{
    private Option<Installed> _installed;

    public int Epoch { get; private set; }

    public bool AllowsEverything => _installed.IsNone;

    /// <summary>Installs a policy; refused while another is installed.</summary>
    public Result<IPolicyHandle, Reason> Install(AccessPolicy policy)
    {
        if (_installed.IsSome)
            return Result<IPolicyHandle, Reason>.Error(Reason.Of(ReasonCodes.AlreadyRegistered, LatticeIdentity.Board));
        var installed = new Installed(this, policy);
        _installed = Option.Some(installed);
        Epoch++;
        return Result<IPolicyHandle, Reason>.Ok(installed);
    }

    public bool MaySee(Actor viewer, Ghost ghost) =>
        !_installed.TryGet(out var installed) || installed.Policy.MaySeeGhost(viewer, ghost);

    public bool MaySee(Actor viewer, Plan plan) =>
        !_installed.TryGet(out var installed) || installed.Policy.MaySeePlan(viewer, plan);

    public Result<Unit, Reason> MayChange(AccessRequest request) =>
        _installed.TryGet(out var installed) ? installed.Policy.MayChange(request) : Result<Unit, Reason>.Ok(Unit.Value);

    private void Remove(Installed installed)
    {
        if (_installed.TryGet(out var current) && ReferenceEquals(current, installed))
        {
            _installed = Option<Installed>.None;
            Epoch++;
        }
    }

    private void Touch(Installed installed)
    {
        if (_installed.TryGet(out var current) && ReferenceEquals(current, installed))
            Epoch++;
    }

    private sealed class Installed(AccessPolicies owner, AccessPolicy policy) : IPolicyHandle
    {
        public AccessPolicy Policy { get; } = policy;

        public void Changed() => owner.Touch(this);

        public void Dispose() => owner.Remove(this);
    }
}
