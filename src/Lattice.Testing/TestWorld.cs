using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Lattice.Api;
using Lattice.Core.EntryPoint;
using Lattice.Core.Executors;
using Lattice.Core.Grid;
using Lattice.Core.Modules;
using Lattice.Core.Ports;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.Core.Validation;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.PlacementCheck;
using Lattice.TestKit.Saves;
using Lattice.TestKit.World;
using Lattice.Testing.Host;

namespace Lattice.Testing;

/// <summary>
/// A running world for a consumer's tests: Lattice's real Core (the same modules the game runs) over the test kit's
/// world, catalogue and placement fakes. While it runs, <see cref="System"/> is <see cref="LatticeSystem.Current"/>, so
/// code written for the game runs unchanged. One at a time per process; <see cref="Dispose"/> ends it and clears
/// <see cref="LatticeSystem.Current"/>. Main thread only: call it from one test thread.
/// </summary>
/// <remarks>
/// Nothing draws (no Unity), nothing syncs to other peers, and plans are saved in a temporary folder deleted at
/// <see cref="Dispose"/>, never in the game's folders. The game's own placement rules are not run: every placement the
/// game would be asked about passes; Lattice's own rules (support, collisions, ends, safety) run as in game.
/// </remarks>
public sealed class TestWorld : IDisposable
{
    private const int MostIdleFrames = 64;

    private readonly LatticeWorld _world;
    private readonly FakeWorld _structures;
    private readonly FakeCatalogue _catalogue;
    private readonly HandGameClock _gameClock;
    private readonly List<InlineWorker> _inline;
    private readonly bool _threads;
    private readonly string _root;
    private readonly List<string> _log;
    private long _lastId;
    private bool _ended;

    private TestWorld(LatticeWorld world, FakeWorld structures, FakeCatalogue catalogue, HandGameClock gameClock,
        List<InlineWorker> inline, bool threads, string root, long lastId, Actor viewer, List<string> log)
    {
        _log = log;
        _world = world;
        _structures = structures;
        _catalogue = catalogue;
        _gameClock = gameClock;
        _inline = inline;
        _threads = threads;
        _root = root;
        _lastId = lastId;
        Viewer = viewer;
    }

    /// <summary>The world's Lattice: the system a consumer's code is handed in game.</summary>
    public IGhostSystem System => _world;

    /// <summary>The local viewer the scenario gave.</summary>
    public Actor Viewer { get; }

    /// <summary>What Lattice logged in this world (its game log lines: modules off, faults, the load's match report), oldest first.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>How many frames have run.</summary>
    public int FrameCount { get; private set; }

    /// <summary>The game time now (it moves only by <see cref="Advance(GameTime)"/>).</summary>
    public GameTime GameTime => _gameClock.Now;

