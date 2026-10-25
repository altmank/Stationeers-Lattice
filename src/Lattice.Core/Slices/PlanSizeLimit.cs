using System;
using Lattice.Api;

namespace Lattice.Core.Slices;

/// <summary>
/// The optional limit on ghosts per plan that a higher layer sets (none by default): Lattice has no limit of its
/// own and refuses past this one with <see cref="ReasonCodes.TooLarge"/>. One limit at a time. Main thread.
/// </summary>
internal sealed class PlanSizeLimit
{
    private Option<Holder> _current;

    /// <summary>Sets the limit until the returned handle is disposed; refused when not positive or one is set.</summary>
    public Result<IDisposable, Reason> Set(int maxGhosts)
    {
        if (maxGhosts <= 0)
            return Result<IDisposable, Reason>.Error(
                Reason.InvalidValue(nameof(PlanSizeLimit), maxGhosts.ToString(), "positive"));
        if (_current.TryGet(out var held))
            return Result<IDisposable, Reason>.Error(Reason.Of(
                ReasonCodes.AlreadyRegistered, SliceRules.SizeLimit, ValueMap.Of("limit", held.Max.ToString())));

        var holder = new Holder(this, maxGhosts);
        _current = Option<Holder>.Some(holder);
        return Result<IDisposable, Reason>.Ok(holder);
    }

    /// <summary>Admits a plan of that many ghosts, or refuses it as too large with the limit and the size. Constant cost.</summary>
    public Result<Unit, Reason> Admit(int ghosts) =>
        _current.TryGet(out var held) && ghosts > held.Max
            ? Result<Unit, Reason>.Error(Reason.Of(
                ReasonCodes.TooLarge,
                SliceRules.SizeLimit,
                ValueMap<string, string>.Empty.With("limit", held.Max.ToString()).With("size", ghosts.ToString())))
            : Result<Unit, Reason>.Ok(Unit.Value);

    private void Release(Holder holder)
    {
        if (_current.TryGet(out var held) && ReferenceEquals(held, holder))
            _current = Option<Holder>.None;
    }

    private sealed class Holder(PlanSizeLimit owner, int max) : IDisposable
    {
        public int Max { get; } = max;

        public void Dispose() => owner.Release(this);
    }
}
