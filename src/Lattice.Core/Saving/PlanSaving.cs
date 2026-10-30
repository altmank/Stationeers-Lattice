using System;
using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Slices;
using Lattice.Core.Written;

namespace Lattice.Core.Saving;

/// <summary>Saving's rule id.</summary>
internal static class SavingReasons
{
    public static readonly RuleId Saving = RuleId.Known("lattice.saving");
}

/// <summary>What a load opened plans from.</summary>
internal enum LoadBasis
{
    /// <summary>The save's record: every plan at the line the save remembers.</summary>
    Record = 1,

    /// <summary>A save with no record (made before Lattice, copied in, record lost): every plan at its newest line.</summary>
    NoRecord = 2,

    /// <summary>A new world: no save and no plan files.</summary>
    NewWorld = 3,
}

/// <summary>
/// Plan changes written after the loaded save (a crash between saves): the plan, the line the save remembers, the
/// newest line after it on its newest branch, and how many lines that is. Accepting moves the plan to the newest line.
/// </summary>
internal sealed record RecoveryOffer(PlanId Plan, long Head, long Newest, int Changes);

/// <summary>
/// What a load found: what it opened from, the problems in the files (torn lines, bad lines), the recovery offers,
/// plans hidden because the save is older than them, plans finished at the save's point, and records this version
/// could not read. Reported once; Lattice never decides for the player.
/// </summary>
internal sealed record OpenReport(
    LoadBasis Basis,
    Option<SaveKey> Save,
    ValueList<FileProblem> Problems,
    ValueList<RecoveryOffer> Offers,
    ValueList<PlanId> Hidden,
    ValueList<PlanId> Finished,
    int Unread)
{
    /// <summary>
    /// What the board reports for the load (<see cref="IGhostEvents.Loaded"/>): whether the save had a record and the
    /// plans whose files are ahead of it; none for a new world.
    /// </summary>
    public Option<LoadReport> ForBoard()
    {
        if (Basis == LoadBasis.NewWorld)
            return Option<LoadReport>.None;
        var ahead = new List<PlanAhead>(Offers.Count);
        foreach (var offer in Offers)
            ahead.Add(new PlanAhead(offer.Plan, offer.Changes));
        SaveRecord record = Basis == LoadBasis.Record ? new SaveRecord.Found() : new SaveRecord.Missing();
        return Option.Some(new LoadReport(record, ValueList.From(ahead)));
    }
}

/// <summary>
/// What the board's reads about saving answer from (<see cref="IPlans.LastLoad"/>, <see cref="IPlans.Finished"/>,
/// <see cref="IPlans.AheadOfSave"/>): the last load's report, the finished-plans index, and how many changes a
/// plan's file holds after the loaded save. Constant cost; the index is read once per world. Also what
/// <see cref="Command.CatchUp"/> applies: <see cref="Newest"/> then, once the board committed it, <see cref="CaughtUp"/>.
/// </summary>
internal interface ISavedPlans
{
    Option<LoadReport> LastLoad { get; }

    ValueList<FinishedPlan> Finished { get; }

    /// <summary>
    /// A finished plan's record at its finishing line, as the index keeps it: what the access policy judges for the
    /// finished list. None for a plan the index does not list or holds no record for. Constant cost.
    /// </summary>
    Option<Plan> FinishedRecord(PlanId plan);

    Result<int, Reason> AheadOfSave(PlanId plan);

    /// <summary>
    /// The plan as its file holds it at the newest line beyond the loaded save; refused not-found when the file holds
    /// nothing beyond it. Linear in the lines after the nearest checkpoint.
    /// </summary>
    Result<NewestPlan, Reason> Newest(PlanId plan);

    /// <summary>
    /// The board committed the plan's catch-up as <paramref name="set"/>: the plan's file is at its newest line now,
    /// and the set's line for the plan is not written (the file already holds those changes).
    /// </summary>
    void CaughtUp(PlanId plan, ChangeSetId set);

    /// <summary>
    /// A finished plan as its file holds it at its finishing line (what repair, take-down, templates and reopen read).
    /// Refused not-found for a plan the world has no file for, not-allowed (<c>problem</c> = <c>not-finished</c>) for a
    /// plan not finished at the line the world is at. Linear in the plan's lines (read from the first).
    /// </summary>
    Result<FinishedContents, Reason> FinishedContents(PlanId plan);
}

/// <summary>A plan at its file's newest line: its record (none if the lines never gave one), whether it is finished, its ghosts.</summary>
internal sealed record NewestPlan(Option<Plan> Record, bool Finished, ValueList<Ghost> Ghosts)
{
    /// <summary>The plan's ghosts done along its lines up to that line.</summary>
    public int Done { get; init; }