    /// <summary>
    /// Starts a world from a scenario and makes it <see cref="LatticeSystem.Current"/> (raising
    /// <see cref="LatticeSystem.Started"/>). Refused: <see cref="TestCodes.WorldRunning"/> while another world runs in
    /// this process; <see cref="ReasonCodes.InvalidValue"/> for a modded piece that does not read (the reason names it);
    /// <see cref="ReasonCodes.AlreadyRegistered"/> for a modded piece the catalogue holds already;
    /// <see cref="ReasonCodes.UnknownPiece"/> for a structure of a piece the catalogue does not hold;
    /// <see cref="ReasonCodes.NotFound"/> or <see cref="ReasonCodes.InvalidValue"/> for a save that is missing or does not read; and
    /// whatever Lattice's own start refuses with.
    /// </summary>
    public static Result<TestWorld, Reason> Start(TestScenario scenario)
    {
        if (LatticeSystem.Current.IsSome)
            return Refused(TestCodes.Refuse(TestCodes.WorldRunning, "one-world-per-process"));
        var pieces = Pieces(scenario);
        if (!pieces.TryGet(out var catalogue))
        {
            pieces.TryGetError(out var bad);
            return Refused(bad);
        }

        var world = Structures(scenario, catalogue);
        if (!world.TryGet(out var structures))
        {
            world.TryGetError(out var bad);
            return Refused(bad);
        }

        long lastId = 0;
        foreach (var standing in structures.All)
            lastId = Math.Max(lastId, standing.Ref.Id.Value);
        foreach (var standing in scenario.Structures)
        {
            if (!catalogue.Piece(standing.Piece).IsOk)
                return Refused(TestCodes.Refuse(ReasonCodes.UnknownPiece, ValueMap.Of("piece", standing.Piece.Hash.ToString(CultureInfo.InvariantCulture))));
            structures.Load(new StructureInfo(new StructureRef(new ReferenceId(++lastId), standing.Piece, standing.At), standing.Turn,
                standing.State, Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None));
        }

        var root = Path.Combine(Path.GetTempPath(), "lattice-testing-" + Guid.NewGuid().ToString("N"));
        var saving = new PlanSaving(new WorldFolder(root));
        var modules = ModuleSwitches.Decide(LatticeModules.All, new AllMembers(), WorldSeat.Host);
        var gameClock = new HandGameClock();
        var ports = new WorldPorts(catalogue, new CubeGrid(), modules, WorldSeat.Host, scenario.Viewer, gameClock)
        {
            World = Option.Some(new GamePorts(structures, new WorldScan(structures), structures, new FakePlacementCheck())),
            Saving = modules.IsOn(LatticeModules.Saving)
                ? Option.Some(new OpenedSaving(saving, saving.Open(Option<SaveKey>.None)))
                : Option<OpenedSaving>.None,
        };
        var inline = new List<InlineWorker>(2);
        var log = new List<string>(16);
        var runtime = scenario.WorkerThreads ? WorldRuntime.Threads(log.Add) : Inline(inline, log.Add);
        var started = LatticeWorld.Start(ports, runtime);
        if (!started.TryGet(out var lattice))
        {
            started.TryGetError(out var why);
            Delete(root);
            return Refused(why);
        }

        var host = new TestWorld(lattice, structures, catalogue, gameClock, inline, scenario.WorkerThreads, root, lastId, scenario.Viewer, log);
        lattice.Publish();
        return Result<TestWorld, Reason>.Ok(host);
    }

    /// <summary>
    /// One game frame, in the entry point's order: executors' lapsed claims, validation, plan state, sliced work, then
    /// the board's queued events. Inline (the default), the heavy checks the frame posted run before it returns, so
    /// their answers are taken up by the next frame. Nothing after <see cref="Dispose"/>.
    /// </summary>
    public void Frame()
    {
        if (_ended)
            return;
        _world.Frame();
        foreach (var worker in _inline)
            worker.RunAll();
        FrameCount++;
    }

    /// <summary>Runs <paramref name="count"/> frames.</summary>
    public void Frames(int count)
    {
        for (var i = 0; i < count; i++)
            Frame();
    }

