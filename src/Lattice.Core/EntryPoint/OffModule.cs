using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Selectors;

namespace Lattice.Core.EntryPoint;

/// <summary>
/// The service of a module that is off in this world: every call answers with the module's off reason
/// (<see cref="ReasonCodes.ModuleMissing"/>, fact <c>module</c>), reads that cannot refuse answer empty. Constant cost.
/// </summary>
internal sealed class OffModule(Reason why) : IValidation, ISteps, IBills, ISwaps, IAnalysis, IPreview, IExecutors, IInspector, ICommands
{
    public Reason Why => why;

    public Result<Answer<ValidationReport>, Reason> Check(Selector which) => Result<Answer<ValidationReport>, Reason>.Error(why);

    public Result<StepPlan, Reason> For(GhostId ghost) => Result<StepPlan, Reason>.Error(why);

    Result<Bill, Reason> IBills.For(Selector which) => Result<Bill, Reason>.Error(why);

    public Result<ValueList<SwapOption>, Reason> OptionsFor(StructureRef structure) => Result<ValueList<SwapOption>, Reason>.Error(why);

    public Result<Answer<AnalysisReport>, Reason> Of(Selector which) => Result<Answer<AnalysisReport>, Reason>.Error(why);

    public Answer<Result<Lattice.Api.Preview, ValueList<Refusal>>> Of(Command command, Actor actor) =>
        new Answer<Result<Lattice.Api.Preview, ValueList<Refusal>>>.Ready(
            Result<Lattice.Api.Preview, ValueList<Refusal>>.Error(ValueList.Of(new Refusal(why, ValueList<GhostId>.Empty))), default);

    public Result<IExecutorHandle, Reason> Register(ExecutorInfo info) => Result<IExecutorHandle, Reason>.Error(why);

    public Option<Claim> ClaimOn(GhostId ghost) => Option<Claim>.None;

    public ValueList<ExecutorReport> StatusOf(GhostId ghost) => ValueList<ExecutorReport>.Empty;

    public Result<GhostStory, Reason> Story(GhostId ghost) => Result<GhostStory, Reason>.Error(why);

    public Result<PlanStory, Reason> Story(PlanId plan) => Result<PlanStory, Reason>.Error(why);

    public Result<ValueList<string>, Reason> Export(PlanId plan) => Result<ValueList<string>, Reason>.Error(why);

    public Submission Submit(CommandId id, Command command, Actor actor) =>
        new Submission.Answered(new Ticket(id), new Outcome.Refused(ValueList.Of(new Refusal(why, ValueList<GhostId>.Empty))));
}

/// <summary>The built world when the world bridge is off: nothing stands, no rooms, no networks.</summary>
internal sealed class NoWorld : IWorldView
{
    public Option<StructureInfo> Structure(ReferenceId id) => Option<StructureInfo>.None;

    public ValueList<StructureInfo> At(Cell cell) => ValueList<StructureInfo>.Empty;

    public Option<NetworkRef> NetworkOf(ReferenceId id) => Option<NetworkRef>.None;

    public Option<NetworkInfo> Network(NetworkRef network) => Option<NetworkInfo>.None;

    public Option<RoomRef> RoomAt(Cell cell) => Option<RoomRef>.None;

    public Option<RoomInfo> Room(RoomRef room) => Option<RoomInfo>.None;

    public Kilopascals PressureDifference(Cell cell, Direction face) => default;

    public bool Terrain(Cell large) => false;
}

/// <summary>
/// <see cref="IWorldQuery"/> for consumers: the world view's reads and the structure selectors, or the selectors'
/// off reason when they are off. Real structures only: nothing is assumed.
/// </summary>
internal sealed class WorldQuery(IWorldView world, StructureSelection? selection, Reason selectorsOff) : IWorldQuery
{
    public Option<StructureInfo> Structure(ReferenceId id) => world.Structure(id);

    public ValueList<StructureInfo> At(Cell cell) => world.At(cell);

    public Result<ValueList<StructureRef>, Reason> Select(StructureSelector selector) =>
        selection is null ? Result<ValueList<StructureRef>, Reason>.Error(selectorsOff) : selection.Select(selector);

    public Option<NetworkRef> NetworkOf(ReferenceId id) => world.NetworkOf(id);

    public Option<RoomRef> RoomAt(Cell cell) => world.RoomAt(cell);

    public Option<GhostId> AssumedFrom(StructureRef structure) => Option<GhostId>.None;
}
