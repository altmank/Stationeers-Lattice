using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Ports;
using Lattice.Core.Support;

namespace Lattice.Core.Validation;

/// <summary>
/// Validation's worker side: the ghosts, the world copy, the facts given, and every judgement made, with what each
/// read. A run takes the changes handed over since the last one and re-checks only the ghosts that read something
/// that changed:
/// <list type="bullet">
/// <item>reads are kept as reader indices (key to the ghosts that read it), so a change finds its readers by lookup,
/// never by visiting every ghost;</item>
/// <item>a ghost whose stage moved is re-checked, and so is any reader of its changes whose order against it flipped
/// (its dependants);</item>
/// <item>new or dropped rules re-check every ghost.</item>
/// </list>
/// Ordering (edges from cached judgements, cycles, stages) is redone every run: it calls no rule. Findings come out
/// in build order, cycles after, stale ghosts last (each with its stale reason). A run is pending while any judgement
/// lacks a main-thread fact; it then lists what to ask. Worker only; not thread-safe.
/// </summary>
internal sealed class ValidationEngine
{
    private readonly ICatalogue _catalogue;
    private readonly Shapes _shapes;
    private readonly SupportJudge _support;
    private readonly ValidationRules _rules;
    private readonly WorldCopy _world;
    private readonly FactStore _store = new();
    private readonly WorkerFacts _facts;
    private readonly OrderRun _order;
    private readonly Dictionary<GhostId, Ghost> _ghosts = [];
    private readonly Dictionary<GhostId, Judged<SupportAnswer>> _edges = [];
    private readonly Dictionary<GhostId, Judged<GhostVerdict>> _stages = [];
    private readonly Readers _edgeReaders = new();
    private readonly Readers _stageReaders = new();
    private Dictionary<GhostId, int> _stageOf = [];

    // A run's working sets, emptied at its start and kept with their room: a run over a large plan fills them with
    // every ghost, and one that re-checks a few ghosts would otherwise allocate them again at full size.
    private readonly HashSet<ReadKey> _changed = [];
    private readonly HashSet<ReadKey> _answers = [];
    private readonly HashSet<GhostId> _direct = [];
    private readonly HashSet<GhostId> _edgeDirty = [];
    private readonly HashSet<GhostId> _stageDirty = [];
    private readonly HashSet<GhostId> _rechecked = [];
    private readonly HashSet<ReadKey> _asked = [];
    private readonly List<ReadKey> _lost = [];
    private HashSet<ReadKey> _orphans = [];
    private HashSet<ReadKey> _olderOrphans = [];
    private readonly List<Ask> _asks = [];
    private readonly List<Ghost> _sorted = [];
    private Dictionary<GhostId, int> _spareStageOf = [];
    private Dictionary<GhostId, Finding> _verdicts = [];
    private ValueList<Finding> _findings = ValueList<Finding>.Empty;
    private Revision _revision;
    private AssumedIndex? _staged;
    private bool _trying;
    private Ordering? _lastOrdering;
    private OrderReport? _lastReport;
    private ReadLog? _spareLog;
    private long _sequence;

    public ValidationEngine(ICatalogue catalogue, IGrid grid, SupportJudge support, ValidationRules rules, MirrorSnapshot start)
    {
        _catalogue = catalogue;
        _shapes = new Shapes(catalogue, grid);
        _support = support;
        _rules = rules;
        _world = new WorldCopy(start, _shapes);
        _facts = new WorkerFacts(_world, _store);
        _order = new OrderRun(catalogue, grid, _facts);
    }