    /// <summary>
    /// Runs one frame, then frames until Lattice has no work pending (no check queued or running, no sliced job running),
    /// and returns how many it ran. A question asked between frames (a preview, a check) is taken up by the first
    /// frame, so its answer is ready after this returns. Refused with <see cref="TestCodes.NotSettled"/> when work is
    /// still pending after <paramref name="maxFrames"/> frames. With <see cref="TestScenario.WithWorkerThreads"/> it waits
    /// a millisecond between frames for the threads.
    /// </summary>
    public Result<int, Reason> Settle(int maxFrames = MostIdleFrames)
    {
        for (var ran = 1; ran <= maxFrames; ran++)
        {
            Frame();
            if (Idle)
                return Result<int, Reason>.Ok(ran);
            if (_threads)
                Thread.Sleep(1);
        }

        return Result<int, Reason>.Error(TestCodes.Refuse(TestCodes.NotSettled, ValueMap
            .Of("problem", "work-pending")
            .With("frames", maxFrames.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>Moves game time on by <paramref name="by"/> seconds (leases and claims lapse by game time); no frame runs.</summary>
    public void Advance(GameTime by) => _gameClock.Advance(by.Seconds);

    /// <summary>
    /// A player's hands place a kit: a new structure of the piece at its first build step (built, for a piece of one
    /// step), raised to Lattice as the game raises a new structure. Refused with <see cref="ReasonCodes.UnknownPiece"/>
    /// for a piece the catalogue does not hold. The game's own placement rules are not checked.
    /// </summary>
    public Result<StructureRef, Reason> Build(PrefabKey piece, Cell at, Turn turn)
    {
        if (!_catalogue.Piece(piece).TryGet(out var facts))
            return Result<StructureRef, Reason>.Error(Unknown(piece));
        var structure = new StructureRef(new ReferenceId(++_lastId), piece, at);
        var state = BuildStates.At(default, Option.Some(facts));
        _structures.Register(new StructureInfo(structure, turn, state, Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None));
        return Result<StructureRef, Reason>.Ok(structure);
    }

    /// <summary>
    /// A player's hands take a structure one build step on; returns its new state (built at its last step). Refused with
    /// <see cref="ReasonCodes.NotFound"/> for a structure the world does not hold and <see cref="ReasonCodes.OutOfRange"/>
    /// for one already built or broken.
    /// </summary>
    public Result<BuildState, Reason> Advance(StructureRef structure)
    {
        if (!_structures.Structure(structure.Id).TryGet(out var info))
            return Result<BuildState, Reason>.Error(Missing(structure));
        if (info.State is not BuildState.Building building || !StepIndex.Create(building.At.Value + 1).TryGet(out var next))
            return Result<BuildState, Reason>.Error(TestCodes.Refuse(ReasonCodes.OutOfRange, "not-building"));
        _structures.ChangeStep(structure.Id, next);
        return Result<BuildState, Reason>.Ok(_structures.Structure(structure.Id).Map(static s => s.State).Reduce(info.State));
    }

    /// <summary>A player's hands take a structure away. Refused with <see cref="ReasonCodes.NotFound"/> when the world does not hold it.</summary>
    public Result<Unit, Reason> Remove(StructureRef structure)
    {
        if (_structures.Structure(structure.Id).IsNone)
            return Result<Unit, Reason>.Error(Missing(structure));
        _structures.Remove(structure.Id);
        return Result<Unit, Reason>.Ok(Unit.Value);
    }

    /// <summary>A player paints a structure. Refused with <see cref="ReasonCodes.NotFound"/> when the world does not hold it.</summary>
    public Result<Unit, Reason> Paint(StructureRef structure, PaintColour colour)
    {
        if (_structures.Structure(structure.Id).IsNone)
            return Result<Unit, Reason>.Error(Missing(structure));
        _structures.Paint(structure.Id, colour);
        return Result<Unit, Reason>.Ok(Unit.Value);
    }

    /// <summary>
    /// Ends the world: <see cref="LatticeSystem.Current"/> is cleared, queued events are raised, every module ends (its
    /// threads stopped) and BoardReset is raised; the temporary plan folder is deleted. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (_ended)
            return;
        _ended = true;
        _world.End();
        _structures.End();
        Delete(_root);
    }

    private bool Idle
    {
        get
        {
            foreach (var worker in _inline)
                if (!worker.Idle)
                    return false;
            return _world.ValidationModule is not { Busy: true } && _world.Scheduler.Idle;
        }
    }

    private static Result<TestWorld, Reason> Refused(Reason why) => Result<TestWorld, Reason>.Error(why);

    private static WorldRuntime Inline(List<InlineWorker> workers, Action<string> log)
    {
        var clock = new StepClock();
        return new WorldRuntime(clock, new FixedTime(), FrameBudgets.Default, _ => Add(workers), () => new InlineRuleCalls(clock), log);
    }

    private static InlineWorker Add(List<InlineWorker> workers)
    {
        var worker = new InlineWorker();
        workers.Add(worker);
        return worker;
    }

    // The scenario's modded pieces, each placed by its kit at its own option or the next free one, read with the same
    // reader as a catalogue file and added to the scenario's catalogue; a kit line the catalogue holds grows by them.
    private static Result<FakeCatalogue, Reason> Pieces(TestScenario scenario)
    {
        var known = scenario.Catalogue.Pieces;
        if (scenario.Pieces.Count == 0)
            return Result<FakeCatalogue, Reason>.Ok(known);
        var options = new Dictionary<KitKey, int>();
        var text = new StringBuilder(256 * scenario.Pieces.Count).Append(CatalogueFile.Header).Append('\n');
        foreach (var piece in scenario.Pieces)
        {
            if (known.Piece(piece.Key).IsOk)
                return Result<FakeCatalogue, Reason>.Error(TestCodes.Refuse(ReasonCodes.AlreadyRegistered, ValueMap.Of("piece", piece.Name)));
            if (piece.Problem().TryGet(out var problem))
                return Result<FakeCatalogue, Reason>.Error(problem);
            if (!options.TryGetValue(piece.KitKey, out var next))
                next = NextOption(known, piece.KitKey);
            var option = piece.ChosenOption.Reduce(next);
            options[piece.KitKey] = Math.Max(next, option + 1);
            text.Append(piece.Text(option)).Append('\n');
        }

        var read = CatalogueFile.Read(new StringReader(text.ToString()), "scenario-pieces.lcat");
        if (!read.TryGet(out var added))
        {
            read.TryGetError(out var error);
            return Result<FakeCatalogue, Reason>.Error(TestCatalogue.Unreadable(error));
        }

        var all = new List<NamedPiece>(known.Pieces.Count + added.Pieces.Count);
        all.AddRange(known.Pieces);
        var seen = new HashSet<PrefabKey>();
        var taken = new HashSet<KitPlace>();
        foreach (var piece in known.Pieces)
            foreach (var place in piece.Facts.Kits)
                taken.Add(place);
        var kitLines = new List<KitLine>(known.KitLines);
        foreach (var piece in added.Pieces)
        {
            if (!seen.Add(piece.Facts.Key))
                return Result<FakeCatalogue, Reason>.Error(TestCodes.Refuse(ReasonCodes.AlreadyRegistered, ValueMap.Of("piece", piece.Name)));
            for (var i = 0; i < piece.Facts.Kits.Count; i++)
            {
                var place = piece.Facts.Kits[i];
                if (!taken.Add(place) || !Grow(kitLines, piece.KitNames[i], place.Option.Index, piece.Name))
                    return Result<FakeCatalogue, Reason>.Error(OptionTaken(piece.Name, piece.KitNames[i], place.Option.Index));
            }

            all.Add(piece);
        }

        var merged = new FakeCatalogue(all, known.BrokenToolNames, kitLines);
        return merged.Gap.TryGet(out var gap)
            ? Result<FakeCatalogue, Reason>.Error(TestCodes.Refuse(ReasonCodes.InvalidValue, ValueMap.Of("problem", "kit-option-gap").With("detail", gap)))
            : Result<FakeCatalogue, Reason>.Ok(merged);
    }

    // A kit line takes a new piece only at its next option, so its options keep running from 0 without a gap.
    private static bool Grow(List<KitLine> kitLines, string kit, int option, string piece)
    {
        for (var i = 0; i < kitLines.Count; i++)
        {
            if (kitLines[i].Name != kit)
                continue;
            if (option != kitLines[i].Options.Count)
                return false;
            kitLines[i] = kitLines[i] with { Options = Words.Plus(kitLines[i].Options, piece) };
        }

        return true;
    }

    private static Reason OptionTaken(string piece, string kit, int option) => TestCodes.Refuse(ReasonCodes.InvalidValue, ValueMap
        .Of("piece", piece)
        .With("kit", kit)
        .With("option", option.ToString(CultureInfo.InvariantCulture))
        .With("problem", "option-taken-or-past-the-next"));

    // The first option after every option the catalogue's pieces and kit lines take in the kit, so a modded piece joins
    // a base kit.
    private static int NextOption(FakeCatalogue known, KitKey kit)
    {
        var next = 0;
        foreach (var line in known.KitLines)
            if (PrefabHash.Kit(line.Name) == kit)
                next = line.Options.Count;
        foreach (var piece in known.Pieces)
            foreach (var place in piece.Facts.Kits)
                if (place.Kit == kit)
                    next = Math.Max(next, place.Option.Index + 1);
        return next;
    }

    private static Result<FakeWorld, Reason> Structures(TestScenario scenario, FakeCatalogue catalogue)
    {
        if (!scenario.Save.TryGet(out var path))
            return Result<FakeWorld, Reason>.Ok(new FakeWorld(catalogue));
        if (!File.Exists(path))
            return Result<FakeWorld, Reason>.Error(TestCodes.Refuse(ReasonCodes.NotFound, ValueMap.Of("file", path)));
        var loaded = SaveLoader.Load(path, catalogue);
        if (loaded.TryGet(out var save))
            return Result<FakeWorld, Reason>.Ok(save.World);
        loaded.TryGetError(out var error);
        return Result<FakeWorld, Reason>.Error(TestCodes.Refuse(ReasonCodes.InvalidValue, ValueMap
            .Of("problem", "save-file")
            .With("file", error.File)
            .With("detail", error.Problem)));
    }

    private static Reason Unknown(PrefabKey piece) =>
        TestCodes.Refuse(ReasonCodes.UnknownPiece, ValueMap.Of("piece", piece.Hash.ToString(CultureInfo.InvariantCulture)));

    private static Reason Missing(StructureRef structure) =>
        TestCodes.Refuse(ReasonCodes.NotFound, ValueMap.Of("structure", structure.Id.Value.ToString(CultureInfo.InvariantCulture)));

    private static void Delete(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
