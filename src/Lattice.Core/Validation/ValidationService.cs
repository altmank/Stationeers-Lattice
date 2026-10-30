using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Ports;
using Lattice.Core.Slices;
using Lattice.Core.Support;

namespace Lattice.Core.Validation;

/// <summary>What validation is built from, for one world.</summary>
/// <param name="Board">The board.</param>
/// <param name="Viewer">The actor this process's reads are for.</param>
/// <param name="Mirror">The world mirror, started.</param>
/// <param name="World">The live world, for the rooms and networks only the main thread can read.</param>
/// <param name="Placement">The game's placement check (main thread).</param>
/// <param name="Catalogue">The catalogue.</param>
/// <param name="Grid">The grid.</param>
/// <param name="Support">Support's judge, with the mods' support rules.</param>
/// <param name="Rules">The mods' validation rules.</param>
/// <param name="Scheduler">The frame scheduler the main-thread work runs in.</param>
/// <param name="Clock">The clock the reconciler's sweeps are timed on.</param>
/// <param name="Worker">Lattice's worker.</param>
internal sealed record ValidationParts(
    GhostBoard Board,
    Actor Viewer,
    WorldMirror Mirror,
    IWorldView World,
    IPlacementCheck Placement,
    ICatalogue Catalogue,
    IGrid Grid,
    SupportJudge Support,
    ValidationRules Rules,
    FrameScheduler Scheduler,
    IClock Clock,
    IValidationWorker Worker)
{
    /// <summary>How often the reconciler asks every held main-thread fact again.</summary>
    public TimeSpan SweepEvery { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The most of a frame a sweep spends (plus the one fact it was asking when the time ran out): a sweep is background
    /// work, so a frame with nothing else to do stays near free instead of filling the checking budget.
    /// </summary>
    public TimeSpan SweepSlice { get; init; } = DefaultSweepSlice;

    /// <summary>
    /// A quarter of a millisecond, in ticks: on the .NET Framework base class library the game's Mono runs,
    /// <see cref="TimeSpan.FromMilliseconds"/> rounds to whole milliseconds, which makes 0.25 zero.
    /// </summary>
    public static TimeSpan DefaultSweepSlice => TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond / 4);

    /// <summary>How long ending the world waits for Lattice's worker.</summary>
    public TimeSpan Join { get; init; } = ThreadWorker.DefaultJoin;

    /// <summary>The mods' analyzers; the worker ones are called for trials.</summary>
    public Analysis.Analyzers Analyzers { get; init; } = new();

    /// <summary>
    /// The thread the worker analyzers run on, never Lattice's validation worker, so one that never returns leaves
    /// only the answers waiting for it pending (the world's entry point gives a <see cref="ThreadWorker"/> of its own).
    /// None: they run on the validation worker right after the trial.
    /// </summary>
    public IValidationWorker? AnalyzerWorker { get; init; }
}