    /// <summary>Takes in the changes handed over and re-checks what they touch.</summary>
    public RunOutcome Run(IReadOnlyList<WorkInput> inputs)
    {
        var sequence = ++_sequence;
        var (changed, answers, direct) = (_changed, _answers, _direct);
        changed.Clear();
        answers.Clear();
        direct.Clear();
        var everything = false;
        foreach (var input in inputs)
            Take(input, changed, answers, direct, ref everything);

        var visited = 0;
        var (edgeDirty, stageDirty) = (_edgeDirty, _stageDirty);
        edgeDirty.Clear();
        edgeDirty.UnionWith(direct);
        stageDirty.Clear();
        stageDirty.UnionWith(direct);
        if (everything)
            foreach (var id in _ghosts.Keys)
            {
                edgeDirty.Add(id);
                stageDirty.Add(id);
            }

        visited += _edgeReaders.Collect(changed, edgeDirty);
        visited += _stageReaders.Collect(changed, stageDirty);
        foreach (var id in edgeDirty)
            _store.Renew(id);
        foreach (var id in stageDirty)
            if (!edgeDirty.Contains(id))
                _store.Renew(id);
        visited += _edgeReaders.Collect(answers, edgeDirty);
        visited += _stageReaders.Collect(answers, stageDirty);

        _rechecked.Clear();
        var judge = new CachingJudge(this, edgeDirty, stageDirty, _rechecked);
        var ghosts = Sorted();
        var ordering = _order.Order(ghosts, judge);
        _staged = ordering.Staged;
        var stageOf = _spareStageOf;
        stageOf.Clear();
        foreach (var item in ordering.Items)
            stageOf[item.Ghost.Id] = item.Stage;
        visited += Moved(stageOf, stageDirty);
        (_stageOf, _spareStageOf) = (stageOf, _stageOf);

        var report = _order.Report(ordering, judge, out var complete);
        (_lastOrdering, _lastReport) = (ordering, report);
        var asks = _asks;
        Asks(ordering, asks);
        var counts = new RunCounts(_ghosts.Count, judge.Judged, visited, ValueSet.From(judge.Rechecked));
        var unread = Unread(changed);
        if (!complete || asks.Count > 0)
        {
            var main = false;
            foreach (var ask in asks)
                main |= ask is Ask.MainSupport or Ask.MainRule;
            return new RunOutcome.Pending(sequence, _revision, ValueList.From(asks), main, counts) { Unread = unread };
        }

        return Ready(sequence, report, counts) with { Unread = unread };
    }

    /// <summary>
    /// Validates a change that is not on the board: runs with the change's ghosts in (upserts and gone ids, as a board
    /// change), keeps the findings of its added and changed ghosts, then puts the board's ghosts back and runs again,
    /// so the ghosts near the change are judged as before. Both runs re-check only what the change touches. The
    /// second run's outcome is the engine's own, as after any run. Game, room and network answers the trial needed
    /// stay for the next trial nearby; main-thread rules' answers for its ghosts hold for this trial only.
    /// The trial is judged on its own ghosts (<see cref="TrialOf"/>): ready once they, and the ghosts whose changes
    /// they read, are judged in full, whatever the rest of the board still waits on.
    /// </summary>
    public (TrialOutcome Trial, RunOutcome After, TrialWorld World) Trial(ChangeSet changes)
    {
        var upserts = new List<Ghost>(changes.Added.Count + changes.Changed.Count);
        var gone = new List<GhostId>(changes.Removed.Count);
        var back = new List<Ghost>(changes.Changed.Count + changes.Removed.Count);
        var added = new List<GhostId>(changes.Added.Count);
        var tried = new HashSet<GhostId>();
        foreach (var ghost in changes.Added)
        {
            upserts.Add(ghost);
            added.Add(ghost.Id);
            tried.Add(ghost.Id);
        }

        foreach (var change in changes.Changed)
        {
            upserts.Add(change.After);
            back.Add(change.Before);
            tried.Add(change.After.Id);
        }

        foreach (var removal in changes.Removed)
        {
            gone.Add(removal.Ghost.Id);
            back.Add(removal.Ghost);
        }

        var (revision, verdicts, findings) = (_revision, _verdicts, _findings);
        TrialOutcome outcome;
        AssumedIndex? staged;
        RunOutcome after;
        _store.BeginTrial(changes);
        _trying = true;
        try
        {
            try
            {
                Run([new WorkInput.Ghosts(changes.Revision, ValueList.From(upserts), ValueList.From(gone), Reset: false)]);
                staged = _staged;
                outcome = TrialOf(tried, _lastOrdering!, _lastReport!);
            }
            finally
            {
                _store.EndTrial();
                (_verdicts, _findings) = (verdicts, findings);
            }

            after = Run([new WorkInput.Ghosts(revision, ValueList.From(back), ValueList.From(added), Reset: false)]);
        }
        finally
        {
            _trying = false;
        }

        return (outcome, after, new TrialWorld(staged));
    }

    /// <summary>
    /// The trial's world with every ghost done, frozen for the worker analyzers: the trial's staged ghosts (never
    /// changed after the trial) over a copy of the world copy and the room and network answers, so a later run does
    /// not move it and it may be read on any thread. None when the trial ran no ordering. Linear in the world changes
    /// taken in since start and the answers held.
    /// </summary>
    public IWorldQuery? AnalyzerWorld(TrialWorld world) =>
        world.Index is null ? null : new SupportWorld(world.Index, AssumedIndex.Every, true, _store.Snapshot(_world), _catalogue, new ReadLog());

