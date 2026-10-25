using System;

namespace Lattice.Api;

/// <summary>
/// Long main-thread drawing work, run inside Lattice's drawing budget per frame so it never stalls a frame (a planner's
/// overlays of a very large plan, for example). Main thread. Lattice implements this; consumers call it. A minor may
/// add members: to fake it in a test, inherit <c>Lattice.Testing.Stubs.StubSlices</c>, which gains every added member
/// in the same release.
/// </summary>
public interface ISlices
{
    /// <summary>
    /// Queues drawing work, from the next frame on, until its step says it is done or the returned handle is
    /// disposed; refused while work with the same id is queued. The work ends with the world. Linear in the queued
    /// drawing work.
    /// </summary>
    Result<IDisposable, Reason> Schedule(SlicedWorkDescriptor work);
}

/// <summary>
/// Drawing work Lattice steps in slices. Each frame <see cref="Step"/> is called on the main thread as often as the
/// drawing budget allows, higher <see cref="Priority"/> first and in scheduling order within a priority; lower
/// priorities get only the time higher ones leave. Keep each step small and of similar cost: a slice stops when the
/// next step is expected to pass the budget, and the first step of a frame always runs. Lattice does not catch what
/// a step throws: the exception skips the rest of this frame's drawing work (consumers' work queued after it), Lattice's
/// own work is not affected (it runs before drawing work each frame), and the work stays queued and steps again next
/// frame. A step that schedules more
/// work sees it start next frame; disposing the handle from inside a step stops the work before its next step.
/// </summary>
/// <param name="Id">Names the work; one queued work per id.</param>
/// <param name="Step">Does one small piece; returns true while more remains, false when done.</param>
public sealed record SlicedWorkDescriptor(SliceId Id, Func<bool> Step)
{
    /// <summary>Where the work runs among other drawing work. Default: <see cref="SlicePriority.Normal"/>.</summary>
    public SlicePriority Priority { get; init; } = SlicePriority.Normal;
}

/// <summary>Where sliced work runs among other work of its kind. Values are fixed for the life of the contract.</summary>
public enum SlicePriority
{
    /// <summary>After normal and high.</summary>
    Low = 1,

    /// <summary>After high.</summary>
    Normal = 2,

    /// <summary>First.</summary>
    High = 3,
}

/// <summary>
/// Names sliced work: a mod's are written <c>owner/name</c>, Lattice's own are bare. <c>default</c> is no id;
/// <see cref="ISlices.Schedule"/> refuses it.
/// </summary>
public readonly record struct SliceId
{
    private readonly string? _value;

    private SliceId(string value) => _value = value;

    /// <summary>The id as written.</summary>
    public string Value => _value ?? "";

    /// <summary>A mod's own id, written <c>owner/name</c>; refused unless <paramref name="name"/> is lowercase words.</summary>
    public static Result<SliceId, Reason> Create(ModId owner, string name) =>
        Token.Qualify(owner, name, nameof(SliceId)).Map(static v => new SliceId(v));

    internal static SliceId Known(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