/// <summary>
/// Validation for one world (<see cref="IValidation"/>): findings per ghost in build order, on the world plus the
/// ghosts before each assumed built, kept true as the board and the world change.
/// <para>
/// Main thread: board change sets and world mirror changes are handed to the worker as they come (constant per change;
/// no scan, no snapshot per change); <see cref="Frame"/>, once per frame outside the scheduler's slices, takes the
/// worker's results and starts the next run; the facts the worker asked for (the game's placement check, rooms,
/// networks, mods' main-thread rules) are answered in the checking budget's slices; the reconciler re-asks every held
/// fact every <see cref="ValidationParts.SweepEvery"/>, for what no signal reports, at most <see cref="ValidationParts.SweepSlice"/> a frame.
/// </para>
/// <para>
/// Worker: <see cref="ValidationEngine"/>, one run at a time. A report for the board's revision is served until the
/// next one replaces it, so after a world change it trails the world by one run; a builder confirms with the game's
/// check right before it places anything (the server's build path checks nothing). A fault on the worker turns
/// validation off for the world: <see cref="Frame"/> throws it once and every later check is refused.
/// </para>
/// <para>
/// Measured on .NET 8 (10,000 ghosts each asking the game, a fake placement check; <c>ScaleTests</c>): the worst
/// main-thread frame 2.1 to 2.5 ms against the 2 ms checking budget, and 0.4 to 0.55 s of worker time for the whole
/// check (two runs: asks, then ready). The game's own check costs 1.2 to 2.1 us a call (<c>GamePlacementCheck</c>).
/// </para>
/// </summary>
internal sealed class ValidationService : IValidation, Lattice.Core.Styles.IVerdictFeed
{
    private readonly ValidationParts _parts;
    private readonly ConcurrentQueue<RunOutcome> _outbox = new();
    private readonly ConcurrentQueue<(ValidationTrial Trial, TrialOutcome Outcome)> _trialbox = new();
    private readonly Queue<ValueList<Ask>> _asks = new();
    private readonly Queue<(ValueList<Ask> Asks, ValidationTrial Trial)> _trialAsks = new();
    private readonly Queue<ValueList<Ask>> _uncounted = new();
    private readonly List<ValidationTrial> _trials = [];
    private readonly AnswerBook _book = new();
    private readonly Dictionary<Selector, ValidationReport> _selections = [];
    private readonly Action<ChangeSet> _onChanged;
    private readonly Action _onReset;
    private readonly Action<MirrorChange> _onWorld;
    private readonly Subscribers<Action<ValueList<VerdictChange>>> _verdicts = new();
    private readonly Subscribers<Action<RunCounts>> _ran = new();
    private List<WorkInput> _pending = [];
    private readonly ConcurrentQueue<List<WorkInput>> _readBatches = new();
    private List<WorkInput>? _spare;
    private readonly Stack<List<(Placement At, GameAnswer Answer)>> _spareGames = new();
    private WorkInput.GameAnswers? _games;
    private readonly HashSet<Cell> _noted = [];
    private readonly Stack<List<MirrorChange>> _spareChanges = new();
    private readonly Stack<List<ReadKey>> _spareDropped = new();
    private WorkInput.World? _changes;
    private ValidationEngine? _engine;
    private ValidationResult? _ready;
    private Revision _readyFor;
    private bool _inFlight;
    private int _askAt;
    private int _trialAskAt;
    private int _asksLeft;
    private int _countAt;
    private int _posts;
    private int _findings;
    private int _settled;
    private bool _mainWaiting;
    private bool _asking;
    private bool _sweeping;
    private TimeSpan _lastSweep;
    private int _supportGeneration;
    private int _rulesGeneration;
    private Exception? _fault;
    private bool _faultThrown;
    private Exception? _analyzerThrew;

    public ValidationService(ValidationParts parts)
    {
        _parts = parts;
        _onChanged = OnBoardChanged;
        _onReset = OnBoardReset;
        _onWorld = OnWorldChanged;
    }

    /// <summary>Ghosts whose verdict changed in a re-check (Executors turn the newly failing ones into blocked notices). Main thread.</summary>
    public event Action<ValueList<VerdictChange>> VerdictsChanged { add => _verdicts.Add(value); remove => _verdicts.Remove(value); }

    /// <summary>Whether validation is off for this world after a fault.</summary>
    public bool Faulted => _fault is not null;

    /// <summary>Whether a run, its asks or changes not yet handed over are outstanding.</summary>
    public bool Busy => _inFlight || _asking || _sweeping || _pending.Count > 0 || _noted.Count > 0 || _asks.Count > 0 || _trialAsks.Count > 0 || _trials.Count > 0;

    /// <summary>Raised with each run's counts as its result is taken, for tests and timings. Main thread.</summary>
    /// <summary>The newest complete run's result, or null before the first; its revision is <see cref="ValidationResult.At"/>.</summary>
    internal ValidationResult? Latest => _ready;

    /// <summary>
    /// Rises each time a run gives findings that differ from the last ones, or the first findings at a board revision:
    /// what <see cref="FindingsFor"/> answers changes only when it rises (or the board moves). Main thread.
    /// </summary>
    internal int FindingsVersion => _findings;

    /// <summary>How many times answers were dropped around noted world changes: at most once a frame with changes.</summary>
    internal int DropPasses => _book.DropPasses;

    public event Action<RunCounts> Ran { add => _ran.Add(value); remove => _ran.Remove(value); }

    /// <summary>
    /// Starts with the world: hands the worker the mirror's snapshot (once, linear in the world's structures) and the
    /// whole board, and follows their changes from then on. Main thread.
    /// </summary>
    public void Start()
    {
        _engine = new ValidationEngine(_parts.Catalogue, _parts.Grid, _parts.Support, _parts.Rules, _parts.Mirror.Snapshot());
        _supportGeneration = _parts.Support.Rules.Generation;
        _rulesGeneration = _parts.Rules.Generation;
        _lastSweep = _parts.Clock.Now;
        _pending.Add(WholeBoard());
        _parts.Board.Events.Changed += _onChanged;
        _parts.Board.Events.BoardReset += _onReset;
        _parts.Mirror.Changed += _onWorld;
    }

