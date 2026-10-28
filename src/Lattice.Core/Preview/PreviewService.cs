using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Analysis;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Commands;
using Lattice.Core.Ports;
using Lattice.Core.Runs;
using Lattice.Core.Safety;
using Lattice.Core.Slices;
using Lattice.Core.Steps;
using Lattice.Core.Support;
using Lattice.Core.Validation;
using PreviewAnswer = Lattice.Api.Answer<Lattice.Api.Result<Lattice.Api.Preview, Lattice.Api.ValueList<Lattice.Api.Refusal>>>;

namespace Lattice.Core.Preview;

/// <summary>What previews are built from, for one world, on either seat.</summary>
/// <param name="Board">The board (the host's, or a client's replica).</param>
/// <param name="Commands">The command service, for expansions and to queue what callbacks submit.</param>
/// <param name="World">The world view.</param>
/// <param name="Mirror">The world mirror, whose revision says when the world moved.</param>
/// <param name="Steps">The step planner, for bills.</param>
/// <param name="Effects">The effects of ghosts on networks, rooms and gas.</param>
/// <param name="Safety">Build safety, whose warnings become findings.</param>
/// <param name="Analyzers">The mods' analyzers.</param>
/// <param name="Runs">The run plans the run and route expanders laid.</param>
/// <param name="Scheduler">The frame scheduler the preview's main-thread work runs in.</param>
/// <param name="Validation">Validation; none when it is off.</param>
internal sealed record PreviewParts(
    GhostBoard Board,
    CommandService Commands,
    IWorldView World,
    WorldMirror Mirror,
    StepPlanner Steps,
    PlanEffects Effects,
    BuildSafety Safety,
    Analyzers Analyzers,
    RunReportBook Runs,
    FrameScheduler Scheduler,
    ValidationService? Validation);

/// <summary>
/// What a command would do, without doing it (<see cref="IPreview"/>). In order: the command's expansion
/// (<see cref="CommandService.Expansion"/>: pending while sliced work runs); the board's dry run
/// (<see cref="GhostBoard.Try"/>), whose refusals are the answer; then, at once, a validation trial of the change on
/// Lattice's worker (<see cref="ValidationService.Trial"/>, which also calls the worker analyzers), and one step in the
/// checking budget that stages the change (<see cref="GhostBoard.Trial.While{T}"/>) for build safety, effects and the
/// main-thread analyzers, then works out the bill and, for a run or route, its report. The answer is pending until all
/// of it is in; <see cref="IGhostEvents.AnswersReady"/> says when to ask again. Findings are validation's findings for
/// the new and changed ghosts, then build safety's warnings for them as <see cref="Finding.Warns"/>, never
/// refusals; with validation off, only the warnings.
/// <para>
/// Answers are kept per command and actor until the board or the world moves, which drops them and cancels their
/// work; a repeat ask meanwhile is a lookup that allocates nothing. A command submitted from an analyzer
/// is queued and applied after the staged step. Analyzers are plain calls: an exception from a main-thread
/// one leaves the preview's step (the scheduler's fault handling), from a worker one validation's frame. A client
/// previews over its own replica, mirror and validation; the host's answer wins once the command is sent.
/// Main thread. Cost: a lookup, or the expansion and the board's dry run; the staged step is
/// linear in the change.
/// </para>
/// </summary>
internal sealed class PreviewService(PreviewParts parts) : IPreview
{
    private readonly Dictionary<(Command Command, Actor Actor), Entry> _kept = [];
    private (Revision Board, long Mirror) _at;

    public PreviewAnswer Of(Command command, Actor actor)
    {
        Moved();
        var key = (command, actor);
        if (!_kept.TryGetValue(key, out var entry))
            _kept[key] = entry = new Entry(command, actor, new PreviewAnswer.Pending(_at.Board));
        if (entry.Answer is { } answer)
            return answer;
        if (entry.Trial is null && Start(entry) is { } settled)
            return settled;
        if (!entry.Done || entry.Checking is { IsReady: false })
            return entry.Pending;
        return Settle(entry, Result<Lattice.Api.Preview, ValueList<Refusal>>.Ok(Assemble(entry)));
    }

    /// <summary>Ends with the world: cancels every preview's work.</summary>
    public void End() => Drop();

    // The expansion and the dry run; an answer when either refuses or is pending, none once the work has started.
    private PreviewAnswer? Start(Entry entry)
    {
        var expansion = parts.Commands.Expansion(entry.Command, entry.Actor);
        if (expansion is not Answer<Result<Command, ValueList<Refusal>>>.Ready expanded)
            return entry.Pending;
        if (!expanded.Value.TryGet(out var primitive))
            return Settle(entry, Refused(expanded.Value.ErrorOf()));
        var tried = parts.Board.Try(primitive, entry.Actor);
        if (!tried.TryGet(out var trial))
            return Settle(entry, Refused(tried.ErrorOf()));

        entry.Trial = trial;
        entry.Ghosts = ValidationTrial.GhostsOf(trial.Changes);
        if (parts.Validation is { Faulted: false } validation)
            entry.Checking = validation.Trial(trial.Changes, entry.Ghosts);
        var work = new PreviewWork(this, entry);
        if (parts.Scheduler.Schedule(WorkKind.Checking, work, Priority.Normal).TryGet(out var job))
            entry.Job = job;
        else
            work.Step();
        return null;
    }

    private static Result<Lattice.Api.Preview, ValueList<Refusal>> Refused(ValueList<Refusal> why) =>
        Result<Lattice.Api.Preview, ValueList<Refusal>>.Error(why);

    private static PreviewAnswer Settle(Entry entry, Result<Lattice.Api.Preview, ValueList<Refusal>> result) =>
        entry.Answer = new PreviewAnswer.Ready(result, entry.Pending.At);

