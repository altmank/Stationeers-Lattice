using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Testing.Stubs;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A stand-in for Lattice as a consumer would write one: fakes that inherit Lattice.Testing's stubs and override only
/// what the flows use, one per service, with canned answers held here. Every member a flow does not use is the stub's,
/// so it throws naming itself, and a minor that adds members changes none of these classes.
/// </summary>
internal sealed class StubSystem : StubGhostSystem
{
    private readonly bool _isHost;
    private readonly CommandsFake _commands;
    private readonly PreviewFake _preview;
    private readonly ValidationFake _validation;
    private readonly StepsFake _steps;
    private readonly ExecutorsFake _executors;
    private readonly StubGhostEvents _events = new();
    private readonly RegistriesFake _registries;
    private readonly SlicesFake _slices;
    private readonly IPieceCatalogue _catalogue = new StubCatalogue(
        new KitEntry(new KitKey(1001), "ItemStubKit", "Stub kit", ValueList.From(new[] { new PrefabKey(2001), new PrefabKey(2002) })));
    private readonly HashSet<string> _asked = new();
    private readonly Queue<(Ticket Ticket, Command Command, Actor Actor)> _sentToHost = new();

    public StubSystem(bool isHost)
    {
        _isHost = isHost;
        _commands = new CommandsFake(this);
        _preview = new PreviewFake(this);
        _validation = new ValidationFake(this);
        _steps = new StepsFake(this);
        _executors = new ExecutorsFake(this);
        _registries = new RegistriesFake(this);
        _slices = new SlicesFake(this);
    }

    // Canned answers.
    public Func<Command, Actor, Result<Preview, ValueList<Refusal>>> PreviewAnswer { get; set; } =
        (_, _) => Result<Preview, ValueList<Refusal>>.Error(ValueList<Refusal>.Empty);

    /// <summary>When set, the first ask for a preview or a validation is pending.</summary>
    public bool FirstAskPending { get; set; }

    public Func<CommandId, Command, Actor, Outcome> HostAnswer { get; set; } =
        (_, _, _) => new Outcome.Refused(ValueList<Refusal>.Empty);

    public ValidationReport Report { get; set; } = new(new Revision(0), ValueList<Finding>.Empty);

    public Dictionary<GhostId, StepPlan> StepPlans { get; } = new();

    public List<(Command Command, Actor Actor)> Submitted { get; } = new();

    public List<RuleDescriptor> Rules { get; } = new();

    public List<SupportRuleDescriptor> SupportRules { get; } = new();

    public List<(GhostId Ghost, ExecutorStatus Status)> Reports { get; } = new();

    public Dictionary<GhostId, ExecutorId> Claims { get; } = new();

    public List<SlicedWorkDescriptor> Drawing { get; } = new();

    // IGhostSystem
    public override ICommands Commands => _commands;
    public override IPreview Preview => _preview;
    public override IValidation Validation => _validation;
    public override ISteps Steps => _steps;
    public override IExecutors Executors => _executors;
    public override IRegistries Registries => _registries;
    public override IGhostEvents Events => _events;
    public override ISlices Slices => _slices;
    public override IPieceCatalogue Catalogue => _catalogue;
    public override ApiVersion Version => ApiVersion.Current;
    public override bool Has(IntentKind kind) => kind is IntentKind.Build or IntentKind.Remove or IntentKind.Swap or IntentKind.FinishOnly;
    public override ValueList<ModuleStatus> Modules => ValueList<ModuleStatus>.Empty;

    /// <summary>The host answers everything this client sent.</summary>
    public void HostReplies()
    {
        while (_sentToHost.Count > 0)
        {
            var (ticket, command, actor) = _sentToHost.Dequeue();
            var outcome = HostAnswer(ticket.Command, command, actor);
            if (outcome is Outcome.Applied applied)
                _events.RaiseChanged(applied.Changes);
            _events.RaiseAnswered(ticket, outcome);
        }
    }

    public void Complete(GhostId ghost) => _events.RaiseCompleted(ghost, new Completion(Option<StructureRef>.None, DateTimeOffset.UnixEpoch));

    public void Move(ValueList<StructureMove> moves) => _events.RaiseStructuresMoved(moves);

    /// <summary>The worker finishes: pending answers are ready.</summary>
    public void FinishPendingWork() => _events.RaiseAnswersReady(Report.At);

    /// <summary>Steps every queued drawing work once, dropping the finished.</summary>
    public void RunDrawingFrame()
    {
        foreach (var work in Drawing.ToArray())
            if (!work.Step())
                Drawing.Remove(work);
    }

    private bool IsPending(string question) => FirstAskPending && _asked.Add(question);

