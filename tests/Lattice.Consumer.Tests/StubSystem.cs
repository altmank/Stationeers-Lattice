using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A hand-written stand-in for Lattice, as a consumer would write one: canned answers for the services the flows use,
/// <see cref="NotSupportedException"/> for the rest.
/// </summary>
internal sealed class StubSystem : IGhostSystem, ICommands, IPreview, IValidation, ISteps, IExecutors, IGhostEvents, IRegistries, ISlices
{
    private readonly bool _isHost;

    public StubSystem(bool isHost) => _isHost = isHost;

    // Canned answers.
    public Func<Command, Actor, Result<Preview, ValueList<Refusal>>> PreviewAnswer { get; set; } =
        (_, _) => Result<Preview, ValueList<Refusal>>.Error(ValueList<Refusal>.Empty);

    /// <summary>When set, the first ask for a preview or a validation is pending.</summary>
    public bool FirstAskPending { get; set; }

    private readonly HashSet<string> _asked = new();

    public Func<CommandId, Command, Actor, Outcome> HostAnswer { get; set; } =
        (_, _, _) => new Outcome.Refused(ValueList<Refusal>.Empty);

    public ValidationReport Report { get; set; } = new(new Revision(0), ValueList<Finding>.Empty);

    public Dictionary<GhostId, StepPlan> StepPlans { get; } = new();

    public List<(Command Command, Actor Actor)> Submitted { get; } = new();

    public List<SupportRuleDescriptor> SupportRules { get; } = new();

    public List<(GhostId Ghost, ExecutorStatus Status)> Reports { get; } = new();

    public Dictionary<GhostId, ExecutorId> Claims { get; } = new();

    private readonly Queue<(Ticket Ticket, Command Command, Actor Actor)> _sentToHost = new();

    // IGhostSystem
    public IGhostBoard Board => throw new NotSupportedException();
    public IWorldQuery World => throw new NotSupportedException();
    public IPlans Plans => throw new NotSupportedException();
    public ICommands Commands => this;
    public IPreview Preview => this;
    public IValidation Validation => this;
    public ISteps Steps => this;
    public IBills Bills => throw new NotSupportedException();
    public ISwaps Swaps => throw new NotSupportedException();
    public IAnalysis Analysis => throw new NotSupportedException();
    public IExecutors Executors => this;
    public IRegistries Registries => this;
    public IInspector Inspector => throw new NotSupportedException();
    public IGhostEvents Events => this;
    public ISlices Slices => this;
    public IGhostStyles Styles => throw new NotSupportedException();
    public IPieceCatalogue Catalogue { get; set; } = new StubCatalogue(
        new KitEntry(new KitKey(1001), "ItemStubKit", "Stub kit", ValueList.From(new[] { new PrefabKey(2001), new PrefabKey(2002) })));
    public ApiVersion Version => ApiVersion.Current;
    public bool Has(IntentKind kind) => kind is IntentKind.Build or IntentKind.Remove or IntentKind.Swap or IntentKind.FinishOnly;
    public ValueList<ModuleStatus> Modules { get; set; } = ValueList<ModuleStatus>.Empty;

    // ICommands
    public Submission Submit(CommandId id, Command command, Actor actor)
    {
        Submitted.Add((command, actor));
        var ticket = new Ticket(id);
        if (!_isHost)
        {
            _sentToHost.Enqueue((ticket, command, actor));
            return new Submission.Sent(ticket);
        }

        var outcome = HostAnswer(id, command, actor);
        if (outcome is Outcome.Applied applied)
            Changed?.Invoke(applied.Changes);
        return new Submission.Answered(ticket, outcome);
    }

    /// <summary>The host answers everything this client sent.</summary>
    public void HostReplies()
    {
        while (_sentToHost.Count > 0)
        {
            var (ticket, command, actor) = _sentToHost.Dequeue();
            var outcome = HostAnswer(ticket.Command, command, actor);
            if (outcome is Outcome.Applied applied)
                Changed?.Invoke(applied.Changes);
            Answered?.Invoke(ticket, outcome);
        }
    }

    public void Complete(GhostId ghost) => Completed?.Invoke(ghost, new Completion(Option<StructureRef>.None, DateTimeOffset.UnixEpoch));

    public void Move(ValueList<StructureMove> moves) => StructuresMoved?.Invoke(moves);

    /// <summary>The worker finishes: pending answers are ready.</summary>
    public void FinishPendingWork() => AnswersReady?.Invoke(Report.At);

    private bool IsPending(string question) => FirstAskPending && _asked.Add(question);

    // IPreview, IValidation, ISteps
    public Answer<Result<Preview, ValueList<Refusal>>> Of(Command command, Actor actor) =>
        IsPending("preview " + command)
            ? new Answer<Result<Preview, ValueList<Refusal>>>.Pending(Report.At)
            : new Answer<Result<Preview, ValueList<Refusal>>>.Ready(PreviewAnswer(command, actor), Report.At);

