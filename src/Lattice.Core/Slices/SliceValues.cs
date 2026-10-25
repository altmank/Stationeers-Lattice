using System;
using Lattice.Api;

namespace Lattice.Core.Slices;

/// <summary>
/// Where a job runs among the jobs of its kind: higher ranks first, equal ranks in scheduling order. Any rank is
/// valid; <c>default</c> is <see cref="Normal"/>.
/// </summary>
internal readonly record struct Priority(int Rank)
{
    public static readonly Priority Low = new(-1);

    public static readonly Priority Normal = new(0);

    public static readonly Priority High = new(1);
}

/// <summary>The time one kind of work may spend per frame; always positive. <c>default</c> is no time at all.</summary>
internal readonly record struct SliceBudget
{
    private SliceBudget(TimeSpan length) => Length = length;

    public TimeSpan Length { get; }

    public static Result<SliceBudget, Reason> Create(TimeSpan length) =>
        length > TimeSpan.Zero
            ? Result<SliceBudget, Reason>.Ok(new SliceBudget(length))
            : Result<SliceBudget, Reason>.Error(Reason.InvalidValue(nameof(SliceBudget), length.ToString(), "positive"));

    internal static SliceBudget Milliseconds(int milliseconds) =>
        new(TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)));
}

/// <summary>The budget per frame of each kind of work. Kinds are measured separately: one never spends another's time.</summary>
internal sealed record FrameBudgets(SliceBudget Checking, SliceBudget Sending, SliceBudget Saving, SliceBudget Drawing)
{
    /// <summary>
    /// Lattice's starting budgets: 6 ms of a 60 fps frame's 16.7 ms in all.
    /// </summary>
    public static FrameBudgets Default { get; } = new(
        Checking: SliceBudget.Milliseconds(2),
        Sending: SliceBudget.Milliseconds(1),
        Saving: SliceBudget.Milliseconds(1),
        Drawing: SliceBudget.Milliseconds(2));

    /// <summary>The budget of a kind; no time for a kind this version does not know.</summary>
    public SliceBudget For(WorkKind kind) => kind switch
    {
        WorkKind.Checking => Checking,
        WorkKind.Sending => Sending,
        WorkKind.Saving => Saving,
        WorkKind.Drawing => Drawing,
        _ => default,
    };
}

/// <summary>Where a scheduled job is in its life.</summary>
internal enum JobState
{
    /// <summary>Waiting for or inside its slices.</summary>
    Running = 1,

    /// <summary>Its last step returned <see cref="StepOutcome.Done"/>.</summary>
    Done = 2,

    /// <summary>Cancelled before it finished; it took no step after the cancel.</summary>
    Cancelled = 3,

    /// <summary>A step of Lattice's own work threw; the job ended there and the scheduler reported it.</summary>
    Faulted = 4,
}

/// <summary>The rule ids of the slices module's refusals.</summary>
internal static class SliceRules
{
    public static readonly RuleId Scheduler = RuleId.Known("lattice.slices");

    public static readonly RuleId SizeLimit = RuleId.Known("lattice.size-limit");
}
