using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Ports;
using Lattice.Core.Safety;
using Lattice.Core.Support;
using Lattice.Core.Validation;

namespace Lattice.Core.Analysis;

/// <summary>What analysis is built from, for one world, on either seat.</summary>
/// <param name="Board">The board (the host's, or a client's replica).</param>
/// <param name="Viewer">The actor this process's reads are for.</param>
/// <param name="World">The world view.</param>
/// <param name="Mirror">The world mirror, whose revision says when the world moved.</param>
/// <param name="Effects">The effects of ghosts on networks, rooms and gas.</param>
/// <param name="Analyzers">The mods' analyzers.</param>
/// <param name="Validation">Validation, whose worker runs the worker analyzers; none when it is off.</param>
internal sealed record AnalysisParts(
    GhostBoard Board,
    Actor Viewer,
    IWorldView World,
    WorldMirror Mirror,
    PlanEffects Effects,
    Analyzers Analyzers,
    ValidationService? Validation);

/// <summary>
/// What ghosts would do to the world if built (<see cref="IAnalysis"/>): the effects of the ghosts a selector picks on
/// the viewer's board, with only those ghosts done (<see cref="PlanEffects"/>), plus the mods' analyzers' facts in
/// registration order. Main-thread analyzers are called at once with the planned world (every ghost the viewer sees
/// done); worker analyzers are called on Lattice's worker through a validation trial, so their facts arrive a few
/// frames later on their own thread (<see cref="Of"/> is pending until then, and <see cref="IGhostEvents.AnswersReady"/>
/// is raised).
/// Answers are kept per selector until the board or the world moves. Analyzers are plain calls: an exception
/// leaves the call. Main thread. Cost: a lookup for a kept answer; else as <see cref="PlanEffects"/>.
/// </summary>
internal sealed class AnalysisService(AnalysisParts parts) : IAnalysis
{
    private readonly Dictionary<Selector, Entry> _kept = [];
    private (Revision Board, long Mirror) _at;

    /// <summary>The report for the selector, pending until the worker analyzers have answered; refused for a selector the board refuses.</summary>
    public Result<Answer<AnalysisReport>, Reason> Of(Selector which)
    {
        Moved();
        if (!_kept.TryGetValue(which, out var entry))
        {
            var view = parts.Board.ViewFor(parts.Viewer);
            var selected = view.Select(which);
            if (!selected.TryGet(out var ghosts))
                return Result<Answer<AnalysisReport>, Reason>.Error(selected.ErrorOf());
            _kept[which] = entry = Start(ghosts, view);
        }

        if (entry.Answer is null && entry.Trial is { IsReady: true } trial)
            entry.Answer = new Answer<AnalysisReport>.Ready(With(entry.Report, trial.Facts), _at.Board);
        return Result<Answer<AnalysisReport>, Reason>.Ok(entry.Answer ?? new Answer<AnalysisReport>.Pending(_at.Board));
    }

    /// <summary>Ends with the world: stops every worker analysis.</summary>
    public void End() => Drop();

    private Entry Start(ValueList<Ghost> ghosts, IGhostBoard view)
    {
        var facts = new List<AnalyzerFact>();
        if (parts.Analyzers.Any(CallbackThread.Main))
            parts.Analyzers.Facts(CallbackThread.Main, new AnalyzerInput(ghosts, new PlannedWorld(view, new ViewQuery(parts.World))), facts);
        var report = parts.Effects.Of(ghosts, view, parts.World, ValueList.From(facts));
        if (!parts.Analyzers.Any(CallbackThread.Worker) || parts.Validation is not { Faulted: false } validation)
            return new Entry(report) { Answer = new Answer<AnalysisReport>.Ready(report, _at.Board) };
        return new Entry(report) { Trial = validation.Trial(Nothing(), ghosts) };
    }

    // A change that changes nothing, at the revision a trial is taken for: the worker analyzers run on the board as it is.
    private ChangeSet Nothing() => new(
        default, default, parts.Viewer, new Revision(parts.Board.Revision.Value + 1), DateTimeOffset.MinValue,
        ValueList<Ghost>.Empty, ValueList<GhostChange>.Empty, ValueList<GhostRemoval>.Empty,
        ValueList<Plan>.Empty, ValueList<PlanChange>.Empty, ValueList<PlanId>.Empty);

    private static AnalysisReport With(AnalysisReport report, ValueList<AnalyzerFact> worker)
    {
        if (worker.Count == 0)
            return report;
        var facts = new List<AnalyzerFact>(report.Facts.Count + worker.Count);
        foreach (var fact in report.Facts)
            facts.Add(fact);
        foreach (var fact in worker)
            facts.Add(fact);
        return report with { Facts = ValueList.From(facts) };
    }

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
            if (entry.Trial is { } trial)
                parts.Validation?.Drop(trial);
        _kept.Clear();
    }

    private sealed class Entry(AnalysisReport report)
    {
        /// <summary>The effects and the main-thread analyzers' facts.</summary>
        public AnalysisReport Report { get; } = report;

        public ValidationTrial? Trial { get; init; }

        public Answer<AnalysisReport>? Answer { get; set; }
    }
}