    /// <summary>
    /// The facts of the mods' worker analyzers for the ghosts on a frozen world (<see cref="AnalyzerWorld"/>), as plain
    /// calls: an analyzer's exception leaves this call. Any thread.
    /// </summary>
    public static ValueList<AnalyzerFact> Analyze(Analysis.Analyzers analyzers, IWorldQuery? world, ValueList<Ghost> ghosts)
    {
        if (world is null || !analyzers.Any(CallbackThread.Worker))
            return ValueList<AnalyzerFact>.Empty;
        var facts = new List<AnalyzerFact>();
        analyzers.Facts(CallbackThread.Worker, new AnalyzerInput(ghosts, world), facts);
        return ValueList.From(facts);
    }

    // The trial's outcome from its own ghosts, right after the run with them in. Pending with the asks that hold them up:
    // their own, ordering's, and those of every ghost not judged in full whose changes they read (it writes a key their
    // judgements read). Else ready with their findings as a ready run gives them: in build order, each ghost's together,
    // a stale ghost's last. The rest of the board's asks are not the trial's. Linear in the board's ghosts.
    private TrialOutcome TrialOf(HashSet<GhostId> tried, Ordering ordering, OrderReport report)
    {
        var asks = new List<Ask>();
        var asked = new HashSet<ReadKey>();
        var read = new HashSet<ReadKey>();
        foreach (var ask in ordering.Reads.Asks)
            if (asked.Add(ask.Key))
                asks.Add(ask);
        foreach (var id in tried)
        {
            if (_edges.TryGetValue(id, out var edge))
                Held(edge.Keys, edge.Asks);
            if (_stages.TryGetValue(id, out var stage))
                Held(stage.Keys, stage.Asks);
        }

        var writes = new List<ReadKey>(8);
        foreach (var pair in _ghosts)
        {
            if (tried.Contains(pair.Key))
                continue;
            var edgeAsks = _edges.TryGetValue(pair.Key, out var edge) ? edge.Asks : [];
            var stageAsks = _stages.TryGetValue(pair.Key, out var stage) ? stage.Asks : [];
            if (edgeAsks.Length == 0 && stageAsks.Length == 0)
                continue;
            writes.Clear();
            Writes(pair.Value, writes);
            if (!read.Overlaps(writes))
                continue;
            foreach (var ask in edgeAsks)
                if (asked.Add(ask.Key))
                    asks.Add(ask);
            foreach (var ask in stageAsks)
                if (asked.Add(ask.Key))
                    asks.Add(ask);
        }

        if (asks.Count > 0)
        {
            var main = false;
            foreach (var ask in asks)
                main |= ask is Ask.MainSupport or Ask.MainRule;
            return new TrialOutcome.Pending(ValueList.From(asks), main);
        }

        var order = new List<GhostId>(tried.Count);
        var groups = new Dictionary<GhostId, List<Finding>>(tried.Count);
        foreach (var finding in report.Findings)
            Group(finding);
        var stale = new List<Ghost>();
        foreach (var id in tried)
            if (_ghosts.TryGetValue(id, out var ghost) && ghost.State is GhostState.Stale)
                stale.Add(ghost);
        stale.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        foreach (var ghost in stale)
            Group(new Finding.WillFail(ghost.Id, ((GhostState.Stale)ghost.State).Why));
        var findings = new List<Finding>();
        foreach (var id in order)
            findings.AddRange(groups[id]);
        return new TrialOutcome.Ready(ValueList.From(findings));

        void Held(ReadKey[] keys, Ask[] held)
        {
            read.UnionWith(keys);
            foreach (var ask in held)
                if (asked.Add(ask.Key))
                    asks.Add(ask);
        }

        void Group(Finding finding)
        {
            if (!tried.Contains(finding.Ghost))
                return;
            if (!groups.TryGetValue(finding.Ghost, out var group))
            {
                groups.Add(finding.Ghost, group = new List<Finding>(2));
                order.Add(finding.Ghost);
            }

            group.Add(finding);
        }
    }

