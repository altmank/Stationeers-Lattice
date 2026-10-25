using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Support;

namespace Lattice.Core.Validation;

/// <summary>What the main thread hands the worker: board and world changes, and answers to what the worker asked.</summary>
internal abstract record WorkInput
{
    private WorkInput() { }

    /// <summary>Ghosts added or changed, and ghosts gone, as of a board revision; with <paramref name="Reset"/>, the whole board.</summary>
    public sealed record Ghosts(Revision At, ValueList<Ghost> Upserts, ValueList<GhostId> Gone, bool Reset) : WorkInput;

    /// <summary>
    /// Changes the world mirror applied, in order. The main thread fills the list and reuses it once the worker hands
    /// the inputs back; the worker only reads it.
    /// </summary>
    public sealed record World(List<MirrorChange> Changes) : WorkInput;

    /// <summary>The game's placement check answered.</summary>
    public sealed record GameAnswered(Placement At, GameAnswer Answer) : WorkInput;

    /// <summary>
    /// The game's placement check answered, for each placement in order. The main thread fills the list and reuses
    /// it once the worker hands the inputs back; the worker only reads it.
    /// </summary>
    public sealed record GameAnswers(List<(Placement At, GameAnswer Answer)> Answers) : WorkInput;

    /// <summary>The room a cell is in.</summary>
    public sealed record RoomAnswered(Cell At, Option<RoomRef> Room) : WorkInput;

    /// <summary>The network a structure is on.</summary>
    public sealed record NetworkAnswered(ReferenceId Of, Option<NetworkRef> Network) : WorkInput;

    /// <summary>A main-thread rule answered for a ghost, as asked at the ghost's epoch.</summary>
    public sealed record MainAnswered(RuleId Rule, GhostId Ghost, int Epoch, object Answer) : WorkInput
    {
        /// <summary>Asked for a trial's ghost: it holds for that trial only, whatever the ghost's epoch.</summary>
        public bool Trial { get; init; }
    }

    /// <summary>
    /// Main-thread facts that may have changed (a world change near them): they are asked again. The main thread fills
    /// the list and reuses it once the worker hands the inputs back; the worker only reads it.
    /// </summary>
    public sealed record Dropped(List<ReadKey> Keys) : WorkInput;

    /// <summary>Rules were registered, disposed or switched off.</summary>
    public sealed record RulesChanged : WorkInput;
}

/// <summary>A ghost's verdict (its first finding that is not a warning) before and after a re-check.</summary>
/// <param name="Ghost">The ghost.</param>
/// <param name="Before">The verdict before; none for a ghost not judged before.</param>
/// <param name="After">The verdict now; none for a ghost that left the board.</param>
internal sealed record VerdictChange(GhostId Ghost, Option<Finding> Before, Option<Finding> After);

/// <summary>How much a run did: judgements made (cached ones not counted), the ghosts they were for, and reader-index entries visited.</summary>
internal sealed record RunCounts(int Ghosts, int Judged, int Visited, ValueSet<GhostId> Rechecked);

/// <summary>
/// Every finding at one board revision, in build order, with each ghost's place, so a selection is found by ghost
/// rather than by scanning, and the ghosts the build order says join two or more pipe networks. Never changed after
/// it is made.
/// </summary>
internal sealed class ValidationResult(
    Revision at,
    ValueList<Finding> findings,
    Dictionary<GhostId, (int Start, int Count, int Order)> places,
    ValueSet<GhostId> pipeJoins)
{
    public Revision At => at;

    public ValueList<Finding> Findings => findings;

    /// <summary>The ghosts that join two or more pipe networks; their steps carry <see cref="Constraint.OnePipeJoinPerTick"/>.</summary>
    public ValueSet<GhostId> PipeJoins => pipeJoins;

    public bool TryPlace(GhostId ghost, out (int Start, int Count, int Order) place) => places.TryGetValue(ghost, out place);
}

/// <summary>What a worker run gives back.</summary>
internal abstract record RunOutcome
{
    private RunOutcome(long sequence) => Sequence = sequence;

    /// <summary>The run's number; later runs have higher numbers.</summary>
    public long Sequence { get; }

    /// <summary>The main-thread facts no ghost reads any longer, dropped from the worker's store.</summary>
    public ValueList<ReadKey> Unread { get; init; } = ValueList<ReadKey>.Empty;

    /// <summary>Every ghost judged with all it needed.</summary>
    public sealed record Ready(long Sequence, ValidationResult Result, bool Changed, ValueList<VerdictChange> Verdicts, RunCounts Counts) : RunOutcome(Sequence);

    /// <summary>Main-thread facts or answers are missing; ask them, then run again. <paramref name="MainAsks"/>: some are mods' main-thread rules, which read the worker's views, so no run starts until they are answered.</summary>
    public sealed record Pending(long Sequence, Revision At, ValueList<Ask> Asks, bool MainAsks, RunCounts Counts) : RunOutcome(Sequence);

