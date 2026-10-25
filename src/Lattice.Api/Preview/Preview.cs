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
    Bill CostAsSingles);

/// <summary>A cell where a run fails, and why.</summary>
/// <param name="At">The cell.</param>
/// <param name="Why">The reason.</param>
public sealed record CellFailure(Cell At, Reason Why);

/// <summary>
/// What a change would do. Main thread; runs on clients too, against the local board and world copy, and never
/// changes anything. Lattice implements this; consumers call it. A minor may add members, so consumer fakes of it are
/// not covered by compatibility.
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