    /// <summary>Ends with the world: stops following changes and stops the worker, waiting for it a bounded time.</summary>
    public void End()
    {
        _parts.Board.Events.Changed -= _onChanged;
        _parts.Board.Events.BoardReset -= _onReset;
        _parts.Mirror.Changed -= _onWorld;
        _parts.Worker.Stop(_parts.Join);
        _parts.AnalyzerWorker?.Stop(TimeSpan.Zero);
        _ready = null;
        _selections.Clear();
        _book.Clear();
        _noted.Clear();
        _trials.Clear();
    }

    /// <summary>
    /// Validates a change the board would make (<see cref="GhostBoard.Trial"/>) on the worker, without putting it on
    /// the board: the findings of its added and changed ghosts, as a normal run would give them. Pending until judged;
    /// a trial for a revision the board has left is dropped and stays pending. Ready at once with no findings while
    /// validation is off after a fault. Main-thread facts it needs are asked in the checking budget like a run's.
    /// Constant on the main thread; the worker re-checks only what the change touches, then what it put back.
    /// The worker analyzers are called with <paramref name="analyze"/> (default: the change's added and changed ghosts)
    /// on a frozen copy of the trial's world, on <see cref="ValidationParts.AnalyzerWorker"/>: the findings are in as
    /// soon as the trial is judged, and the trial is ready once the analyzers' facts are too. One that throws leaves the
    /// trial pending, and <see cref="Frame"/> throws its exception; validation stays on.
    /// </summary>
    public ValidationTrial Trial(ChangeSet changes, ValueList<Ghost>? analyze = null)
    {
        var trial = new ValidationTrial(changes, analyze ?? ValidationTrial.GhostsOf(changes), _parts.Board.Revision);
        if (_fault is not null)
            trial.Ready(ValueList<Finding>.Empty, ValueList<AnalyzerFact>.Empty);
        else
            _trials.Add(trial);
        return trial;
    }

    /// <summary>Stops working on a trial nobody waits for any more.</summary>
    public void Drop(ValidationTrial trial) => _trials.Remove(trial);

    /// <summary>
    /// Once per frame, outside the scheduler's slices: takes the worker's results, queues the asks and the reconciler,
    /// and starts the next run. Constant when there is nothing new. Throws once, with the worker's exception inside,
    /// when a run faulted.
    /// </summary>
    public void Frame()
    {
        if (_fault is not null)
        {
            if (_faultThrown)
                return;
            _faultThrown = true;
            throw new InvalidOperationException("Lattice validation stopped on a fault.", _fault);
        }

        DropNoted();
        Rules();
        while (_readBatches.TryDequeue(out var read))
        {
            foreach (var input in read)
                switch (input)
                {
                    case WorkInput.GameAnswers games:
                        games.Answers.Clear();
                        _spareGames.Push(games.Answers);
                        break;
                    case WorkInput.World world:
                        world.Changes.Clear();
                        _spareChanges.Push(world.Changes);
                        break;
                    case WorkInput.Dropped dropped:
                        dropped.Keys.Clear();
                        _spareDropped.Push(dropped.Keys);
                        break;
                }

            read.Clear();
            if (_spare is null || read.Capacity > _spare.Capacity)
                _spare = read;
        }

        while (_outbox.TryDequeue(out var outcome))
            Take(outcome);
        while (_trialbox.TryDequeue(out var tried))
            Take(tried.Trial, tried.Outcome);
        if (_analyzerThrew is { } threw)
        {
            _analyzerThrew = null;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(threw).Throw();
        }
        if (_fault is not null)
        {
            Frame();
            return;
        }

        if ((_asks.Count > 0 || _trialAsks.Count > 0) && !_asking)
        {
            _asking = true;
            _parts.Scheduler.Schedule(WorkKind.Checking, new AskWork(this), Priority.Normal);
        }

        if (!_sweeping && _book.Count > 0 && _parts.Clock.Now - _lastSweep >= _parts.SweepEvery)
        {
            _sweeping = true;
            _parts.Scheduler.Schedule(WorkKind.Checking, new SweepWork(this, _book.Keys()), Priority.Low);
        }

        if (!_inFlight && (NextTrial() is not null || (!_mainWaiting && _pending.Count > 0)))
            Post();
    }

    /// <summary>How many of a run's asks a checking step counts against the book.</summary>
    internal const int CountedPerStep = 1024;

    /// <summary>How many asks answered already a checking step passes over looking for one to answer.</summary>
    internal const int SkippedPerStep = 1024;