    private sealed class CommandsFake(StubSystem system) : StubCommands
    {
        public override Submission Submit(CommandId id, Command command, Actor actor)
        {
            system.Submitted.Add((command, actor));
            var ticket = new Ticket(id);
            if (!system._isHost)
            {
                system._sentToHost.Enqueue((ticket, command, actor));
                return new Submission.Sent(ticket);
            }

            var outcome = system.HostAnswer(id, command, actor);
            if (outcome is Outcome.Applied applied)
                system._events.RaiseChanged(applied.Changes);
            return new Submission.Answered(ticket, outcome);
        }
    }

    private sealed class PreviewFake(StubSystem system) : StubPreview
    {
        public override Answer<Result<Preview, ValueList<Refusal>>> Of(Command command, Actor actor) =>
            system.IsPending("preview " + command)
                ? new Answer<Result<Preview, ValueList<Refusal>>>.Pending(system.Report.At)
                : new Answer<Result<Preview, ValueList<Refusal>>>.Ready(system.PreviewAnswer(command, actor), system.Report.At);
    }

    private sealed class ValidationFake(StubSystem system) : StubValidation
    {
        public override Result<Answer<ValidationReport>, Reason> Check(Selector which) =>
            Result<Answer<ValidationReport>, Reason>.Ok(system.IsPending("check " + which)
                ? new Answer<ValidationReport>.Pending(system.Report.At)
                : new Answer<ValidationReport>.Ready(system.Report, system.Report.At));
    }

    private sealed class StepsFake(StubSystem system) : StubSteps
    {
        public override Result<StepPlan, Reason> For(GhostId ghost) =>
            system.StepPlans.TryGetValue(ghost, out var plan)
                ? Result<StepPlan, Reason>.Ok(plan)
                : Result<StepPlan, Reason>.Error(Reason.Of(ReasonCodes.NotFound, RuleId.Contract));
    }

    private sealed class ExecutorsFake(StubSystem system) : StubExecutors
    {
        public override Result<IExecutorHandle, Reason> Register(ExecutorInfo info) =>
            system._isHost
                ? Result<IExecutorHandle, Reason>.Ok(new Handle(system, info.Id))
                : Result<IExecutorHandle, Reason>.Error(Reason.Of(ReasonCodes.HostOnly, RuleId.Contract));

        public override Option<Claim> ClaimOn(GhostId ghost) => Option<Claim>.None;

        public override ValueList<ExecutorReport> StatusOf(GhostId ghost) => ValueList<ExecutorReport>.Empty;
    }

    private sealed class Handle(StubSystem system, ExecutorId id) : StubExecutorHandle
    {
        public override Result<Claim, ClaimRefused> Claim(GhostId ghost, Lease lease)
        {
            if (system.Claims.TryGetValue(ghost, out var holder))
                return Result<Claim, ClaimRefused>.Error(new ClaimRefused(Reason.Of(ReasonCodes.Claimed, RuleId.Contract), Option.Some(holder)));
            system.Claims[ghost] = id;
            return Result<Claim, ClaimRefused>.Ok(new Claim(ghost, id, new GameTime(lease.Seconds)));
        }

        public override Result<Claim, ClaimRefused> Renew(Claim claim) => Result<Claim, ClaimRefused>.Ok(claim);

        public override void Release(Claim claim) => system.Claims.Remove(claim.Ghost);

        public override Result<Unit, Reason> Report(GhostId ghost, ExecutorStatus status)
        {
            system.Reports.Add((ghost, status));
            return Result<Unit, Reason>.Ok(Unit.Value);
        }

        public override void Dispose()
        {
            var mine = new List<GhostId>();
            foreach (var claim in system.Claims)
                if (claim.Value == id)
                    mine.Add(claim.Key);
            foreach (var ghost in mine)
                system.Claims.Remove(ghost);
        }
    }

    private sealed class RegistriesFake(StubSystem system) : StubRegistries
    {
        public override Result<IDisposable, Reason> Register(RuleDescriptor rule)
        {
            system.Rules.Add(rule);
            return Result<IDisposable, Reason>.Ok(new Unregister(() => system.Rules.Remove(rule)));
        }

        public override Result<IDisposable, Reason> Register(SupportRuleDescriptor rule)
        {
            system.SupportRules.Add(rule);
            return Result<IDisposable, Reason>.Ok(new Unregister(() => system.SupportRules.Remove(rule)));
        }

        public override Result<IDisposable, Reason> Register(AttachmentDescriptor attachment) => Result<IDisposable, Reason>.Ok(new Unregister(static () => { }));
    }

    private sealed class SlicesFake(StubSystem system) : StubSlices
    {
        public override Result<IDisposable, Reason> Schedule(SlicedWorkDescriptor work)
        {
            system.Drawing.Add(work);
            return Result<IDisposable, Reason>.Ok(new Unregister(() => system.Drawing.Remove(work)));
        }
    }

    private sealed class Unregister(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
