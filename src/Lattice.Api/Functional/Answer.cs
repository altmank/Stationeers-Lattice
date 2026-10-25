namespace Lattice.Api;

/// <summary>
/// An answer that may not be ready yet. Checking never stalls a frame: heavy work runs on Lattice's worker or in
/// frame time slices, and until it is done the answer is <see cref="Pending"/>. When pending answers for a revision
/// are ready, <see cref="IGhostEvents.AnswersReady"/> is raised; ask again then.
/// </summary>
public abstract record Answer<T> where T : notnull
{
    private Answer(Revision at) => At = at;

    /// <summary>The board revision the answer is for.</summary>
    public Revision At { get; }

    /// <summary>The answer, computed for a board revision.</summary>
    /// <param name="Value">The answer.</param>
    /// <param name="At">The board revision it was computed for.</param>
    public sealed record Ready(T Value, Revision At) : Answer<T>(At);

    /// <summary>Being worked out for a board revision; <see cref="IGhostEvents.AnswersReady"/> says when to ask again.</summary>
    /// <param name="At">The board revision it is being computed for.</param>
    public sealed record Pending(Revision At) : Answer<T>(At);
}