    /// <summary>The run threw: a Lattice bug.</summary>
    public sealed record Faulted(long Sequence, Exception Error) : RunOutcome(Sequence);
}

/// <summary>What validating a change not on the board gives back.</summary>
internal abstract record TrialOutcome
{
    private TrialOutcome() { }

    /// <summary>The findings of the change's added and changed ghosts, in build order.</summary>
    public sealed record Ready(ValueList<Finding> Findings) : TrialOutcome
    {
        /// <summary>The mods' worker analyzers' facts, in registration order.</summary>
        public ValueList<AnalyzerFact> Facts { get; init; } = ValueList<AnalyzerFact>.Empty;

        /// <summary>The worker analyzers are still running on their own thread; their facts follow as <see cref="Analyzed"/>.</summary>
        public bool Analyzing { get; init; }
    }

    /// <summary>The worker analyzers' facts for a trial already judged, in registration order.</summary>
    public sealed record Analyzed(ValueList<AnalyzerFact> Facts) : TrialOutcome;

    /// <summary>A mod's worker analyzer threw; the exception leaves the trial on the main thread.</summary>
    public sealed record Threw(Exception Error) : TrialOutcome;

    /// <summary>Main-thread facts or answers are missing; ask them, then try again. <paramref name="MainAsks"/> as in <see cref="RunOutcome.Pending"/>.</summary>
    public sealed record Pending(ValueList<Ask> Asks, bool MainAsks) : TrialOutcome;
}

/// <summary>The world a trial judged on, with every ghost done; none when the trial ran no ordering.</summary>
internal sealed record TrialWorld(AssumedIndex? Index);

/// <summary>
/// The main-thread facts the worker has been given (game answers, rooms, networks, main-thread rules' answers), and
/// each ghost's epoch: a main-thread rule's answer counts only for the epoch it was asked at, and a ghost's epoch rises
/// whenever anything it read changes. Worker only.
/// </summary>
internal sealed class FactStore
{
    private readonly Dictionary<Placement, GameAnswer> _games = [];
    private readonly Dictionary<Cell, Option<RoomRef>> _rooms = [];
    private readonly Dictionary<ReferenceId, Option<NetworkRef>> _networks = [];
    private readonly Dictionary<(RuleId, GhostId), (int Epoch, object Answer)> _main = [];
    private readonly Dictionary<GhostId, int> _epochs = [];
    private readonly Dictionary<(RuleId, GhostId), object> _trialMain = [];
    private object? _trialFor;
    private bool _trying;

    public int EpochOf(GhostId ghost) => _epochs.TryGetValue(ghost, out var epoch) ? epoch : 0;

    /// <summary>
    /// A trial starts: main-thread answers given for trial ghosts count while it runs. They are kept while the same
    /// trial (by reference) is tried again, and dropped when another one starts.
    /// </summary>
    public void BeginTrial(object trial)
    {
        if (!ReferenceEquals(_trialFor, trial))
            _trialMain.Clear();
        _trialFor = trial;
        _trying = true;
    }

    public void EndTrial() => _trying = false;

    public void Renew(GhostId ghost) => _epochs[ghost] = EpochOf(ghost) + 1;

    public void Forget(GhostId ghost) => _epochs.Remove(ghost);

    public Option<GameAnswer> Game(Placement at) => _games.TryGetValue(at, out var answer) ? Option.Some(answer) : Option<GameAnswer>.None;

    /// <summary>The room and network answers held now, copied for a <see cref="SnapshotFacts"/>.</summary>
    public SnapshotFacts Snapshot(WorldCopy world) =>
        new(world.Freeze(), new Dictionary<Cell, Option<RoomRef>>(_rooms), new Dictionary<ReferenceId, Option<NetworkRef>>(_networks));

    public Option<Option<RoomRef>> Room(Cell at) => _rooms.TryGetValue(at, out var room) ? Option.Some(room) : Option<Option<RoomRef>>.None;

    public Option<Option<NetworkRef>> Network(ReferenceId of) =>
        _networks.TryGetValue(of, out var network) ? Option.Some(network) : Option<Option<NetworkRef>>.None;

    public Option<T> Main<T>(RuleId rule, GhostId ghost) where T : class
    {
        if (_trying && _trialMain.TryGetValue((rule, ghost), out var tried) && tried is T trialAnswer)
            return Option.Some(trialAnswer);
        return _main.TryGetValue((rule, ghost), out var held) && held.Epoch == EpochOf(ghost) && held.Answer is T answer
            ? Option.Some(answer)
            : Option<T>.None;
    }