    // A run's asks are queued as they come (constant here, outside the slices). In the checking slices they are first
    // counted against the book, some each step, and room for the answers is taken once all are counted, before the
    // first is answered, so no answering step grows a collection the size of the plan (a copy of it and, past 85 KB, a
    // large block from the collector). A run lists every ask it still waits on, most of them answered already, so room
    // is taken for the ones the book does not hold; it at least doubles when it grows, so a plan whose asks come over
    // many runs copies its collections a few times in all. A trial's asks queue apart and are answered first, so a
    // preview never waits behind a plan's.
    private void Enqueue(ValueList<Ask> asks, ValidationTrial? trial)
    {
        if (trial is null)
            _asks.Enqueue(asks);
        else
            _trialAsks.Enqueue((asks, trial));
        _uncounted.Enqueue(asks);
    }

    // Counts up to CountedPerStep asks of the lists not counted yet; takes the room once the last is counted. False
    // when every list is counted.
    private bool CountSome()
    {
        if (_uncounted.Count == 0)
            return false;
        var list = _uncounted.Peek();
        var end = Math.Min(list.Count, _countAt + CountedPerStep);
        for (; _countAt < end; _countAt++)
            if (!_book.Holds(list[_countAt].Key))
                _asksLeft++;
        if (_countAt < list.Count)
            return true;
        _uncounted.Dequeue();
        _countAt = 0;
        if (_uncounted.Count == 0)
            TakeRoom();
        return true;
    }

    private void TakeRoom()
    {
        if (_pending.Capacity < _pending.Count + _asksLeft)
            _pending.Capacity = Math.Max(_pending.Count + _asksLeft, 2 * _pending.Capacity);
        _book.Reserve(_asksLeft);
        GameRoom(_games is not null && IsLast(_games) ? _games.Answers : null);
    }

    // Room for the asks left in the game answers they will go in: the batch being filled, or the next one.
    private void GameRoom(List<(Placement At, GameAnswer Answer)>? filling)
    {
        if (filling is null && (_spareGames.Count == 0 || _spareGames.Peek().Capacity < _asksLeft))
            _spareGames.Push(new List<(Placement, GameAnswer)>(_asksLeft));
        var games = filling ?? _spareGames.Peek();
        if (games.Capacity < games.Count + _asksLeft)
            games.Capacity = Math.Max(games.Count + _asksLeft, 2 * games.Capacity);
    }

    private bool IsLast(WorkInput input) => _pending.Count > 0 && ReferenceEquals(_pending[_pending.Count - 1], input);

    // A run lists every ask the board's checking still waits on (a ready run, none), so the asks of earlier runs not
    // yet worked through are left out: what they ask that is still wanted is in the new list. Trials' asks stay (room
    // for them is taken again only if the book needs it). Linear in the queued trial lists.
    private void Supersede()
    {
        _asks.Clear();
        _askAt = 0;
        _uncounted.Clear();
        _countAt = 0;
        foreach (var (asks, _) in _trialAsks)
            _uncounted.Enqueue(asks);
        _asksLeft = 0;
    }

    // The first trial ready to go to the worker: not asked yet, or its asks all answered (the board's asks may still be
    // waiting: a trial goes with whatever answers are in); trials prepared for another revision are dropped.
    private ValidationTrial? NextTrial()
    {
        var next = new Revision(_parts.Board.Revision.Value + 1);
        for (var i = _trials.Count - 1; i >= 0; i--)
            if (!_trials[i].Posted && _trials[i].Changes.Revision != next)
                _trials.RemoveAt(i);
        foreach (var trial in _trials)
            if (!trial.Posted && !trial.Judged && (!trial.Asked || !Asking(trial)))
                return trial;
        return null;
    }

    // Whether a trial's asks are still queued to be answered.
    private bool Asking(ValidationTrial trial)
    {
        foreach (var (_, queued) in _trialAsks)
            if (queued == trial)
                return true;
        return false;
    }

    /// <summary>
    /// The findings for the ghosts a selector picks out of the newest run, to be worked out in parts
    /// (<see cref="FindingsRun"/>); none while that run is not at the board's revision, validation is off, or the
    /// selector is refused. The report comes out as <see cref="Check"/> gives it. Cost: the selection.
    /// </summary>
    internal Option<FindingsRun> FindingsFor(Selector which)
    {
        var at = _parts.Board.Revision;
        if (_fault is not null || _ready is null || _readyFor != at)
            return Option<FindingsRun>.None;
        var view = _parts.Board.ViewFor(_parts.Viewer);
        return view.Select(which).TryGet(out var ghosts)
            ? Option.Some(new FindingsRun(_ready, ghosts, view, at))
            : Option<FindingsRun>.None;
    }

