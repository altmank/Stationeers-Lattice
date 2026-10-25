namespace Lattice.Api;

/// <summary>The six facts Lattice works out for a plan, at one board revision. Never set by hand.</summary>
/// <param name="Plan">The plan.</param>
/// <param name="At">The board revision the facts are for.</param>
/// <param name="ValidAlone">No two ghosts clash; every piece has support in the plan or the world.</param>
/// <param name="ValidInWorld">Nothing built since blocks it; no ghost is stale.</param>
/// <param name="BuildableNext">Ghosts whose support exists and that nothing blocks.</param>
/// <param name="Progress">Done, started, remaining.</param>
/// <param name="Consequences">Safety warnings.</param>
/// <param name="Cost">Materials and tools still needed.</param>
public sealed record PlanFacts(
    PlanId Plan,
    Revision At,
    Validity ValidAlone,
    Validity ValidInWorld,
    ValueList<GhostId> BuildableNext,
    Progress Progress,
    ValueList<SafetyWarning> Consequences,
    Bill Cost);

/// <summary>Whether a set of ghosts can work: valid, or the findings that say why not.</summary>
public abstract record Validity
{
    private Validity() { }

    /// <summary>Every ghost passes or waits.</summary>
    public sealed record Valid : Validity;

    /// <summary>At least one ghost will fail.</summary>
    /// <param name="Failing">The findings that fail.</param>
    public sealed record Invalid(ValueList<Finding> Failing) : Validity;
}

/// <summary>How far a plan has got, in ghosts.</summary>
/// <param name="Done">Ghosts done.</param>
/// <param name="Started">Ghosts started.</param>
/// <param name="Remaining">Ghosts not started.</param>
public readonly record struct Progress(int Done, int Started, int Remaining);

/// <summary>An unintended consequence of a plan; a warning, never a block. Names the ghosts behind it.</summary>
/// <param name="Why">What would happen, with the facts behind it.</param>
/// <param name="Ghosts">The ghosts that cause it.</param>
public sealed record SafetyWarning(Reason Why, ValueList<GhostId> Ghosts);

/// <summary>A plan's state, derived from its facts.</summary>
public abstract record PlanState
{
    private PlanState() { }

    /// <summary>No ghosts.</summary>
    public sealed record Empty : PlanState;

    /// <summary>At least one ghost cannot work.</summary>
    public sealed record HasErrors : PlanState;

    /// <summary>Every ghost passes or waits; nothing built yet.</summary>
    public sealed record Valid : PlanState;

    /// <summary>Valid, and at least one ghost is done or started.</summary>
    public sealed record InProgress : PlanState;

    /// <summary>Every ghost is done.</summary>
    public sealed record Complete : PlanState;
}

/// <summary>
/// Plan-level answers. Main thread. Lattice implements this; consumers call it. A minor may add members, so consumer
/// fakes of it are not covered by compatibility.
/// </summary>
public interface IPlans
{
    /// <summary>The active plans the local side may see. Cached per revision: allocates nothing on an unchanged board.</summary>
    ValueList<Plan> All { get; }

    /// <summary>The plan loose ghosts go in.</summary>
    PlanId Default { get; }

    /// <summary>An active plan by id. Constant cost.</summary>
    Option<Plan> Find(PlanId plan);

    /// <summary>A plan's six facts, or pending while they are worked out in time slices; refused for an unknown plan. Constant cost on the main thread.</summary>
    Result<Answer<PlanFacts>, Reason> Facts(PlanId plan);

    /// <summary>A plan's state, derived from its facts, or pending with them; refused for an unknown plan.</summary>
    Result<Answer<PlanState>, Reason> State(PlanId plan);

    /// <summary>The index of finished plans. Read from the index file once per world.</summary>
    ValueList<FinishedPlan> Finished { get; }

    /// <summary>
    /// How many changes a plan's file holds beyond the point the loaded save remembers (after a crash, the newest
    /// save is behind its plan files); <see cref="Command.CatchUp"/> moves the plan forward to them. Refused for an
    /// unknown plan. Constant cost.
    /// </summary>
    Result<int, Reason> AheadOfSave(PlanId plan);

    /// <summary>What the last world load found, once a world has loaded; also raised once as <see cref="IGhostEvents.Loaded"/>.</summary>
    Option<LoadReport> LastLoad { get; }

    /// <summary>What a plan (active or finished) holds, as a template; refused for an unknown plan. Reads a finished plan's file.</summary>
    Result<Template, Reason> AsTemplate(PlanId plan, TemplateName name);
}

/// <summary>Whether the loaded save had a record of how far through each plan file it had got.</summary>
public abstract record SaveRecord
{
    private SaveRecord() { }

    /// <summary>It had one: every plan was rebuilt to the point it remembers.</summary>
    public sealed record Found : SaveRecord;

    /// <summary>
    /// It had none (made before Lattice was installed, copied in, or its record lost): every plan opened at its newest
    /// line on its most recent branch.
    /// </summary>
    public sealed record Missing : SaveRecord;
}

/// <summary>A plan whose file holds changes beyond the loaded save.</summary>
/// <param name="Plan">The plan.</param>
/// <param name="Changes">How many changes.</param>
public sealed record PlanAhead(PlanId Plan, int Changes);

/// <summary>What a world load found: the save's record, and the plans whose files are ahead of it.</summary>
/// <param name="Record">Whether the save had a record.</param>
/// <param name="Ahead">Plans with changes after the save.</param>
public sealed record LoadReport(SaveRecord Record, ValueList<PlanAhead> Ahead);
