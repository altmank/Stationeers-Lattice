namespace Lattice.Api;

/// <summary>
/// What validation says about one ghost. A later minor may add cases; a consumer skips and reports one it does not know.
/// </summary>
public abstract record Finding
{
    private Finding(GhostId ghost) => Ghost = ghost;

    /// <summary>The ghost.</summary>
    public GhostId Ghost { get; }

    /// <summary>It can be built as things stand (with earlier ghosts assumed built).</summary>
    /// <param name="Ghost">The ghost.</param>
    public sealed record Passes(GhostId Ghost) : Finding(Ghost);

    /// <summary>It can be built once other ghosts are.</summary>
    /// <param name="Ghost">The ghost.</param>
    /// <param name="Others">The ghosts it waits for, in any plan.</param>
    public sealed record WaitsFor(GhostId Ghost, ValueList<GhostId> Others) : Finding(Ghost);

    /// <summary>It cannot be built.</summary>
    /// <param name="Ghost">The ghost.</param>
    /// <param name="Why">The reason.</param>
    public sealed record WillFail(GhostId Ghost, Reason Why) : Finding(Ghost);

    /// <summary>It can be built, with a warning (build safety, a rule's warning).</summary>
    /// <param name="Ghost">The ghost.</param>
    /// <param name="Why">The reason.</param>
    public sealed record Warns(GhostId Ghost, Reason Why) : Finding(Ghost);
}

/// <summary>The findings for a set of ghosts, in build order, at one board revision.</summary>
/// <param name="At">The board revision.</param>
/// <param name="Findings">One or more findings per ghost, in build order.</param>
public sealed record ValidationReport(Revision At, ValueList<Finding> Findings);

/// <summary>
/// Validates ghosts in build order on the world plus the ghosts before them assumed built. Main thread; heavy checks
/// run on Lattice's worker and the answer is cached per revision. Lattice implements this; consumers call it. A minor
/// may add members: to fake it in a test, inherit <c>Lattice.Testing.Stubs.StubValidation</c>, which gains every added
/// member in the same release.
/// </summary>
public interface IValidation
{
    /// <summary>
    /// The findings for the ghosts a selector picks, or pending while the worker checks them; refused for an unknown
    /// selector kind. Cost on the main thread: constant when cached for the revision; otherwise it queues the check of
    /// the ghosts near the change and those depending on them.
    /// </summary>
    Result<Answer<ValidationReport>, Reason> Check(Selector which);
}