    public Result<Answer<ValidationReport>, Reason> Check(Selector which)
    {
        if (_fault is not null)
            return Result<Answer<ValidationReport>, Reason>.Error(Reason.Of(
                ReasonCodes.ModuleMissing, ValidationCodes.Validation, ValueMap<string, string>.Of("module", ValidationCodes.Module)));
        var at = _parts.Board.Revision;
        var view = _parts.Board.ViewFor(_parts.Viewer);
        if (_ready is null || _readyFor != at)
            return view.Select(which).Map(_ => (Answer<ValidationReport>)new Answer<ValidationReport>.Pending(at));
        if (_selections.TryGetValue(which, out var cached))
            return Result<Answer<ValidationReport>, Reason>.Ok(new Answer<ValidationReport>.Ready(cached, at));
        return view.Select(which).Map(ghosts =>
        {
            var report = Select(_ready, ghosts, view, at);
            _selections[which] = report;
            return (Answer<ValidationReport>)new Answer<ValidationReport>.Ready(report, at);
        });
    }

    private static ValidationReport Select(ValidationResult result, ValueList<Ghost> ghosts, IGhostBoard view, Revision at)
    {
        var run = new FindingsRun(result, ghosts, view, at);
        run.Step(int.MaxValue);
        return run.Report();
    }

    private WorkInput WholeBoard()
    {
        var ghosts = new Ghost[_parts.Board.Index.Ghosts.Count];
        var at = 0;
        foreach (var entry in _parts.Board.Index.Ghosts.Values)
            ghosts[at++] = entry.Ghost;
        return new WorkInput.Ghosts(_parts.Board.Revision, Board.Lists.Own(ghosts), ValueList<GhostId>.Empty, Reset: true);
    }

    // The change set's ghosts, copied once into the worker's input (a plan landing is one change set of every ghost).
    private void OnBoardChanged(ChangeSet changes)
    {
        var upserts = changes.Changed.Count == 0 ? changes.Added : Upserts(changes);
        var gone = new GhostId[changes.Removed.Count];
        for (var i = 0; i < gone.Length; i++)
            gone[i] = changes.Removed[i].Ghost.Id;
        _pending.Add(new WorkInput.Ghosts(changes.Revision, upserts, Board.Lists.Own(gone), Reset: false));
    }

    private static ValueList<Ghost> Upserts(ChangeSet changes)
    {
        var upserts = new Ghost[changes.Added.Count + changes.Changed.Count];
        var at = 0;
        foreach (var ghost in changes.Added)
            upserts[at++] = ghost;
        foreach (var change in changes.Changed)
            upserts[at++] = change.After;
        return Board.Lists.Own(upserts);
    }

    private void OnBoardReset() => _pending.Add(WholeBoard());

    // A world change is noted, not acted on: the fine buckets of its structure's cells join the set of changed buckets
    // and the change joins the batch going to the worker (a new batch after any other input, so the worker takes
    // everything in the order given). The answers near the noted buckets are dropped together before the book is next
    // read (DropNoted), so a burst of changes drops them once and allocates nothing a change.
    private void OnWorldChanged(MirrorChange change)
    {
        switch (change)
        {
            case MirrorChange.Appeared appeared:
                Note(appeared.Now);
                break;
            case MirrorChange.Altered altered:
                Note(altered.Before);
                Note(altered.Now);
                break;
            case MirrorChange.Gone gone:
                Note(gone.Before);
                break;
        }

        if (_changes is null || !IsLast(_changes))
        {
            _changes = new WorkInput.World(_spareChanges.Count > 0 ? _spareChanges.Pop() : new List<MirrorChange>());
            _pending.Add(_changes);
        }

        _changes.Changes.Add(change);
    }

    private void Note(StructureInfo info)
    {
        _noted.Add(ReadKey.FineBucketOf(info.Ref.At));
        if (_parts.Mirror.SpotsOf(info).TryGet(out var spots))
            for (var i = 0; i < spots.Count; i++)
                _noted.Add(ReadKey.FineBucketOf(spots[i].Cell));
    }

    // Drops the answers near the buckets noted since the last drop, in one pass, and hands their keys to the worker.
    // Runs before anything reads the book (validation's frame, an asking or sweeping step), so no answer given before
    // a change outlives it and none given after it is dropped by it.
    private void DropNoted()
    {
        if (_noted.Count == 0)
            return;
        var dropped = _spareDropped.Count > 0 ? _spareDropped.Pop() : new List<ReadKey>();
        _book.DropAround(_noted, dropped);
        _noted.Clear();
        if (dropped.Count == 0)
            _spareDropped.Push(dropped);
        else
            _pending.Add(new WorkInput.Dropped(dropped));
    }