    private void Take(WorkInput input, HashSet<ReadKey> changed, HashSet<ReadKey> answers, HashSet<GhostId> direct, ref bool everything)
    {
        switch (input)
        {
            case WorkInput.Ghosts ghosts:
                if (ghosts.Reset)
                    Reset(changed);
                foreach (var gone in ghosts.Gone)
                    if (_ghosts.TryGetValue(gone, out var old))
                    {
                        Writes(old, changed);
                        Forget(gone);
                        direct.Remove(gone);
                    }

                foreach (var ghost in ghosts.Upserts)
                {
                    if (_ghosts.TryGetValue(ghost.Id, out var before))
                        Writes(before, changed);
                    Writes(ghost, changed);
                    Forget(ghost.Id);
                    _ghosts[ghost.Id] = ghost;
                    direct.Add(ghost.Id);
                }

                _revision = ghosts.At;
                break;
            case WorkInput.World world:
                foreach (var change in world.Changes)
                {
                    foreach (var cell in _world.Apply(change))
                        changed.Add(ReadKey.At(cell));
                    changed.Add(ReadKey.StructureOf(IdOf(change)));
                }

                break;
            case WorkInput.Dropped dropped:
                foreach (var gone in dropped.Keys)
                    if (_store.Drop(gone))
                        changed.Add(gone);
                break;
            case WorkInput.RulesChanged:
                everything = true;
                break;
            case WorkInput.MainAnswered:
                if (_store.Take(input).TryGet(out var answered))
                    answers.Add(answered);
                break;
            case WorkInput.GameAnswers games:
                foreach (var (at, answer) in games.Answers)
                    if (_store.TakeGame(at, answer).TryGet(out var game))
                        changed.Add(game);
                break;
            default:
                if (_store.Take(input).TryGet(out var key))
                    changed.Add(key);
                break;
        }
    }

    private static ReferenceId IdOf(MirrorChange change) => change switch
    {
        MirrorChange.Appeared appeared => appeared.Now.Ref.Id,
        MirrorChange.Altered altered => altered.Now.Ref.Id,
        MirrorChange.Gone gone => gone.Before.Ref.Id,
        _ => default,
    };

    private void Reset(HashSet<ReadKey> changed)
    {
        foreach (var ghost in _ghosts.Values)
            Writes(ghost, changed);
        foreach (var id in new List<GhostId>(_ghosts.Keys))
            Forget(id);
        _ghosts.Clear();
    }

    // A trial's ghosts leave the judgements when it ends, but the facts it read stay for the next trial nearby.
    private List<ReadKey>? Lost => _trying ? null : _lost;

    /// <summary>
    /// The main-thread facts no ghost reads any longer: every ghost that read one left the board or now reads others.
    /// A fact the store holds is dropped from it. One asked and not answered yet is an orphan for two more runs: a
    /// run's asks are passed over once the next run's outcome is taken, so an answer given for it reaches the worker
    /// by then, and is dropped if no ghost reads it. The main thread forgets them all, so the reconciler asks again
    /// only what the board reads. Linear in the facts lost and the run's changes; nothing in a trial.
    /// </summary>
    private ValueList<ReadKey> Unread(HashSet<ReadKey> changed)
    {
        if (_trying || (_lost.Count == 0 && _orphans.Count == 0 && _olderOrphans.Count == 0))
            return ValueList<ReadKey>.Empty;
        List<ReadKey>? unread = null;
        if (_orphans.Count > 0 || _olderOrphans.Count > 0)
            foreach (var key in changed)
                if ((_orphans.Remove(key) | _olderOrphans.Remove(key)) && !Read(key) && _store.Drop(key))
                    (unread ??= new List<ReadKey>()).Add(key);

        (_orphans, _olderOrphans) = (_olderOrphans, _orphans);
        _orphans.Clear();
        foreach (var key in _lost)
        {
            if (Read(key))
                continue;
            if (_store.Drop(key))
                (unread ??= new List<ReadKey>(_lost.Count)).Add(key);
            else
                _orphans.Add(key);
        }

        _lost.Clear();
        return unread is null ? ValueList<ReadKey>.Empty : ValueList.From(unread);
    }

    private bool Read(ReadKey key) => _edgeReaders.Has(key) || _stageReaders.Has(key);

    private void Forget(GhostId id)
    {
        _ghosts.Remove(id);
        if (_edges.TryGetValue(id, out var edge))
            _edgeReaders.Remove(id, edge.Keys, Lost);
        if (_stages.TryGetValue(id, out var stage))
            _stageReaders.Remove(id, stage.Keys, Lost);
        _edges.Remove(id);
        _stages.Remove(id);
        _store.Forget(id);
    }

    // What a ghost changes in the views others read: its placement's cell and assumed id, the structure it takes, and
    // the buckets near-change questions read.
    private void Writes(Ghost ghost, ICollection<ReadKey> into)
    {
        switch (ghost.Intent)
        {
            case Intent.Build build:
                WritesPut(ghost.Id, build.Target, into);
                break;
            case Intent.Swap swap:
                WritesPut(ghost.Id, swap.New, into);
                WritesTake(swap.Old, into);
                break;
            case Intent.Remove remove:
                WritesTake(remove.Target, into);
                break;
        }
    }