    /// <summary>Takes in one game answer; gives the key it changed, or none when it changed nothing.</summary>
    public Option<ReadKey> TakeGame(Placement at, GameAnswer answer)
    {
        if (_games.TryGetValue(at, out var known) && known == answer)
            return Option<ReadKey>.None;
        _games[at] = answer;
        return Option.Some(ReadKey.Game(at));
    }

    /// <summary>Drops a game, room or network answer; whether one was held.</summary>
    public bool Drop(ReadKey key) => key.Kind switch
    {
        ReadKind.Game => _games.Remove(key.Placement!),
        ReadKind.Room => _rooms.Remove(key.Cell),
        ReadKind.Network => _networks.Remove(new ReferenceId(key.Id)),
        _ => false,
    };

    /// <summary>Takes in one answer; gives the key it changed, or none when it changed nothing.</summary>
    public Option<ReadKey> Take(WorkInput input)
    {
        switch (input)
        {
            case WorkInput.GameAnswered game:
                return TakeGame(game.At, game.Answer);
            case WorkInput.RoomAnswered room:
                if (_rooms.TryGetValue(room.At, out var held) && held == room.Room)
                    return Option<ReadKey>.None;
                _rooms[room.At] = room.Room;
                return Option.Some(ReadKey.RoomAt(room.At));
            case WorkInput.NetworkAnswered network:
                if (_networks.TryGetValue(network.Of, out var on) && on == network.Network)
                    return Option<ReadKey>.None;
                _networks[network.Of] = network.Network;
                return Option.Some(ReadKey.NetworkOf(network.Of));
            case WorkInput.MainAnswered { Trial: true } trial:
                _trialMain[(trial.Rule, trial.Ghost)] = trial.Answer;
                return Option<ReadKey>.None;
            case WorkInput.MainAnswered main:
                if (main.Epoch != EpochOf(main.Ghost))
                    return Option<ReadKey>.None;
                _main[(main.Rule, main.Ghost)] = (main.Epoch, main.Answer);
                return Option.Some(ReadKey.MainRule(main.Rule, main.Ghost));
            default:
                return Option<ReadKey>.None;
        }
    }
}

/// <summary>
/// Facts frozen at one moment for reads on another thread (worker analyzers): the world copy and the room and network
/// answers as they were. No game answers or main-thread rule answers: those are for judging ghosts. Read-only, so
/// safe on any thread.
/// </summary>
internal sealed class SnapshotFacts(
    WorldCopy world,
    Dictionary<Cell, Option<RoomRef>> rooms,
    Dictionary<ReferenceId, Option<NetworkRef>> networks) : IJudgeFacts
{
    public Option<StructureInfo> Structure(ReferenceId id) => world.Structure(id);

    public ValueList<StructureInfo> At(Cell cell) => world.At(cell);

    public Option<Option<NetworkRef>> NetworkOf(ReferenceId id) =>
        networks.TryGetValue(id, out var network) ? Option.Some(network) : Option<Option<NetworkRef>>.None;

    public Option<Option<RoomRef>> RoomAt(Cell cell) =>
        rooms.TryGetValue(cell, out var room) ? Option.Some(room) : Option<Option<RoomRef>>.None;

    public Option<GameAnswer> Game(Placement placement) => Option<GameAnswer>.None;

    public Option<Guarded<SupportVerdict>> MainSupport(SupportRules rules, SupportRules.Entry entry, GhostId ghost, SupportInput input) =>
        Option<Guarded<SupportVerdict>>.None;

    public Option<Guarded<RuleVerdict>> MainRule(ValidationRules rules, ValidationRules.Entry entry, RuleInput input) =>
        Option<Guarded<RuleVerdict>>.None;

    public int EpochOf(GhostId ghost) => 0;
}

/// <summary>Judging facts on the worker: the world copy, and the main-thread facts given so far.</summary>
internal sealed class WorkerFacts(WorldCopy world, FactStore store) : IJudgeFacts
{
    public Option<StructureInfo> Structure(ReferenceId id) => world.Structure(id);

    public ValueList<StructureInfo> At(Cell cell) => world.At(cell);

    public Option<Option<NetworkRef>> NetworkOf(ReferenceId id) => store.Network(id);

    public Option<Option<RoomRef>> RoomAt(Cell cell) => store.Room(cell);

    public Option<GameAnswer> Game(Placement placement) => store.Game(placement);

    public Option<Guarded<SupportVerdict>> MainSupport(SupportRules rules, SupportRules.Entry entry, GhostId ghost, SupportInput input) =>
        store.Main<Guarded<SupportVerdict>>(entry.Rule.Id, ghost);

    public Option<Guarded<RuleVerdict>> MainRule(ValidationRules rules, ValidationRules.Entry entry, RuleInput input) =>
        store.Main<Guarded<RuleVerdict>>(entry.Rule.Id, input.Ghost.Id);

    public int EpochOf(GhostId ghost) => store.EpochOf(ghost);
}