    private void Rules()
    {
        var support = _parts.Support.Rules.Generation;
        var rules = _parts.Rules.Generation;
        if (support == _supportGeneration && rules == _rulesGeneration)
            return;
        _supportGeneration = support;
        _rulesGeneration = rules;
        _pending.Add(new WorkInput.RulesChanged());
    }

    private void Take(RunOutcome outcome)
    {
        _inFlight = false;
        _settled = _posts;
        foreach (var key in outcome.Unread)
            _book.Forget(key, _settled);
        switch (outcome)
        {
            case RunOutcome.Faulted faulted:
                _fault = faulted.Error;
                foreach (var trial in _trials)
                    trial.Ready(ValueList<Finding>.Empty, ValueList<AnalyzerFact>.Empty);
                _trials.Clear();
                break;
            case RunOutcome.Pending pending:
                Counted(pending.Counts);
                Supersede();
                if (pending.Asks.Count > 0)
                {
                    Enqueue(pending.Asks, null);
                    _mainWaiting |= pending.MainAsks;
                }
                break;
            case RunOutcome.Ready ready:
                Counted(ready.Counts);
                Supersede();
                var first = _ready is null || _readyFor != ready.Result.At;
                _ready = ready.Result;
                _readyFor = ready.Result.At;
                _selections.Clear();
                if (first || ready.Changed)
                {
                    _findings++;
                    _parts.Board.Events.Queue(new AnswersReadyEvent(ready.Result.At));
                }
                if (ready.Verdicts.Count > 0)
                {
                    var handlers = _verdicts.Current;
                    for (var i = 0; i < handlers.Length; i++)
                        handlers[i](ready.Verdicts);
                }

                break;
        }
    }

    private void Take(ValidationTrial trial, TrialOutcome outcome)
    {
        trial.Posted = false;
        if (!_trials.Contains(trial))
            return;
        switch (outcome)
        {
            case TrialOutcome.Ready { Analyzing: true } judged:
                trial.Judge(judged.Findings, analyzing: true);
                break;
            case TrialOutcome.Ready ready:
                trial.Judge(ready.Findings, analyzing: false);
                trial.Analyzed(ready.Facts);
                Settled(trial);
                break;
            case TrialOutcome.Analyzed analyzed:
                trial.Analyzed(analyzed.Facts);
                Settled(trial);
                break;
            case TrialOutcome.Threw threw:
                _trials.Remove(trial);
                _analyzerThrew ??= threw.Error;
                break;
            case TrialOutcome.Pending pending:
                trial.Asked = pending.Asks.Count > 0;
                if (trial.Asked)
                {
                    Enqueue(pending.Asks, trial);
                    _mainWaiting |= pending.MainAsks;
                }

                break;
        }
    }

    private void Settled(ValidationTrial trial)
    {
        _trials.Remove(trial);
        if (trial.Answer.At == _parts.Board.Revision)
            _parts.Board.Events.Queue(new AnswersReadyEvent(trial.Answer.At));
    }

    private void Counted(RunCounts counts)
    {
        var handlers = _ran.Current;
        for (var i = 0; i < handlers.Length; i++)
            handlers[i](counts);
    }

    private void Post()
    {
        var batch = _pending;
        var trial = NextTrial();
        _pending = _spare ?? new List<WorkInput>(_asksLeft);
        _spare = null;
        _games = null;
        _changes = null;
        if (_asksLeft > 0)
            GameRoom(null);
        _posts++;
        _inFlight = true;
        if (trial is not null)
            trial.Posted = true;
        var engine = _engine!;
        var outbox = _outbox;
        var trialbox = _trialbox;
        var readBatches = _readBatches;
        var analyzers = _parts.Analyzers;
        var analyzerWorker = _parts.AnalyzerWorker;
        _parts.Worker.Post(() =>
        {
            IWorldQuery? world;
            TrialOutcome tried;
            try
            {
                if (batch.Count > 0 || trial is null)
                    outbox.Enqueue(engine.Run(batch));
                readBatches.Enqueue(batch);
                if (trial is null)
                    return;
                (tried, var after, var staged) = engine.Trial(trial.Changes);
                world = tried is TrialOutcome.Ready && analyzers.Any(CallbackThread.Worker) ? engine.AnalyzerWorld(staged) : null;
                outbox.Enqueue(after);
            }
            catch (Exception error)
            {
                outbox.Enqueue(new RunOutcome.Faulted(0, error));
                return;
            }

            if (tried is not TrialOutcome.Ready ready || world is null)
                trialbox.Enqueue((trial, tried));
            else if (analyzerWorker is null)
            {
                var analyzed = Analyzed(analyzers, world, trial);
                trialbox.Enqueue((trial, analyzed is TrialOutcome.Analyzed facts ? ready with { Facts = facts.Facts } : analyzed));
            }
            else
            {
                trialbox.Enqueue((trial, ready with { Analyzing = true }));
                analyzerWorker.Post(() => trialbox.Enqueue((trial, Analyzed(analyzers, world, trial))));
            }
        });
    }