    private static void WritesPut(GhostId ghost, Placement put, ICollection<ReadKey> into)
    {
        into.Add(ReadKey.At(put.At));
        into.Add(ReadKey.StructureOf(SupportWorld.IdOf(ghost)));
        into.Add(ReadKey.Fine(ReadKey.FineBucketOf(put.At)));
        into.Add(ReadKey.Coarse(ReadKey.CoarseBucketOf(put.At)));
    }

    private void WritesTake(StructureRef target, ICollection<ReadKey> into)
    {
        into.Add(ReadKey.At(target.At));
        foreach (var cell in _world.CellsOf(target.Id))
            into.Add(ReadKey.At(cell));
        into.Add(ReadKey.StructureOf(target.Id));
        into.Add(ReadKey.Fine(ReadKey.FineBucketOf(target.At)));
        into.Add(ReadKey.Coarse(ReadKey.CoarseBucketOf(target.At)));
    }

    // A ghost whose stage moved is re-checked; so is every reader of its changes whose order against it flipped.
    private int Moved(Dictionary<GhostId, int> now, HashSet<GhostId> dirty)
    {
        var visited = 0;
        var keys = new List<ReadKey>(8);
        var readers = new List<GhostId>(8);
        foreach (var pair in now)
        {
            var id = pair.Key;
            if (_stageOf.TryGetValue(id, out var was) && was == pair.Value)
                continue;
            Dirty(id, dirty);
            keys.Clear();
            Writes(_ghosts[id], keys);
            foreach (var key in keys)
            {
                readers.Clear();
                _stageReaders.Of(key, readers);
                foreach (var reader in readers)
                {
                    visited++;
                    if (reader != id && Relation(_stageOf, id, reader) != Relation(now, id, reader))
                        Dirty(reader, dirty);
                }
            }
        }

        return visited;
    }

    private void Dirty(GhostId id, HashSet<GhostId> dirty)
    {
        if (dirty.Add(id))
            _store.Renew(id);
    }

    private static int Relation(Dictionary<GhostId, int> stages, GhostId writer, GhostId reader) =>
        stages.TryGetValue(writer, out var w) && stages.TryGetValue(reader, out var r) && w >= 0 && r >= 0
            ? Math.Sign(w - r)
            : 2;

    private List<Ghost> Sorted()
    {
        var ghosts = _sorted;
        ghosts.Clear();
        foreach (var ghost in _ghosts.Values)
            ghosts.Add(ghost);
        ghosts.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        return ghosts;
    }

    private void Asks(Ordering ordering, List<Ask> asks)
    {
        asks.Clear();
        var seen = _asked;
        seen.Clear();
        foreach (var ask in ordering.Reads.Asks)
            if (seen.Add(ask.Key))
                asks.Add(ask);
        foreach (var judged in _edges.Values)
            foreach (var ask in judged.Asks)
                if (seen.Add(ask.Key))
                    asks.Add(ask);
        foreach (var judged in _stages.Values)
            foreach (var ask in judged.Asks)
                if (seen.Add(ask.Key))
                    asks.Add(ask);
    }

    private RunOutcome Ready(long sequence, OrderReport report, RunCounts counts)
    {
        var findings = new List<Finding>(report.Findings.Count + 8);
        foreach (var finding in report.Findings)
            findings.Add(finding);
        var stale = new List<Ghost>();
        foreach (var ghost in _ghosts.Values)
            if (ghost.State is GhostState.Stale)
                stale.Add(ghost);
        stale.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        foreach (var ghost in stale)
            findings.Add(new Finding.WillFail(ghost.Id, ((GhostState.Stale)ghost.State).Why));

        var places = new Dictionary<GhostId, (int Start, int Count, int Order)>(_ghosts.Count);
        var verdicts = new Dictionary<GhostId, Finding>(_ghosts.Count);
        for (var i = 0; i < findings.Count; i++)
        {
            var id = findings[i].Ghost;
            if (places.TryGetValue(id, out var place))
                places[id] = (place.Start, place.Count + 1, place.Order);
            else
                places[id] = (i, 1, places.Count);
            if (findings[i] is not Finding.Warns && !verdicts.ContainsKey(id))
                verdicts[id] = findings[i];
        }

        var all = ValueList.From(findings);
        var changes = new List<VerdictChange>();
        foreach (var pair in verdicts)
        {
            var had = _verdicts.TryGetValue(pair.Key, out var before);
            if (!had || before != pair.Value)
                changes.Add(new VerdictChange(pair.Key, had ? Option.Some(before!) : Option<Finding>.None, Option.Some(pair.Value)));
        }

        foreach (var pair in _verdicts)
            if (!verdicts.ContainsKey(pair.Key))
                changes.Add(new VerdictChange(pair.Key, Option.Some(pair.Value), Option<Finding>.None));
        var differs = !all.Equals(_findings);
        _verdicts = verdicts;
        _findings = all;
        return new RunOutcome.Ready(sequence, new ValidationResult(_revision, all, places, report.PipeJoins), differs, ValueList.From(changes), counts);
    }