    public Result<Answer<ValidationReport>, Reason> Check(Selector which) =>
        Result<Answer<ValidationReport>, Reason>.Ok(IsPending("check " + which)
            ? new Answer<ValidationReport>.Pending(Report.At)
            : new Answer<ValidationReport>.Ready(Report, Report.At));

    public Result<StepPlan, Reason> For(GhostId ghost) =>
        StepPlans.TryGetValue(ghost, out var plan)
            ? Result<StepPlan, Reason>.Ok(plan)
            : Result<StepPlan, Reason>.Error(Reason.Of(ReasonCodes.NotFound, RuleId.Contract));

    // IExecutors
    public Result<IExecutorHandle, Reason> Register(ExecutorInfo info) =>
        _isHost
            ? Result<IExecutorHandle, Reason>.Ok(new Handle(this, info.Id))
            : Result<IExecutorHandle, Reason>.Error(Reason.Of(ReasonCodes.HostOnly, RuleId.Contract));

    public Option<Claim> ClaimOn(GhostId ghost) => Option<Claim>.None;

    public ValueList<ExecutorReport> StatusOf(GhostId ghost) => ValueList<ExecutorReport>.Empty;

    private sealed class Handle(StubSystem system, ExecutorId id) : IExecutorHandle
    {
        public Result<Claim, ClaimRefused> Claim(GhostId ghost, Lease lease)
        {
            if (system.Claims.TryGetValue(ghost, out var holder))
                return Result<Claim, ClaimRefused>.Error(new ClaimRefused(Reason.Of(ReasonCodes.Claimed, RuleId.Contract), Option.Some(holder)));
            system.Claims[ghost] = id;
            return Result<Claim, ClaimRefused>.Ok(new Claim(ghost, id, new GameTime(lease.Seconds)));
        }

        public Result<Claim, ClaimRefused> Renew(Claim claim) => Result<Claim, ClaimRefused>.Ok(claim);

        public void Release(Claim claim) => system.Claims.Remove(claim.Ghost);

        public Result<Unit, Reason> Report(GhostId ghost, ExecutorStatus status)
        {
            system.Reports.Add((ghost, status));
            return Result<Unit, Reason>.Ok(Unit.Value);
        }

        public void Dispose()
        {
            var mine = new List<GhostId>();
            foreach (var claim in system.Claims)
                if (claim.Value == id)
                    mine.Add(claim.Key);
            foreach (var ghost in mine)
                system.Claims.Remove(ghost);
        }
    }

    // IGhostEvents
    public event Action<ChangeSet>? Changed;
    public event Action<GhostId, Completion>? Completed;
    public event Action<GhostId>? StatusChanged { add { } remove { } }
    public event Action<Ticket, Outcome>? Answered;
    public event Action<PlanId>? PlanStateChanged { add { } remove { } }
    public event Action<Maker, ValueList<BlockedGhost>>? Blocked { add { } remove { } }
    public event Action<ValueList<StructureMove>>? StructuresMoved;
    public event Action<Revision>? AnswersReady;
    public event Action<LoadReport>? Loaded { add { } remove { } }
    public event Action? BoardReset { add { } remove { } }

    // IRegistries
    public List<RuleDescriptor> Rules { get; } = new();

    public Result<IDisposable, Reason> Register(RuleDescriptor rule)
    {
        Rules.Add(rule);
        return Result<IDisposable, Reason>.Ok(new Unregister(() => Rules.Remove(rule)));
    }

    public Result<IDisposable, Reason> Register(SupportRuleDescriptor rule)
    {
        SupportRules.Add(rule);
        return Result<IDisposable, Reason>.Ok(new Unregister(() => SupportRules.Remove(rule)));
    }

    public Result<IDisposable, Reason> Register(AnalyzerDescriptor analyzer) => throw new NotSupportedException();
    public Result<IDisposable, Reason> Register(AttachmentDescriptor attachment) => Result<IDisposable, Reason>.Ok(new Unregister(() => { }));
    public Result<IDisposable, Reason> Register(CatalogueAddition addition) => throw new NotSupportedException();
    public Result<IDisposable, Reason> Register<TCommand>(CommandExpander<TCommand> expander) where TCommand : Command => throw new NotSupportedException();
    public Result<IPolicyHandle, Reason> Install(AccessPolicy policy) => throw new NotSupportedException();
    public Result<IDisposable, Reason> LimitPlanSize(int maxGhosts) => throw new NotSupportedException();

    // ISlices
    public List<SlicedWorkDescriptor> Drawing { get; } = new();

    public Result<IDisposable, Reason> Schedule(SlicedWorkDescriptor work)
    {
        Drawing.Add(work);
        return Result<IDisposable, Reason>.Ok(new Unregister(() => Drawing.Remove(work)));
    }

    /// <summary>Steps every queued drawing work once, dropping the finished.</summary>
    public void RunDrawingFrame()
    {
        foreach (var work in Drawing.ToArray())
            if (!work.Step())
                Drawing.Remove(work);
    }

    private sealed class Unregister(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