    /// <summary>Of <see cref="Done"/>, how many each executor held the claim on.</summary>
    public ValueMap<ExecutorId, int> DoneBy { get; init; } = ValueMap<ExecutorId, int>.Empty;
}

/// <summary>The board to start a world with, and what the load found.</summary>
internal sealed record OpenedPlans(BoardSeed Seed, OpenReport Report);

/// <summary>
/// Plan files and what each game save remembers about them: loading
/// an older save, history and finished plans.
/// <list type="bullet">
/// <item><see cref="Record"/> queues each board change set; the frame's saving slice (<see cref="Step"/>) turns it into
/// one line per plan it touches, a few operations a step, following the plan's current line, so after an older save
/// the file branches, and writes the lines to disk. <see cref="Flush"/> does the rest at once. Every read of the plans'
/// lines (heads, finished, ahead of save, newest, a save) first works out what is queued, so it answers as if each
/// set had been recorded when it came.</item>
/// <item><see cref="OnGameSave"/>: flushes, checkpoints every active plan at its line, and writes the save's record.
/// A plan also gets a checkpoint every <c>checkpointEvery</c> lines.</item>
/// <item><see cref="Open"/>: finds the save's record by path and fingerprint (a copied save by fingerprint), rebuilds
/// each plan from the nearest checkpoint on its branch, hides plans the save is older than, and reports.</item>
/// <item>A finished plan's file moves to the archive with an index entry; a save from before it finished reopens it.</item>
/// </list>
/// Ids are never reused: the seed's counters are one above the largest id in any line of any file, every branch
/// included. Main thread; file work on the calling thread.
/// </summary>
internal sealed class PlanSaving : ISlicedWork, ISavedPlans
{
    private WorldFolder _folder;
    private readonly int _checkpointEvery;
    private readonly Dictionary<PlanId, PlanTree> _trees = new();
    private readonly Dictionary<PlanId, long> _heads = new();
    private readonly Dictionary<PlanId, PlanReplay> _states = new();
    private readonly Dictionary<PlanId, int> _sinceCheckpoint = new();
    private readonly Queue<Queued> _pending = new();
    private readonly Queue<ChangeSet> _incoming = new();
    private readonly Queue<PlanPart> _parts = new();
    private ChangeSet? _set;
    private ChangeSplit? _split;
    private Building? _building;
    private WorldFolder.CheckpointWrite? _checkpoint;
    private long _queuedChars;
    private readonly List<SavePoint> _records = new();
    private readonly List<RecoveryOffer> _offers = new();
    private readonly Dictionary<PlanId, PlanReplay> _newest = new();
    private readonly HashSet<(ChangeSetId Set, PlanId Plan)> _written = new();
    private IdCounters _next = new(1, GhostBoard.DefaultPlan.Value + 1, 1);
    private Option<LoadReport> _lastLoad;
    private ValueList<FinishedPlan>? _finished;
    private Dictionary<PlanId, Plan> _finishedRecords = new();
    private Revision _revision;

    public PlanSaving(WorldFolder folder, int checkpointEvery = 2000)
    {
        _folder = folder;
        _checkpointEvery = checkpointEvery;
    }

    /// <summary>Changes of a queued set split into plan parts, or operations of a line written and applied, in one step.</summary>
    public const int OpsPerStep = 64;

    /// <summary>
    /// The most characters of one plan's queued lines a step appends at once (a longer single line goes alone): the
    /// file is opened once for them, which is most of what an append costs.
    /// </summary>
    public const int AppendChars = 64 * 1024;

    /// <summary>Ghosts of a checkpoint formatted and appended in one step.</summary>
    public const int CheckpointUnits = 256;

    /// <summary>Lines and checkpoints not yet on disk; a change set not yet worked into lines counts once, so does a checkpoint being written.</summary>
    public int Pending => _pending.Count + _incoming.Count + (_set is null ? 0 : 1) + (_checkpoint is null ? 0 : 1);

    public Option<LoadReport> LastLoad => _lastLoad;

    public ValueList<FinishedPlan> Finished
    {
        get
        {
            if (_finished is { } known)
                return known;
            var index = _folder.Finished();
            var plans = new List<FinishedPlan>(index.Count);
            var records = new Dictionary<PlanId, Plan>(index.Count);
            foreach (var entry in index)
            {
                plans.Add(entry.Plan);
                if (entry.Record.TryGet(out var record))
                    records[entry.Plan.Id] = record;
            }

            _finishedRecords = records;
            return _finished = ValueList.From(plans);
        }
    }