    private SupportAnswer JudgeEdge(OrderItem item, Placement put, PieceFacts piece, AssumedIndex all)
    {
        var log = TakeLog();
        var world = new SupportWorld(all, AssumedIndex.Every, false, _facts, _catalogue, log);
        var answer = _support.Judge(new SupportCase(item.Ghost.Id, put, piece, world), report: false);
        Keep(_edges, _edgeReaders, item.Ghost.Id, answer, log, Lost);
        Return(log);
        return answer;
    }

    private GhostVerdict JudgeStage(StageCase @case)
    {
        var item = @case.Item;
        var ghost = item.Ghost;
        var log = TakeLog();
        var world = new SupportWorld(@case.Staged, @case.Stage, true, _facts, _catalogue, log);
        var fails = new List<Reason>();
        var warnings = new List<Reason>();
        var support = Option<SupportAnswer>.None;
        if (item.Put.TryGet(out var put) && item.Facts.TryGet(out var piece))
        {
            var answer = _support.Judge(new SupportCase(ghost.Id, put, piece, world));
            support = Option.Some(answer);
            if (!answer.Replaced && answer.Outcome is not SupportOutcome.Fails)
                BuiltWorldCheck(ghost.Id, put, piece, world, fails);
        }

        RunRules(ghost, world, fails, warnings);
        var verdict = new GhostVerdict(support, ValueList.From(fails), ValueList.From(warnings), log.Complete);
        if (log.Complete)
        {
            _support.Rules.Guard.Settle(ghost.Id);
            _rules.Guard.Settle(ghost.Id);
        }

        Keep(_stages, _stageReaders, ghost.Id, verdict, log, Lost);
        Return(log);
        return verdict;
    }

    // A judgement's log is copied into its cache entry, so the next judgement reuses it and its room (a main-thread
    // rule's ask carries a view with a log of its own).
    private ReadLog TakeLog()
    {
        var log = _spareLog ?? new ReadLog();
        _spareLog = null;
        return log;
    }

    private void Return(ReadLog log)
    {
        log.Clear();
        _spareLog = log;
    }

    // The game's own check against the built world: asked where nothing planned at or before this
    // ghost's stage changes its neighbourhood, so the game sees what the ghost will be built into. The neighbourhood
    // is the footprint's cells and one cell around on every axis, a heuristic, not a game rule.
    private void BuiltWorldCheck(GhostId ghost, Placement put, PieceFacts piece, SupportWorld world, List<Reason> fails)
    {
        var step = piece.Grid == GridSize.Large ? 20 : 5;
        var (low, high) = (put.At, put.At);
        if (_shapes.Of(put).TryGet(out var shape))
            foreach (var spot in shape.Spots)
            {
                var cell = spot.Cell;
                low = new Cell(Math.Min(low.X, cell.X), Math.Min(low.Y, cell.Y), Math.Min(low.Z, cell.Z));
                high = new Cell(Math.Max(high.X, cell.X), Math.Max(high.Y, cell.Y), Math.Max(high.Z, cell.Z));
            }

        low = new Cell(low.X - step, low.Y - step, low.Z - step);
        high = new Cell(high.X + step, high.Y + step, high.Z + step);
        if (world.AnyChangeIn(low, high, ghost, sameStage: true))
            return;
        if (world.Game(put).TryGet(out var answer) && answer is GameAnswer.Refuses refuses)
            fails.Add(new Reason(ReasonCodes.GameRefused, ValueMap<string, string>.Of("check", "built-world"), refuses.Message, ValidationCodes.Validation));
    }