    // The worker analyzers on a frozen world, as plain calls; an exception is handed to the main thread, where it leaves
    // Frame (the call that runs validation's work), and never turns validation off.
    private static TrialOutcome Analyzed(Analysis.Analyzers analyzers, IWorldQuery world, ValidationTrial trial)
    {
        try
        {
            return new TrialOutcome.Analyzed(ValidationEngine.Analyze(analyzers, world, trial.Analyze));
        }
        catch (Exception error)
        {
            return new TrialOutcome.Threw(error);
        }
    }

    // The next ask not answered already (an answer on its way to the worker), passing over at most SkippedPerStep
    // answered ones (more is false then: look again next step); none, with more false, when every list is done. A
    // main-thread rule, or the game about a board ghost's placement, is not asked once the ghost has left the board
    // (removed or done): no judgement reads that answer. Trials' asks come first, a dropped trial's passed over; they
    // are about ghosts not on the board (or the ghosts a trial's ghosts read), and are always asked.
    private (Ask Ask, bool Trial)? NextAsk(out bool more)
    {
        more = false;
        var skipped = 0;
        while (_trialAsks.Count > 0)
        {
            var (list, of) = _trialAsks.Peek();
            while (_trialAskAt < list.Count && _trials.Contains(of))
            {
                if (skipped++ == SkippedPerStep)
                {
                    more = true;
                    return null;
                }

                var ask = list[_trialAskAt++];
                if (_asksLeft > 0 && !_book.Holds(ask.Key))
                    _asksLeft--;
                if (ask is Ask.MainSupport or Ask.MainRule || !_book.OnItsWay(ask.Key, _settled))
                    return (ask, true);
            }

            _trialAsks.Dequeue();
            _trialAskAt = 0;
        }

        while (_asks.Count > 0)
        {
            var list = _asks.Peek();
            while (_askAt < list.Count)
            {
                if (skipped++ == SkippedPerStep)
                {
                    more = true;
                    return null;
                }

                var ask = list[_askAt++];
                if (_asksLeft > 0 && !_book.Holds(ask.Key))
                    _asksLeft--;
                if (ask is Ask.MainSupport or Ask.MainRule && !_parts.Board.Index.Ghosts.ContainsKey(new GhostId(ask.Key.Id)))
                    continue;
                if (ask is Ask.Game game && !OnTheBoard(game.At))
                    continue;
                if (!_book.OnItsWay(ask.Key, _settled))
                    return (ask, false);
            }

            _asks.Dequeue();
            _askAt = 0;
        }

        _asksLeft = 0;
        _mainWaiting = false;
        return null;
    }

    // Whether a board ghost is placed there. The game is asked about a ghost's own placement, so a ghost there is in
    // the board's index of the placement's cell.
    private bool OnTheBoard(Placement at)
    {
        var index = _parts.Board.Index;
        foreach (var id in index.InCell(at.At))
            if (index.Ghosts.TryGetValue(id, out var entry) && entry.Shape.TryGet(out var shape) && shape.Placement == at)
                return true;
        return false;
    }

    private void Answer(Ask ask, bool trial)
    {
        switch (ask)
        {
            case Ask.Game game:
                var answer = _parts.Placement.Check(game.At).Match<GameAnswer>(
                    static _ => GameAnswer.Accepted,
                    static why => new GameAnswer.Refuses(why.GameMessage));
                Give(ask.Key, game.At, answer);
                break;
            case Ask.Room room:
                var inRoom = _parts.World.RoomAt(room.At);
                Give(ask.Key, room.At, inRoom, new WorkInput.RoomAnswered(room.At, inRoom));
                break;
            case Ask.Network network:
                var on = _parts.World.NetworkOf(network.Of);
                var at = _parts.Mirror.Structure(network.Of).TryGet(out var info) ? info.Ref.At : default;
                Give(ask.Key, at, on, new WorkInput.NetworkAnswered(network.Of, on));
                break;
            case Ask.MainSupport support:
                var input = support.Input with { World = Fork(support.Input.World) };
                var verdict = _parts.Support.Rules.Check(support.Entry, input);
                _pending.Add(new WorkInput.MainAnswered(support.Entry.Rule.Id, support.Ghost, support.Epoch, verdict) { Trial = trial });
                break;
            case Ask.MainRule rule:
                var ruled = _parts.Rules.Check(rule.Entry, rule.Input with { World = Fork(rule.Input.World) });
                _pending.Add(new WorkInput.MainAnswered(rule.Entry.Rule.Id, rule.Input.Ghost.Id, rule.Epoch, ruled) { Trial = trial });
                break;
        }
    }