    public Option<Plan> FinishedRecord(PlanId plan)
    {
        _ = Finished;
        return _finishedRecords.TryGetValue(plan, out var record) ? Option.Some(record) : Option<Plan>.None;
    }

    public Result<int, Reason> AheadOfSave(PlanId plan)
    {
        Build(int.MaxValue);
        foreach (var offer in _offers)
            if (offer.Plan == plan)
                return Result<int, Reason>.Ok(offer.Changes);
        return _heads.ContainsKey(plan) || _trees.ContainsKey(plan)
            ? Result<int, Reason>.Ok(0)
            : Result<int, Reason>.Error(Reason.Of(ReasonCodes.NotFound, SavingReasons.Saving, ValueMap.Of("plan", plan.Value.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>Whether a plan is finished at the line it is at.</summary>
    public bool IsFinished(PlanId plan)
    {
        Build(int.MaxValue);
        return _states.TryGetValue(plan, out var state) && state.Finished;
    }

    /// <summary>The line each plan is at; a finished plan stays at its finishing line.</summary>
    public IReadOnlyDictionary<PlanId, long> Heads
    {
        get
        {
            Build(int.MaxValue);
            return _heads;
        }
    }

    /// <summary>
    /// Opens the plans for a world loaded from <paramref name="save"/> (none for a new world). Linear in the files'
    /// length to index them, plus the lines after each plan's nearest checkpoint.
    /// </summary>
    public OpenedPlans Open(Option<SaveKey> save)
    {
        Clear();
        _folder.NewSession();
        var problems = new List<FileProblem>();
        _records.AddRange(_folder.Records());
        var record = save.Bind(Find);
        var archivedOnly = new Dictionary<PlanId, string>();
        var maxima = new IdMaxima();
        foreach (var finished in _folder.Finished())
            maxima.Plan(finished.Plan.Id.Value);

        foreach (var (plan, path, archived) in _folder.PlanFiles())
        {
            maxima.Plan(plan.Value);
            if (archived && !(record.TryGet(out var r) && r.Heads.ContainsKey(plan)))
            {
                archivedOnly[plan] = path;
                continue;
            }

            var text = _folder.ReadText(path);
            maxima.Scan(text);
            var (tree, found) = PlanTree.Read(plan, text);
            _trees[plan] = tree;
            problems.AddRange(found);
        }

        foreach (var (plan, path) in archivedOnly)
            maxima.Scan(_folder.ReadText(path));

        var basis = record.IsSome ? LoadBasis.Record : _trees.Count == 0 && archivedOnly.Count == 0 ? LoadBasis.NewWorld : LoadBasis.NoRecord;
        if (record.TryGet(out var saved))
        {
            foreach (var (plan, head) in saved.Heads)
                if (_trees.TryGetValue(plan, out var tree) && (head == 0 || tree.Has(head)))
                    _heads[plan] = head;
                else
                    problems.Add(new FileProblem.BadLine(plan, head, "the save's line is not in the plan's file"));
        }
        else
        {
            foreach (var (plan, tree) in _trees)
                if (tree.Count > 0)
                    _heads[plan] = tree.Newest;
        }

        var unread = 0;
        foreach (var (plan, head) in _heads)
        {
            var state = Rebuild(_trees[plan], head, problems);
            _states[plan] = state;
            unread += state.Unread;
        }

        var hidden = new List<PlanId>();
        foreach (var plan in _trees.Keys)
            if (!_heads.ContainsKey(plan))
                hidden.Add(plan);
        if (record.TryGet(out var from))
            Offers(from, hidden);

        _next = new IdCounters(maxima.Ghost + 1, Math.Max(maxima.PlanId + 1, GhostBoard.DefaultPlan.Value + 1), maxima.Set + 1);
        _revision = new Revision(maxima.Revision);
        var finishedPlans = new List<PlanId>();
        foreach (var (plan, state) in _states)
            if (state.Finished)
                finishedPlans.Add(plan);
        foreach (var plan in archivedOnly.Keys)
            finishedPlans.Add(plan);

        FillFinishedRecords();
        hidden.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        finishedPlans.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        var report = new OpenReport(
            basis,
            save,
            ValueList.From(problems),
            ValueList.From(_offers),
            ValueList.From(hidden),
            ValueList.From(finishedPlans),
            unread);
        _lastLoad = report.ForBoard();
        return new OpenedPlans(Seed(), report);
    }

    /// <summary>
    /// Moves the offered plans forward to their newest lines (crash recovery; the caller decides which). Returns the
    /// board to restart with; plans not offered are left as they are.
    /// </summary>
    public BoardSeed MoveForward(IEnumerable<PlanId> plans)
    {
        Flush();
        var problems = new List<FileProblem>();
        foreach (var plan in plans)
            foreach (var offer in _offers)
                if (offer.Plan == plan)
                {
                    _heads[plan] = offer.Newest;
                    _states[plan] = Rebuild(TreeFor(plan), offer.Newest, problems);
                }

        _offers.RemoveAll(o => !_heads.TryGetValue(o.Plan, out var head) || head == o.Newest);
        return Seed();
    }

    public Result<NewestPlan, Reason> Newest(PlanId plan)
    {
        Build(int.MaxValue);
        foreach (var offer in _offers)
            if (offer.Plan == plan)
            {
                Flush();
                var state = Rebuild(TreeFor(plan), offer.Newest, new List<FileProblem>());
                _newest[plan] = state;
                var ghosts = new List<Ghost>(state.Ghosts);
                ghosts.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
                return Result<NewestPlan, Reason>.Ok(new NewestPlan(state.Record, state.Finished, ValueList.From(ghosts)) { Done = state.Done, DoneBy = state.DoneBy });
            }

        return Result<NewestPlan, Reason>.Error(Reason.Of(ReasonCodes.NotFound, SavingReasons.Saving,
            ValueMap.Of("plan", plan.Value.ToString(CultureInfo.InvariantCulture)).With("ahead", "0")));
    }

    public void CaughtUp(PlanId plan, ChangeSetId set)
    {
        Build(int.MaxValue);
        for (var i = 0; i < _offers.Count; i++)
            if (_offers[i].Plan == plan)
            {
                _heads[plan] = _offers[i].Newest;
                _states[plan] = _newest.TryGetValue(plan, out var state) ? state : Rebuild(TreeFor(plan), _offers[i].Newest, new List<FileProblem>());
                _offers.RemoveAt(i);
                _newest.Remove(plan);
                _written.Add((set, plan));
                return;
            }
    }

    public Result<FinishedContents, Reason> FinishedContents(PlanId plan)
    {
        Flush();
        var tree = TreeFor(plan);
        if (tree.Count == 0)
            return Result<FinishedContents, Reason>.Error(Reason.Of(ReasonCodes.NotFound, SavingReasons.Saving, PlanFact(plan)));
        var head = FinishedHead(plan, tree);
        var replay = new FinishedReplay(plan);
        foreach (var line in tree.PathTo(head))
            if (ChangeLineForm.Read(line.Text).TryGet(out var read))
                replay.Apply(read);
        if (!replay.Finished || !replay.Record.TryGet(out var record))
            return Result<FinishedContents, Reason>.Error(Reason.Of(ReasonCodes.NotAllowed, SavingReasons.Saving, PlanFact(plan).With("problem", "not-finished")));
        return Result<FinishedContents, Reason>.Ok(new FinishedContents(record, ValueList.From(replay.Built), ValueList.From(replay.Left), head) { Done = replay.Done, DoneBy = replay.DoneBy });
    }

    /// <summary>
    /// Takes a board change set and queues it for the saving slice: it becomes one line per plan it touches; a plan
    /// the set caught up (<see cref="CaughtUp"/>) gets none. A line that opens a finished plan again (reopen) follows its
    /// finishing line: its file moves back out of the archive before the line is written, and its index entry is dropped
    /// only after, so a world that stops in between still lists the plan as finished, as its last save holds it.
    /// Constant.
    /// </summary>
    public void Record(ChangeSet set) => _incoming.Enqueue(set);

    /// <summary>
    /// Works up to <see cref="OpsPerStep"/> changes of the queued sets into plan parts or operations into lines, then
    /// writes one thing: the next plan's queued lines in one append (up to <see cref="AppendChars"/>), a part of a
    /// checkpoint (<see cref="CheckpointUnits"/> ghosts), or an archive move. Linear in those units plus the characters
    /// appended.
    /// </summary>
    public StepOutcome Step()
    {
        Build(OpsPerStep);
        if (_checkpoint is { } checkpoint)
        {
            if (checkpoint.Step(CheckpointUnits))
                _checkpoint = null;
        }
        else if (_pending.Count > 0 && !Holding())
        {
            WriteSome();
        }

        return _pending.Count == 0 && _incoming.Count == 0 && _set is null && _checkpoint is null ? StepOutcome.Done : StepOutcome.More;
    }

    /// <summary>Works everything queued into lines and writes them, in order; neighbouring lines of one plan go in one append.</summary>
    public void Flush()
    {
        Build(int.MaxValue);
        if (_checkpoint is { } checkpoint)
        {
            checkpoint.Step(int.MaxValue);
            _checkpoint = null;
        }

        var batch = new System.Text.StringBuilder();
        var plan = default(PlanId);
        while (_pending.Count > 0)
        {
            var next = Dequeue();
            if (next is Queued.Line line && (batch.Length == 0 || line.Plan == plan))
            {
                plan = line.Plan;
                batch.Append(line.Text);
                continue;
            }

            if (batch.Length > 0)
            {
                _folder.Append(plan, batch.ToString());
                batch.Clear();
            }

            if (next is Queued.Line first)
            {
                plan = first.Plan;
                batch.Append(first.Text);
            }
            else
            {
                Write(next);
            }
        }

        if (batch.Length > 0)
            _folder.Append(plan, batch.ToString());
    }

    /// <summary>The folder the world's files are in.</summary>
    public WorldFolder Folder => _folder;

    /// <summary>
    /// The game saved to <paramref name="save"/>: <see cref="BeforeGameSave"/> then <see cref="AfterGameSave"/>.
    /// </summary>
    public SavePoint OnGameSave(SaveKey save, DateTimeOffset at) => AfterGameSave(BeforeGameSave(), save, at);

    /// <summary>
    /// The game starts a save (main thread, as it reads its world): everything queued is written, every plan gets a
    /// checkpoint at its line (unless it has one), and each plan's line is taken for the save's record. Linear in the
    /// plans' ghosts.
    /// </summary>
    public ValueMap<PlanId, long> BeforeGameSave()
    {
        Flush();
        foreach (var (plan, state) in _states)
            if (state.At > 0 && !_folder.CheckpointLines(plan).Contains(state.At))
            {
                _folder.WriteCheckpoint(state);
                _sinceCheckpoint[plan] = 0;
            }

        return ValueMap<PlanId, long>.From(new Dictionary<PlanId, long>(_heads));
    }

    /// <summary>
    /// The game finished writing <paramref name="save"/>: its record remembers the lines taken when the save started.
    /// Writes one small file; safe to call off the main thread.
    /// </summary>
    public SavePoint AfterGameSave(ValueMap<PlanId, long> heads, SaveKey save, DateTimeOffset at)
    {
        var record = new SavePoint(save, at, heads);
        _folder.WriteRecord(record);
        _records.RemoveAll(r => string.Equals(r.Save.Path, save.Path, StringComparison.OrdinalIgnoreCase));
        _records.Add(record);
        return record;
    }

    // Works up to `units` changes of the queued sets into plan parts and operations into lines, set by set and plan by
    // plan, in order; taking a set counts as one, so sets with nothing for any plan file are bounded too.
    private void Build(int units)
    {
        while (units > 0)
        {
            if (_split is { } split)
            {
                units -= split.Step(units);
                if (!split.Done)
                    return;
                foreach (var part in split.Parts)
                    _parts.Enqueue(part);
                _split = null;
                continue;
            }

            if (_building is { } building)
            {
                units -= building.Line.Step(units);
                if (!building.Line.Done)
                    return;
                Built(building);
                _building = null;
                continue;
            }

            if (_set is { } set && _parts.Count > 0)
            {
                Begin(set, _parts.Dequeue());
                continue;
            }

            _set = null;
            if (_incoming.Count == 0)
                return;
            _set = _incoming.Dequeue();
            _split = new ChangeSplit(_set);
            units--;
        }
    }

    private void Begin(ChangeSet set, PlanPart part)
    {
        if (_written.Remove((set.Id, part.Plan)))
            return;
        LoadArchived(part.Plan);
        var number = TreeFor(part.Plan).NextNumber;
        var follows = _heads.TryGetValue(part.Plan, out var head) ? head : 0;
        var line = ChangeLine.For(new LineNumber(number), follows == 0 ? Option<LineNumber>.None : Option.Some(new LineNumber(follows)), set, part.Ops);
        if (!_states.TryGetValue(part.Plan, out var state))
            _states[part.Plan] = state = new PlanReplay(part.Plan);
        _building = new Building(part.Plan, new LineInParts(line, state), state, state.Finished);
    }

    private void Built(Building building)
    {
        var (plan, parts, state, wasFinished) = building;
        var number = parts.Line.Number.Value;
        var follows = parts.Line.Follows.Map(static f => f.Value).Reduce(0);
        var text = parts.End();
        TreeFor(plan).Add(new TreeLine(number, follows, text.Substring(0, text.Length - 1)));
        _heads[plan] = number;
        var reopened = wasFinished && !state.Finished;
        if (reopened)
            _pending.Enqueue(new Queued.Unarchive(plan));
        _pending.Enqueue(new Queued.Line(plan, text));
        _queuedChars += text.Length;
        if (reopened)
            _pending.Enqueue(new Queued.Unindex(plan));
        if (!wasFinished && state.Finished)
            Finishing(plan, state, number);
        _sinceCheckpoint[plan] = _sinceCheckpoint.TryGetValue(plan, out var since) ? since + 1 : 1;
        if (_sinceCheckpoint[plan] >= _checkpointEvery)
        {
            _sinceCheckpoint[plan] = 0;
            _pending.Enqueue(new Queued.Checkpoint(Copy(state)));
        }
    }

    // A finished plan's entry for the index; a plan with no record (it never opened) is archived without one.
    private void Finishing(PlanId plan, PlanReplay state, long line)
    {
        var entry = state.Record.Map(record =>
        {
            var box = state.Bounds.Reduce((default(Cell), default(Cell)));
            return new FinishedEntry(new FinishedPlan(plan, record.Name, record.Owner, state.LineAt, state.Built, box.Item1, box.Item2), line) { Record = Option.Some(record) };
        });
        _pending.Enqueue(new Queued.Archive(plan, entry));
    }

    /// <summary>
    /// A new world's first save names its folder: everything queued is written, then the files move to
    /// <paramref name="to"/> (refused when that folder already exists, so another world's files are never touched).
    /// </summary>
    public Result<Unit, Reason> MoveTo(WorldFolder to)
    {
        Flush();
        if (System.IO.Directory.Exists(to.Root))
            return Result<Unit, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, SavingReasons.Saving,
                ValueMap<string, string>.Empty.With("folder", to.Root).With("problem", "exists")));
        var parent = System.IO.Path.GetDirectoryName(to.Root);
        if (parent != null)
            System.IO.Directory.CreateDirectory(parent);
        if (System.IO.Directory.Exists(_folder.Root))
            System.IO.Directory.Move(_folder.Root, to.Root);
        _folder = to;
        _folder.NewSession();
        return Result<Unit, Reason>.Ok(Unit.Value);
    }

    private Option<SavePoint> Find(SaveKey save)
    {
        foreach (var record in _records)
            if (string.Equals(record.Save.Path, save.Path, StringComparison.OrdinalIgnoreCase) && record.Save.Fingerprint == save.Fingerprint)
                return Option.Some(record);
        SavePoint? copied = null;
        foreach (var record in _records)
            if (record.Save.Fingerprint == save.Fingerprint && (copied == null || record.At > copied.At))
                copied = record;
        return copied == null ? Option<SavePoint>.None : Option.Some(copied);
    }

    // Lines after the save's point on the newest branch, beyond the furthest point another save remembers on it.
    private void Offers(SavePoint from, List<PlanId> hidden)
    {
        foreach (var (plan, head) in from.Heads)
        {
            if (!_trees.TryGetValue(plan, out var tree))
                continue;
            var after = tree.NewestAfter(head);
            var saved = -1;
            for (var i = 0; i < after.Count; i++)
                foreach (var record in _records)
                    if (record.Heads.TryGetValue(plan, out var other) && other == after[i])
                        saved = i;
            var changes = after.Count - saved - 1;
            if (changes > 0)
                _offers.Add(new RecoveryOffer(plan, head, after[after.Count - 1], changes));
        }

        foreach (var plan in hidden)
        {
            var known = false;
            foreach (var record in _records)
                known |= record.Heads.ContainsKey(plan);
            if (!known && _trees[plan].Count > 0)
                _offers.Add(new RecoveryOffer(plan, 0, _trees[plan].Newest, _trees[plan].NewestAfter(0).Count));
        }
    }

    private PlanReplay Rebuild(PlanTree tree, long head, List<FileProblem> problems)
    {
        var path = tree.PathTo(head);
        var checkpoints = _folder.CheckpointLines(tree.Plan);
        var start = 0;
        var state = new PlanReplay(tree.Plan);
        for (var i = path.Count - 1; i >= 0; i--)
            if (checkpoints.Contains(path[i].Number) && _folder.ReadCheckpoint(tree.Plan, path[i].Number).TryGet(out var from))
            {
                state = from;
                start = i + 1;
                break;
            }

        for (var i = start; i < path.Count; i++)
        {
            var read = ChangeLineForm.Read(path[i].Text);
            if (read.TryGet(out var line))
                state.Apply(line);
            else
                problems.Add(new FileProblem.BadLine(tree.Plan, path[i].Number, read.TryGetError(out var why) ? why.GetType().Name : "unreadable"));
        }

        return state;
    }

    private BoardSeed Seed()
    {
        var plans = new List<Plan>();
        var ghosts = new List<Ghost>();
        var done = new Dictionary<PlanId, int>();
        var doneBy = new Dictionary<PlanId, ValueMap<ExecutorId, int>>();
        foreach (var (plan, state) in _states)
        {
            if (state.Finished)
                continue;
            if (state.Record.TryGet(out var record))
                plans.Add(record);
            if (state.Done > 0)
                done[plan] = state.Done;
            if (state.DoneBy.Count > 0)
                doneBy[plan] = state.DoneBy;
            foreach (var ghost in state.Ghosts)
                ghosts.Add(ghost);
        }

        plans.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        ghosts.Sort(static (a, b) => a.Id.Value.CompareTo(b.Id.Value));
        return new BoardSeed(_revision, _next, ValueList.From(plans), ValueList.From(ghosts)) { Done = ValueMap<PlanId, int>.From(done), DoneBy = ValueMap<PlanId, ValueMap<ExecutorId, int>>.From(doneBy) };
    }

    // The line a finished plan is at: the world's head for it, else its index entry's finishing line, else its newest line.
    private long FinishedHead(PlanId plan, PlanTree tree)
    {
        if (_heads.TryGetValue(plan, out var head))
            return head;
        foreach (var entry in _folder.Finished())
            if (entry.Plan.Id == plan && tree.Has(entry.Line))
                return entry.Line;
        return tree.Newest;
    }

    // A plan finished before this load sits only in the archive with no head; a new line for it (reopen) follows its finishing line.
    private void LoadArchived(PlanId plan)
    {
        if (_heads.ContainsKey(plan) || System.IO.File.Exists(_folder.PlanPath(plan)) || !System.IO.File.Exists(_folder.ArchivedPath(plan)))
            return;
        var tree = TreeFor(plan);
        var head = FinishedHead(plan, tree);
        _heads[plan] = head;
        _states[plan] = Rebuild(tree, head, new List<FileProblem>());
    }

    private static ValueMap<string, string> PlanFact(PlanId plan) =>
        ValueMap.Of("plan", plan.Value.ToString(CultureInfo.InvariantCulture));

    // An index written before it kept plan records: each entry without one gets its plan's record at the finishing
    // line, read from the plan's file, and the index is written again. An entry whose file or line is gone stays
    // without one. Linear in those plans' lines; nothing to do for an index that has every record.
    private void FillFinishedRecords()
    {
        var index = _folder.Finished();
        var filled = false;
        for (var i = 0; i < index.Count; i++)
        {
            var entry = index[i];
            if (entry.Record.IsSome)
                continue;
            var tree = _trees.TryGetValue(entry.Plan.Id, out var held) ? held : ReadTree(entry.Plan.Id);
            if (!tree.Has(entry.Line) || !Rebuild(tree, entry.Line, new List<FileProblem>()).Record.TryGet(out var record))
                continue;
            index[i] = entry with { Record = Option.Some(record) };
            filled = true;
        }

        if (filled)
            _folder.ReplaceFinished(index);
    }

    private PlanTree ReadTree(PlanId plan)
    {
        var path = System.IO.File.Exists(_folder.PlanPath(plan)) ? _folder.PlanPath(plan)
            : System.IO.File.Exists(_folder.ArchivedPath(plan)) ? _folder.ArchivedPath(plan)
            : null;
        return path == null ? PlanTree.Empty(plan) : PlanTree.Read(plan, _folder.ReadText(path)).Tree;
    }

    private PlanTree TreeFor(PlanId plan)
    {
        if (_trees.TryGetValue(plan, out var tree))
            return tree;
        var path = System.IO.File.Exists(_folder.PlanPath(plan)) ? _folder.PlanPath(plan)
            : System.IO.File.Exists(_folder.ArchivedPath(plan)) ? _folder.ArchivedPath(plan)
            : null;
        tree = path == null ? PlanTree.Empty(plan) : PlanTree.Read(plan, _folder.ReadText(path)).Tree;
        _trees[plan] = tree;
        return tree;
    }

    private static PlanReplay Copy(PlanReplay state) =>
        PlanReplay.From(state.Plan, state.At, state.Record, state.Finished, new List<Ghost>(state.Ghosts), state.Built, state.Bounds, state.Done, state.DoneBy);

    // The next queued item: a line with the lines after it of the same plan, up to AppendChars, in one append; a
    // checkpoint's first part; anything else whole.
    private void WriteSome()
    {
        var next = Dequeue();
        switch (next)
        {
            case Queued.Line line:
                if (_pending.Count == 0 || _pending.Peek() is not Queued.Line { } after || after.Plan != line.Plan || line.Text.Length + after.Text.Length > AppendChars)
                {
                    _folder.Append(line.Plan, line.Text);
                    return;
                }

                var batch = new System.Text.StringBuilder(line.Text, AppendChars);
                while (_pending.Count > 0 && _pending.Peek() is Queued.Line { } more && more.Plan == line.Plan && batch.Length + more.Text.Length <= AppendChars)
                    batch.Append(((Queued.Line)Dequeue()).Text);
                _folder.Append(line.Plan, batch.ToString());
                return;
            case Queued.Checkpoint checkpoint:
                var write = _folder.BeginCheckpoint(checkpoint.State);
                if (!write.Step(CheckpointUnits))
                    _checkpoint = write;
                return;
            default:
                Write(next);
                return;
        }
    }

    // Lines wait while sets are still being worked into lines and less than AppendChars of them is queued, so one
    // append takes many: opening the file is most of what an append costs.
    private bool Holding() =>
        _pending.Peek() is Queued.Line && (_set is not null || _incoming.Count > 0) && _queuedChars < AppendChars;

    private Queued Dequeue()
    {
        var next = _pending.Dequeue();
        if (next is Queued.Line line)
            _queuedChars -= line.Text.Length;
        return next;
    }

    private void Write(Queued pending)
    {
        switch (pending)
        {
            case Queued.Line line:
                _folder.Append(line.Plan, line.Text);
                break;
            case Queued.Checkpoint checkpoint:
                _folder.WriteCheckpoint(checkpoint.State);
                break;
            case Queued.Archive archive:
                _folder.ToArchive(archive.Plan);
                if (archive.Entry.TryGet(out var entry))
                    _folder.AddFinished(entry);
                _finished = null;
                break;
            case Queued.Unarchive unarchive:
                _folder.FromArchive(unarchive.Plan);
                break;
            case Queued.Unindex unindex:
                _folder.RemoveFinished(unindex.Plan);
                _finished = null;
                break;
            default:
                break;
        }
    }

    private void Clear()
    {
        _trees.Clear();
        _heads.Clear();
        _states.Clear();
        _sinceCheckpoint.Clear();
        _pending.Clear();
        _queuedChars = 0;
        _incoming.Clear();
        _parts.Clear();
        _set = null;
        _split = null;
        _building = null;
        _checkpoint = null;
        _records.Clear();
        _offers.Clear();
        _newest.Clear();
        _written.Clear();
        _finished = null;
    }

    /// <summary>A plan's line being worked out: its plan, the line in parts, the plan's replay, and whether the plan was finished before it.</summary>
    private sealed record Building(PlanId Plan, LineInParts Line, PlanReplay State, bool WasFinished);

    private abstract record Queued
    {
        private Queued() { }

        public sealed record Line(PlanId Plan, string Text) : Queued;

        public sealed record Checkpoint(PlanReplay State) : Queued;

        public sealed record Archive(PlanId Plan, Option<FinishedEntry> Entry) : Queued;

        public sealed record Unarchive(PlanId Plan) : Queued;

        public sealed record Unindex(PlanId Plan) : Queued;
    }

    /// <summary>The largest ghost, plan and change-set id and revision written anywhere in a file's text.</summary>
    private sealed class IdMaxima
    {
        public long Ghost { get; private set; }

        public long PlanId { get; private set; }

        public long Set { get; private set; }

        public long Revision { get; private set; }

        public void Plan(long plan) => PlanId = Math.Max(PlanId, plan);

        public void Scan(string text)
        {
            Ghost = Math.Max(Ghost, Max(text, " ghost="));
            Ghost = Math.Max(Ghost, Max(text, " by-ghost="));
            PlanId = Math.Max(PlanId, Max(text, " plan="));
            Set = Math.Max(Set, Max(text, " set="));
            Revision = Math.Max(Revision, Max(text, " rev="));
        }

        private static long Max(string text, string key)
        {
            var max = 0L;
            for (var at = text.IndexOf(key, StringComparison.Ordinal); at >= 0; at = text.IndexOf(key, at + key.Length, StringComparison.Ordinal))
            {
                var start = at + key.Length;
                var end = start;
                while (end < text.Length && text[end] >= '0' && text[end] <= '9')
                    end++;
                if (end > start && long.TryParse(text.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    max = Math.Max(max, value);
            }

            return max;
        }
    }
}