    private void RunRules(Ghost ghost, SupportWorld world, List<Reason> fails, List<Reason> warnings)
    {
        foreach (var entry in _rules.Current)
        {
            if (!entry.AppliesTo(ghost))
                continue;
            Option<Guarded<RuleVerdict>> answer;
            if (entry.Inline)
                answer = Option.Some<Guarded<RuleVerdict>>(new Guarded<RuleVerdict>.Answered(entry.Rule.Check(new RuleInput(ghost, world))));
            else if (entry.Rule.Thread == CallbackThread.Main)
                answer = world.MainRule(_rules, entry, new RuleInput(ghost, world));
            else
            {
                var call = world.Fork();
                var guarded = _rules.Check(entry, new RuleInput(ghost, call));
                if (guarded is Guarded<RuleVerdict>.Answered)
                    world.Log.Merge(call.Log);
                answer = Option.Some(guarded);
            }

            if (answer.TryGet(out var verdict))
                Verdict(entry, ghost.Id, verdict, fails, warnings);
        }
    }

    private void Verdict(ValidationRules.Entry entry, GhostId ghost, Guarded<RuleVerdict> verdict, List<Reason> fails, List<Reason> warnings)
    {
        switch (verdict)
        {
            case Guarded<RuleVerdict>.Answered { Value: RuleVerdict.Fail fail }:
                fails.Add(fail.Why);
                break;
            case Guarded<RuleVerdict>.Answered { Value: RuleVerdict.Warn warn }:
                warnings.Add(warn.Why);
                break;
            case Guarded<RuleVerdict>.Answered { Value: RuleVerdict.Pass }:
                break;
            case Guarded<RuleVerdict>.TimedOut:
                warnings.Add(Reason.Of(ReasonCodes.RuleTimedOut, entry.Rule.Id));
                break;
            case Guarded<RuleVerdict>.SwitchedOff:
                if (_rules.ReportSwitchOff(entry, ghost))
                    warnings.Add(Reason.Of(ReasonCodes.RuleSwitchedOff, entry.Rule.Id));
                break;
            default:
                warnings.Add(Reason.Of(ReasonCodes.UnknownKind, entry.Rule.Id));
                break;
        }
    }

    // A re-judged ghost's earlier entry gives its arrays to the new one when they are the right size, as they mostly
    // are (a ghost judged again reads the same things), so a re-check copies its reads without allocating.
    private static void Keep<T>(Dictionary<GhostId, Judged<T>> cache, Readers readers, GhostId id, T value, ReadLog log, List<ReadKey>? lost)
    {
        var had = cache.TryGetValue(id, out var old);
        if (had)
            readers.Remove(id, old!.Keys, lost);
        var judged = new Judged<T>(value, log, had ? old : null);
        cache[id] = judged;
        readers.Add(id, judged.Keys);
    }

    /// <summary>A judgement and what it read and asked for; <c>earlier</c> is the ghost's entry it replaces, if any.</summary>
    private sealed class Judged<T>(T value, ReadLog log, Judged<T>? earlier)
    {
        public T Value { get; } = value;

        public ReadKey[] Keys { get; } = Copy(log.Keys, earlier?.Keys);

        public Ask[] Asks { get; } = Copy(log.Asks, earlier?.Asks);

        private static TItem[] Copy<TItem>(IReadOnlyCollection<TItem> items, TItem[]? room)
        {
            if (items.Count == 0)
                return [];
            var copy = room is not null && room.Length == items.Count ? room : new TItem[items.Count];
            var i = 0;
            foreach (var item in items)
                copy[i++] = item;
            return copy;
        }
    }

    private static bool IsMainFact(ReadKey key) => key.Kind is ReadKind.Game or ReadKind.Room or ReadKind.Network;

    /// <summary>
    /// Key to the ghosts whose judgement read it. This index is most of what validation keeps per ghost, and all of it
    /// is new when a plan lands, so it is kept small and of few objects: a key's readers are a chain of nodes in two
    /// arrays shared by every key (a ghost id and the next node, 12 bytes a reader, freed nodes reused), searched in
    /// order while they are few (a lookup costs less than hashing then), and a set past <see cref="Most"/>.
    /// </summary>
    private sealed class Readers
    {
        private const int Most = 16;
        private const int End = -1;

        private readonly Dictionary<ReadKey, int> _first = [];
        private readonly Dictionary<ReadKey, HashSet<GhostId>> _lots = [];
        private GhostId[] _ghost = new GhostId[256];
        private int[] _next = new int[256];
        private int _used;
        private int _free = End;