    // A main-thread rule reads the view it was asked with, through a log of its own.
    private static IWorldQuery Fork(IWorldQuery world) => world is SupportWorld view ? view.Fork() : world;

    // An answer goes to the worker with the next post; until that post's run is taken it is on its way, and the
    // worker's asks for it are passed over.
    private void Give(ReadKey key, Cell at, object answer, WorkInput input)
    {
        _book.Record(key, at, answer, _posts + 1);
        _pending.Add(input);
    }

    // The game's answers go in one batch at the end of the inputs (a new one after any other input, so the worker
    // takes everything in the order given), its list reused once the worker hands the inputs back: an answer step
    // makes no object.
    private void Give(ReadKey key, Placement at, GameAnswer answer)
    {
        _book.Record(key, at.At, answer, _posts + 1);
        if (_games is null || !IsLast(_games))
        {
            _games = new WorkInput.GameAnswers(_spareGames.Count > 0 ? _spareGames.Pop() : new List<(Placement, GameAnswer)>(_asksLeft));
            _pending.Add(_games);
        }

        _games.Answers.Add((at, answer));
    }

    // The reconciler: a fact asked again that answers differently goes to the worker.
    private void Resweep(ReadKey key)
    {
        if (!_book.TryGet(key, out var held))
            return;
        switch (key.Kind)
        {
            case ReadKind.Game:
                var placement = key.Placement!;
                var answer = _parts.Placement.Check(placement).Match<GameAnswer>(
                    static _ => GameAnswer.Accepted,
                    static why => new GameAnswer.Refuses(why.GameMessage));
                if (!Equals(held, answer))
                    Give(key, placement, answer);
                break;
            case ReadKind.Room:
                var room = _parts.World.RoomAt(key.Cell);
                if (!Equals(held, room))
                    Give(key, key.Cell, room, new WorkInput.RoomAnswered(key.Cell, room));
                break;
            case ReadKind.Network:
                var id = new ReferenceId(key.Id);
                var network = _parts.World.NetworkOf(id);
                if (!Equals(held, network))
                    Give(key, _parts.Mirror.Structure(id).TryGet(out var info) ? info.Ref.At : default, network, new WorkInput.NetworkAnswered(id, network));
                break;
        }
    }

    /// <summary>
    /// Answers trials' asks first, one per step; then counts the board's asks against the book and takes room for their
    /// answers, then answers them one per step, in the checking budget. A step's work is bounded: a part of a list counted, the room taken, or one answer.
    /// </summary>
    private sealed class AskWork(ValidationService service) : ISlicedWork
    {
        public StepOutcome Step()
        {
            service.DropNoted();
            if (service._trialAsks.Count > 0 && service.NextAsk(out _) is { Trial: true } first)
            {
                service.Answer(first.Ask, true);
                return StepOutcome.More;
            }

            if (service.CountSome())
                return StepOutcome.More;

            if (service.NextAsk(out var more) is { } next)
            {
                service.Answer(next.Ask, next.Trial);
                return StepOutcome.More;
            }

            if (more)
                return StepOutcome.More;

            service._asking = false;
            return StepOutcome.Done;
        }
    }

    /// <summary>Asks every held fact again, one per step, at low priority.</summary>
    private sealed class SweepWork(ValidationService service, List<ReadKey> keys) : ISlicedWork
    {
        private int _next;

        public StepOutcome Step()
        {
            service.DropNoted();
            var clock = service._parts.Clock;
            var until = clock.Now + service._parts.SweepSlice;
            while (_next < keys.Count)
            {
                service.Resweep(keys[_next++]);
                if (clock.Now >= until)
                    break;
            }

            if (_next < keys.Count)
                return StepOutcome.NextFrame;
            service._sweeping = false;
            service._lastSweep = service._parts.Clock.Now;
            return StepOutcome.Done;
        }
    }
}
