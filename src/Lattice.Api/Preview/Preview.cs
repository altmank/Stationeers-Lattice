namespace Lattice.Api;

/// <summary>What a command would do, without doing it.</summary>
/// <param name="WouldChange">The change set it would make.</param>
/// <param name="Findings">Validation of the new and changed ghosts in context.</param>
/// <param name="Bill">The bill for the new and changed ghosts.</param>
/// <param name="Effects">What they would do to networks, rooms and gas.</param>
/// <param name="Run">For a run or route: its cells, pieces, first failing cell and cost.</param>
public sealed record Preview(
    ChangeSet WouldChange,
    ValueList<Finding> Findings,
    Bill Bill,
    AnalysisReport Effects,
    Option<RunReport> Run);

/// <summary>A planned run: cells, pieces (longest first), the first cell that fails, and its cost against single pieces.</summary>
/// <param name="Cells">The cells, in order.</param>
/// <param name="Pieces">The pieces placed.</param>
/// <param name="FirstFailure">The first cell that fails, if any.</param>
/// <param name="Cost">The bill for the pieces placed.</param>
/// <param name="CostAsSingles">The bill if every cell were a single piece.</param>
public sealed record RunReport(
    ValueList<Cell> Cells,
    ValueList<Placement> Pieces,
    Option<CellFailure> FirstFailure,
    Bill Cost,
    Bill CostAsSingles)
{
    /// <summary>
    /// For each port end of the request, the from end first: the port it resolved to and the run piece that joins it;
    /// none when the run reached the port's cell but no piece of the kit joins it there (then
    /// <see cref="FirstFailure"/> names that cell). Empty for a request with no port ends. Added in contract 0.8.
    /// </summary>
    public ValueList<PortJoin> PortEnds { get; init; } = ValueList<PortJoin>.Empty;

    /// <summary>
    /// The kit the run was laid with, when <see cref="LayOptions.MatchGrade"/> chose another than the request's.
    /// Added in contract 0.9.
    /// </summary>
    public Option<KitKey> LaidWith { get; init; }
}

/// <summary>A port end of a run, the port it resolved to, and the piece that joins it. Added in contract 0.8.</summary>
/// <param name="End">The end as the request named it.</param>
/// <param name="Port">The port it resolved to when the run was laid.</param>
/// <param name="JoinedBy">The run piece that joins the port: one laid by the run, or one already there; none when no piece of the kit joins it.</param>
public sealed record PortJoin(PortEnd End, Port Port, Option<Placement> JoinedBy);

/// <summary>A cell where a run fails, and why.</summary>
/// <param name="At">The cell.</param>
/// <param name="Why">The reason.</param>
public sealed record CellFailure(Cell At, Reason Why);

/// <summary>
/// What a change would do. Main thread; runs on clients too, against the local board and world copy, and never changes
/// anything. Lattice implements this; consumers call it. A minor may add members: to fake it in a test, inherit
/// <c>Lattice.Testing.Stubs.StubPreview</c>, which gains every added member in the same release.
/// </summary>
public interface IPreview
{
    /// <summary>
    /// The preview of a command for an actor, or the refusals it would get; pending while a big change is worked out
    /// in time slices. The refusals are inside the answer because a command's refusals are known only after it is
    /// expanded, which may take several frames (<see cref="IAnalysis.Of"/> nests the other way). Cost on the main
    /// thread: bounded by the frame time slice.
    /// </summary>
    Answer<Result<Preview, ValueList<Refusal>>> Of(Command command, Actor actor);
}