        public void Add(GhostId id, ReadKey[] keys)
        {
            foreach (var key in keys)
            {
                if (_lots.TryGetValue(key, out var lots))
                {
                    lots.Add(id);
                    continue;
                }

                if (!_first.TryGetValue(key, out var first))
                {
                    _first.Add(key, Node(id, End));
                    continue;
                }

                var count = 0;
                var found = false;
                for (var node = first; node != End && !found; node = _next[node])
                {
                    found = _ghost[node] == id;
                    count++;
                }

                if (found)
                    continue;
                if (count < Most)
                {
                    _first[key] = Node(id, first);
                    continue;
                }

                lots = new HashSet<GhostId> { id };
                for (var node = first; node != End;)
                {
                    lots.Add(_ghost[node]);
                    var next = _next[node];
                    Free(node);
                    node = next;
                }

                _first.Remove(key);
                _lots.Add(key, lots);
            }
        }

        public bool Has(ReadKey key) => _first.ContainsKey(key) || _lots.ContainsKey(key);

        /// <summary>Removes the ghost as a reader of the keys; a main-thread fact left with no reader goes to <paramref name="lost"/>.</summary>
        public void Remove(GhostId id, ReadKey[] keys, List<ReadKey>? lost)
        {
            foreach (var key in keys)
            {
                if (_first.TryGetValue(key, out var first))
                {
                    var before = End;
                    var node = first;
                    while (node != End && _ghost[node] != id)
                        (before, node) = (node, _next[node]);
                    if (node == End)
                        continue;
                    var next = _next[node];
                    Free(node);
                    if (before != End)
                        _next[before] = next;
                    else if (next == End)
                    {
                        _first.Remove(key);
                        if (lost is not null && IsMainFact(key))
                            lost.Add(key);
                    }
                    else
                        _first[key] = next;
                }
                else if (_lots.TryGetValue(key, out var lots) && lots.Remove(id) && lots.Count == 0)
                {
                    _lots.Remove(key);
                    if (lost is not null && IsMainFact(key))
                        lost.Add(key);
                }
            }
        }

        /// <summary>Adds the key's readers to the list.</summary>
        public void Of(ReadKey key, List<GhostId> into)
        {
            if (_first.TryGetValue(key, out var first))
                for (var node = first; node != End; node = _next[node])
                    into.Add(_ghost[node]);
            else if (_lots.TryGetValue(key, out var lots))
                foreach (var id in lots)
                    into.Add(id);
        }

        /// <summary>Adds the readers of every key; gives how many reader entries it visited.</summary>
        public int Collect(HashSet<ReadKey> keys, HashSet<GhostId> into)
        {
            var visited = 0;
            foreach (var key in keys)
                if (_first.TryGetValue(key, out var first))
                    for (var node = first; node != End; node = _next[node])
                    {
                        visited++;
                        into.Add(_ghost[node]);
                    }
                else if (_lots.TryGetValue(key, out var lots))
                {
                    foreach (var id in lots)
                        into.Add(id);
                    visited += lots.Count;
                }

            return visited;
        }

        private int Node(GhostId id, int next)
        {
            int node;
            if (_free != End)
            {
                node = _free;
                _free = _next[node];
            }
            else
            {
                if (_used == _ghost.Length)
                {
                    Array.Resize(ref _ghost, 2 * _ghost.Length);
                    Array.Resize(ref _next, 2 * _next.Length);
                }

                node = _used++;
            }

            _ghost[node] = id;
            _next[node] = next;
            return node;
        }

        private void Free(int node)
        {
            _next[node] = _free;
            _free = node;
        }
    }

    /// <summary>Answers from the cache unless the ghost is dirty; counts the judgements it makes.</summary>
    private sealed class CachingJudge(ValidationEngine engine, HashSet<GhostId> edgeDirty, HashSet<GhostId> stageDirty, HashSet<GhostId> rechecked) : IGhostJudge
    {
        public int Judged { get; private set; }

        public HashSet<GhostId> Rechecked => rechecked;

        public SupportAnswer Edge(OrderItem item, Placement put, PieceFacts piece, AssumedIndex all)
        {
            var id = item.Ghost.Id;
            if (!edgeDirty.Contains(id) && engine._edges.TryGetValue(id, out var cached))
                return cached.Value;
            Judged++;
            Rechecked.Add(id);
            return engine.JudgeEdge(item, put, piece, all);
        }

        public GhostVerdict Stage(StageCase @case)
        {
            var id = @case.Item.Ghost.Id;
            if (!stageDirty.Contains(id) && engine._stages.TryGetValue(id, out var cached))
                return cached.Value;
            Judged++;
            Rechecked.Add(id);
            return engine.JudgeStage(@case);
        }
    }
}