    // The staged step: build safety, effects and main-thread analyzers on the board as it would be; then the bill and the run.
    private void Work(Entry entry)
    {
        var trial = entry.Trial!;
        var ghosts = entry.Ghosts;
        var (warnings, effects) = parts.Commands.WhileApplying(() => trial.While(() =>
        {
            var view = parts.Board.ViewFor(entry.Actor);
            var facts = new List<AnalyzerFact>();
            if (parts.Analyzers.Any(CallbackThread.Main))
                parts.Analyzers.Facts(CallbackThread.Main, new AnalyzerInput(ghosts, new PlannedWorld(view, new ViewQuery(parts.World))), facts);
            return (parts.Safety.Check(ghosts, view, parts.World), parts.Effects.Of(ghosts, view, parts.World, ValueList.From(facts)));
        }));
        entry.Warnings = warnings;
        entry.Effects = effects;
        entry.Bill = Bill(ghosts);
        var runs = new List<RunReport>(1);
        Reports(entry.Command, ghosts, runs);
        entry.Runs = ValueList.From(runs);
        entry.Run = runs.Count > 0 ? Option.Some(runs[0]) : Option<RunReport>.None;
        entry.Done = true;
        parts.Board.Events.Queue(new AnswersReadyEvent(entry.Pending.At));
    }

    // The report of each run or route step the expanders laid, in step order with nested batches flattened; each billed for its own pieces.
    private void Reports(Command command, ValueList<Ghost> ghosts, List<RunReport> reports)
    {
        if (command is Command.Batch batch)
        {
            foreach (var step in batch.Steps)
                Reports(step, ghosts, reports);
            return;
        }

        if (command is Command.PlanRun or Command.PlanRoute && parts.Runs.Find(command).TryGet(out var plan))
            reports.Add(plan.Report(pieces => Bill(Builds(pieces, ghosts))));
    }

    private Bill Bill(IReadOnlyList<Ghost> ghosts)
    {
        var plans = new List<StepPlan>(ghosts.Count);
        foreach (var ghost in ghosts)
            if (parts.Steps.For(ghost).TryGet(out var plan))
                plans.Add(plan);
        return BillMaker.Of(plans);
    }

    // Build ghosts for a run's pieces, shaped like the change's first ghost, so the step planner prices them.
    private static List<Ghost> Builds(ValueList<Placement> pieces, ValueList<Ghost> ghosts)
    {
        var builds = new List<Ghost>(pieces.Count);
        if (ghosts.Count == 0)
            return builds;
        var shape = ghosts[0] with { State = new GhostState.Planned() };
        foreach (var piece in pieces)
            builds.Add(shape with { Intent = new Intent.Build(piece, Option<Finish>.None) });
        return builds;
    }

    private static Lattice.Api.Preview Assemble(Entry entry)
    {
        var findings = new List<Finding>();
        if (entry.Checking?.Answer is Answer<ValueList<Finding>>.Ready checkedFindings)
            foreach (var finding in checkedFindings.Value)
                findings.Add(finding);
        foreach (var warning in entry.Warnings)
            foreach (var ghost in warning.Ghosts)
                findings.Add(new Finding.Warns(ghost, warning.Why));
        var effects = entry.Effects!;
        if (entry.Checking is { Facts.Count: > 0 } checking)
        {
            var facts = new List<AnalyzerFact>(effects.Facts.Count + checking.Facts.Count);
            foreach (var fact in effects.Facts)
                facts.Add(fact);
            foreach (var fact in checking.Facts)
                facts.Add(fact);
            effects = effects with { Facts = ValueList.From(facts) };
        }

        return new Lattice.Api.Preview(entry.Trial!.Changes, ValueList.From(findings), entry.Bill!, effects, entry.Run) { Runs = entry.Runs };
    }

    private Revision Revision => parts.Board.Revision;

    private void Moved()
    {
        var now = (parts.Board.Revision, parts.Mirror.Revision);
        if (now == _at)
            return;
        Drop();
        _at = now;
    }

    private void Drop()
    {
        foreach (var entry in _kept.Values)
        {
            entry.Job?.Cancel();
            if (entry.Checking is { } checking)
                parts.Validation?.Drop(checking);
        }

        _kept.Clear();
    }

    /// <summary>One command's preview for one actor at one revision.</summary>
    private sealed class Entry(Command command, Actor actor, PreviewAnswer.Pending pending)
    {
        public Command Command { get; } = command;

        public Actor Actor { get; } = actor;

        /// <summary>The pending answer, kept so a repeat ask allocates nothing.</summary>
        public PreviewAnswer.Pending Pending { get; } = pending;

        public PreviewAnswer? Answer { get; set; }

        public GhostBoard.Trial? Trial { get; set; }

        public ValueList<Ghost> Ghosts { get; set; } = ValueList<Ghost>.Empty;

        public ValidationTrial? Checking { get; set; }

        public SlicedJob? Job { get; set; }

        public bool Done { get; set; }

        public ValueList<SafetyWarning> Warnings { get; set; } = ValueList<SafetyWarning>.Empty;

        public AnalysisReport? Effects { get; set; }

        public Bill? Bill { get; set; }

        public Option<RunReport> Run { get; set; }

        public ValueList<RunReport> Runs { get; set; } = ValueList<RunReport>.Empty;
    }

    /// <summary>The preview's main-thread work: one step in the checking budget, skipped when the board has moved since.</summary>
    private sealed class PreviewWork(PreviewService service, Entry entry) : ISlicedWork
    {
        public StepOutcome Step()
        {
            if (!entry.Done && entry.Pending.At == service.Revision)
                service.Work(entry);
            return StepOutcome.Done;
        }
    }
}
