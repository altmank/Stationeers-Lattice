using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Analysis;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Catalogue;
using Lattice.Core.Commands;
using Lattice.Core.Executors;
using Lattice.Core.FinishedPlans;
using Lattice.Core.Inspector;
using Lattice.Core.Matching;
using Lattice.Core.Openings;
using Lattice.Core.Plans;
using Lattice.Core.Ports;
using Lattice.Core.Preview;
using Lattice.Core.Routes;
using Lattice.Core.Runs;
using Lattice.Core.Safety;
using Lattice.Core.Saving;
using Lattice.Core.Selectors;
using Lattice.Core.Slices;
using Lattice.Core.Steps;
using Lattice.Core.Styles.Library;
using Lattice.Core.Styles;
using Lattice.Core.Support;
using Lattice.Core.Swaps;
using Lattice.Core.Sync;
using Lattice.Core.Transforms;
using Lattice.Core.Validation;

namespace Lattice.Core.EntryPoint;

/// <summary>
/// One world's Lattice: every module that is on, built in dependency order over the world's ports, and a stand-in that
/// answers module-missing for every module that is off. <see cref="Start"/> builds it; <see cref="Publish"/> makes it
/// <see cref="LatticeSystem.Current"/> and raises Started; <see cref="Frame"/> runs once a frame on the main thread;
/// <see cref="End"/> takes it down on every way a world ends. Lattice's failures never stop the game's world: a fault
/// while starting ends what started and leaves Lattice off for the world, a module's fault in a frame is logged once
/// and the frame goes on. Consumer code is never caught.
/// </summary>
internal sealed class LatticeWorld : IGhostSystem
{
    private readonly List<(string Name, Action Stop)> _stops = new(24);
    private readonly ModuleTable _modules;
    private readonly Action<string> _log;
    private readonly Action _lapse;
    private readonly Action _validationFrame;
    private readonly Action _planFrame;
    private readonly Action _styleFrame;
    private Action _flushChanges = static () => { };
    private CompletionWatcher? _completion;
    private ModuleName _starting;
    private WorldExecutors? _executors;
    private PlanStateModule? _planStates;
    private bool _ended;

    private LatticeWorld(WorldPorts ports, WorldRuntime runtime)
    {
        _modules = ports.Modules;
        _log = runtime.Log;
        Faults = new ModuleFaults(runtime.Log);
        Seat = ports.Seat;
        _lapse = Lapse;
        _validationFrame = ValidationFrame;
        _planFrame = PlanFrame;
        _styleFrame = StyleFrame;
    }

    /// <summary>The ghosts as the local viewer sees them: on a dedicated server Lattice's own actor (it sees everything; agents read through <see cref="IGhostBoard.As"/>), on a listen host or in single player the local player.</summary>
    public IGhostBoard Board { get; private set; } = null!;

    public IWorldQuery World { get; private set; } = null!;

    public IPlans Plans { get; private set; } = null!;

    public ICommands Commands { get; private set; } = null!;

    public IPreview Preview { get; private set; } = null!;

    public IValidation Validation { get; private set; } = null!;

    public ISteps Steps { get; private set; } = null!;

    public IBills Bills { get; private set; } = null!;

    public ISwaps Swaps { get; private set; } = null!;

    public IAnalysis Analysis { get; private set; } = null!;

    public IExecutors Executors { get; private set; } = null!;

    public IRegistries Registries { get; private set; } = null!;

    public IInspector Inspector { get; private set; } = null!;

    public IGhostEvents Events => BoardEvents;

    public ISlices Slices { get; private set; } = null!;

    /// <inheritdoc />
    public IGhostStyles Styles { get; private set; } = new NoStyles();

    /// <inheritdoc />
    public IPieceCatalogue Catalogue { get; private set; } = null!;

    public ApiVersion Version => ApiVersion.Current;

    public ValueList<ModuleStatus> Modules => _modules.Statuses;

    public WorldSeat Seat { get; }

    internal ModuleFaults Faults { get; }

    internal GhostBoard Ghosts { get; private set; } = null!;

    internal BoardEvents BoardEvents { get; private set; } = null!;

    internal FrameScheduler Scheduler { get; private set; } = null!;

    /// <summary>The family each piece is drawn and filtered by: a mod's registered one, else the catalogue's by type.</summary>
    internal IPieceFamilies Families { get; private set; } = null!;

    internal CommandService LocalCommands { get; private set; } = null!;

    internal ValidationService? ValidationModule { get; private set; }

    internal HostSync? Host { get; private set; }

    /// <summary>Style resolution, when module <c>styles</c> is on.</summary>
    internal GhostStyles? StyleModule { get; private set; }

    internal ClientSync? Client { get; private set; }

    internal Option<MatchReport> Match { get; private set; }

    internal bool Ended => _ended;

    public bool Has(IntentKind kind) => kind is IntentKind.Build or IntentKind.Remove or IntentKind.Swap or IntentKind.FinishOnly;

    /// <summary>
    /// Builds the world's Lattice. Refused when the catalogue is off (the kernel needs it) or when starting a module
    /// throws: what started is ended in reverse order and the reason names the module. Logs one line per module that is
    /// off and the load's match report.
    /// </summary>
    public static Result<LatticeWorld, Reason> Start(WorldPorts ports, WorldRuntime runtime)
    {
        foreach (var status in ports.Modules.Statuses)
            if (status.State is ModuleState.Off off)
                runtime.Log($"Lattice: module {status.Name.Value} is off: {off.Why}");
        if (ports.Modules.Why(LatticeModules.Catalogue).TryGet(out var noCatalogue))
        {
            runtime.Log("Lattice does not start for this world: the catalogue cannot be read.");
            return Result<LatticeWorld, Reason>.Error(noCatalogue);
        }

        var world = new LatticeWorld(ports, runtime);
        try
        {
            world.Compose(ports, runtime);
            return Result<LatticeWorld, Reason>.Ok(world);
        }
        catch (Exception error)
        {
            runtime.Log($"Lattice does not start for this world: starting {world._starting.Value} failed. {error}");
            world.End();
            return Result<LatticeWorld, Reason>.Error(Reason.Of(ReasonCodes.ModuleMissing, EntryCodes.Rule, ValueMap
                .Of("module", world._starting.Value)
                .With("problem", "start-failed")
                .With("error", error.GetType().Name + ": " + error.Message)));
        }
    }

    /// <summary>
    /// Makes this world <see cref="LatticeSystem.Current"/> and raises Started; every Lattice module is complete by
    /// then. A Started handler's exception leaves this call and the world keeps running.
    /// </summary>
    public void Publish()
    {
        if (!_ended)
            LatticeSystem.Publish(this);
    }

    /// <summary>
    /// One frame, main thread: the world's changes reported since the last frame raised (the mirror follows them,
    /// completion notes the ghosts they touch), executors' lapsed claims, validation's frame, plan state's frame (each
    /// caught per module), the scheduler's slices (Lattice's jobs caught per job), the board's queued events, the styles' own
    /// frame (caught), then the styles' consumer callbacks last. Consumer code in the slices, event handlers or style
    /// callbacks is not caught: its exception leaves the frame after the events were raised. An idle frame allocates
    /// nothing.
    /// </summary>
    public void Frame()
    {
        if (_ended)
            return;
        Faults.Run(LatticeModules.World, _flushChanges);
        Faults.Run(LatticeModules.Executors, _lapse);
        Faults.Run(LatticeModules.Validation, _validationFrame);
        Faults.Run(LatticeModules.Plans, _planFrame);
        try
        {
            Scheduler.RunFrame();
        }
        finally
        {
            BoardEvents.Flush();
        }

        Faults.Run(LatticeModules.Styles, _styleFrame);
        StyleModule?.RaiseChanges();
    }

    /// <summary>
    /// Brings the board up to the world, for a save about to be written: the world's reported changes raised, every
    /// ghost completion has waiting judged, then the board's queued events raised (saving records them). Main thread;
    /// linear in what waits. Does nothing once the world has ended.
    /// </summary>
    internal void CatchUp()
    {
        if (_ended)
            return;
        Faults.Run(LatticeModules.World, _flushChanges);
        Faults.Run(LatticeModules.Matching, CatchUpCompletion);
        BoardEvents.Flush();
    }

    /// <summary>
    /// Ends the world's Lattice; safe to call more than once and after a failed start. <see cref="LatticeSystem.Current"/>
    /// is cleared first, the ghosts completion has waiting are judged and queued events are raised (saving records
    /// them); changes the world reported and not yet raised are dropped (a world ending by a load takes every structure
    /// away). then every module ends in reverse order, each
    /// on its own so one fault skips nothing, and BoardReset is raised last for consumers holding this stale system.
    /// </summary>
    public void End()
    {
        if (_ended)
            return;
        _ended = true;
        if (LatticeSystem.Current.TryGet(out var current) && ReferenceEquals(current, this))
            LatticeSystem.Clear();
        try
        {
            Faults.Run(LatticeModules.Matching, CatchUpCompletion);
            BoardEvents?.Flush();
        }
        finally
        {
            for (var i = _stops.Count - 1; i >= 0; i--)
            {
                try
                {
                    _stops[i].Stop();
                }
                catch (Exception error)
                {
                    _log($"Lattice: ending {_stops[i].Name} failed. {error}");
                }
            }

            _stops.Clear();
            Faults.LogCounts();
            if (BoardEvents is { } events)
            {
                events.Queue(new BoardResetEvent());
                events.Flush();
            }
        }
    }

    private void Compose(WorldPorts ports, WorldRuntime runtime)
    {
        var catalogue = ports.Catalogue;
        var grid = ports.Grid;
        var on = ports.Modules;
        var host = ports.Seat == WorldSeat.Host;
        var game = ports.World.TryGet(out var bridge) ? bridge : null;
        if (game is not null)
            _flushChanges = game.FlushChanges;
        var view = game?.View ?? new NoWorld();
        OpenedSaving? saved = on.IsOn(LatticeModules.Saving) && host && ports.Saving.TryGet(out var opened) ? opened : null;

        _starting = LatticeModules.Catalogue;
        var shapes = new Shapes(catalogue, grid);
        var policies = new AccessPolicies();
        var sizeLimit = new PlanSizeLimit();
        BoardEvents = new BoardEvents();
        Ghosts = new GhostBoard(shapes, runtime.Time, policies, sizeLimit, BoardEvents,
            saved is null ? Option<BoardSeed>.None : Option.Some(saved.Opened.Seed));
        Scheduler = new FrameScheduler(runtime.Clock, runtime.Budgets, (work, error) => Faults.Report(work.GetType().Name, error));
        Slices = new SliceService(Scheduler);
        var expanders = new CommandExpanders();
        var attachments = new AttachmentLimits();
        var additions = new CatalogueAdditions(catalogue);
        Families = additions;
        Catalogue = new PieceCatalogue(catalogue.Listing, additions, new PiecePorts(catalogue, shapes), new Lattice.Core.Styles.Facings(shapes));
        var placements = new Placements(catalogue, grid, additions);
        var book = new RunReportBook(Ghosts);
        var supportFacts = new CatalogueSupportFacts(catalogue);
        var openings = new CatalogueOpeningFacts(catalogue);
        Board = Ghosts.ViewFor(ports.Viewer);
        Plans = new PlansView(Ghosts, ports.Viewer);

        if (saved is not null)
        {
            _starting = LatticeModules.Saving;
            Ghosts.InstallSaving(saved.Saving);
            Action<ChangeSet> record = new SavingSlice(saved.Saving, Scheduler).Record;
            BoardEvents.Changed += record;
            Stop(LatticeModules.Saving, () =>
            {
                BoardEvents.Changed -= record;
                saved.Saving.Flush();
            });
        }

        WorldMirror? mirror = null;
        if (on.IsOn(LatticeModules.Mirror) && game is not null)
        {
            _starting = LatticeModules.Mirror;
            mirror = new WorldMirror(shapes, view);
            mirror.Start(game.Changes, game.Scan);
            Stop(LatticeModules.Mirror, mirror.End);
        }

        if (on.IsOn(LatticeModules.Matching) && mirror is not null)
        {
            _starting = LatticeModules.Matching;
            var watcher = new CompletionWatcher(Ghosts, mirror, new TurnFreePieces(catalogue, grid, shapes), runtime.Time, Scheduler);
            var report = watcher.Load();
            _completion = watcher;
            Stop(LatticeModules.Matching, watcher.End);
            Match = Option.Some(report);
            runtime.Log($"Lattice: load matched {report.Judged} ghosts: {report.Done} done, {report.Started} started, {report.Stale} stale, {report.TargetGone} target gone, {report.Unjudged} not judged; {report.TurnFree.Count} turn-free pieces.");
        }

        StructureSelection? selection = null;
        if (on.IsOn(LatticeModules.Selectors) && mirror is not null)
        {
            _starting = LatticeModules.Selectors;
            Ghosts.InstallSelectors(new GhostSelectors(shapes, grid, mirror, view));
            selection = new StructureSelection(shapes, grid, mirror, view);
        }

        World = new WorldQuery(view, selection, on.Missing(LatticeModules.Selectors));
        _starting = LatticeModules.Catalogue;
        LocalCommands = new CommandService(Ghosts, expanders, World, new ClientChecks(CommandLimits.Default, catalogue, attachments), Scheduler);

        if (on.IsOn(LatticeModules.Runs) && game is not null)
        {
            _starting = LatticeModules.Runs;
            Must(expanders.Register(new RunExpander(new RunPlanner(catalogue, grid, game.Placement), book).Descriptor));
            Must(expanders.Register(new SplitExpander(catalogue, grid).Descriptor));
        }

        if (on.IsOn(LatticeModules.Routes) && game is not null)
        {
            _starting = LatticeModules.Routes;
            Must(expanders.RegisterSliced(new RouteExpander(new RoutePlanner(
                catalogue, grid, game.Placement, openings, supportFacts, OpeningBands.Default, RouteLimits.Default), book).Descriptor));
        }

        SwapPlanner? swaps = null;
        if (on.IsOn(LatticeModules.Swaps))
        {
            _starting = LatticeModules.Swaps;
            swaps = new SwapPlanner(catalogue, shapes, view);
            Must(expanders.RegisterSliced(new SwapExpander(swaps).Descriptor));
        }

        if (on.IsOn(LatticeModules.Transforms))
        {
            _starting = LatticeModules.Transforms;
            Must(expanders.RegisterSliced(new TransformExpander(placements).Descriptor));
        }

        if (on.IsOn(LatticeModules.Templates))
        {
            _starting = LatticeModules.Templates;
            Must(expanders.RegisterSliced(new TemplateExpander(placements).Descriptor));
        }

        if (on.IsOn(LatticeModules.FinishedPlans) && mirror is not null && saved is not null)
        {
            _starting = LatticeModules.FinishedPlans;
            var finished = new FinishedPlansExpander(Ghosts, mirror, shapes);
            Must(expanders.RegisterSliced(finished.Repair));
            Must(expanders.RegisterSliced(finished.TakeDown));
        }

        var analyzing = on.IsOn(LatticeModules.Analysis) || on.IsOn(LatticeModules.Preview);
        Analyzers? analyzers = null;
        if (analyzing)
        {
            analyzers = new Analyzers();
            Stop(LatticeModules.Analysis, analyzers.End);
        }

        ValidationRules? rules = null;
        SupportRules? supportRules = null;
        if (on.IsOn(LatticeModules.Validation) && mirror is not null && game is not null)
        {
            _starting = LatticeModules.Validation;
            var main = new InlineRuleCalls(runtime.Clock);
            supportRules = new SupportRules(new RuleGuard(runtime.RuleLimit, RuleCalls(runtime), main));
            rules = new ValidationRules(new RuleGuard(runtime.RuleLimit, RuleCalls(runtime), main));
            if (swaps is not null)
                Must(rules.Register(new SwapFaceRule(swaps).Descriptor));
            var judge = new SupportJudge(supportRules, new SupportGeometry(grid, supportFacts, shapes));
            var validation = new ValidationService(new ValidationParts(
                Ghosts, LatticeIdentity.Actor, mirror, view, game.Placement, catalogue, grid, judge, rules, Scheduler, runtime.Clock,
                runtime.Workers("Lattice validation"))
            {
                Analyzers = analyzers ?? new Analyzers(),
                AnalyzerWorker = runtime.AnalyzerWorker(),
            });
            var rulesOf = rules;
            Stop(LatticeModules.Validation, () =>
            {
                validation.End();
                rulesOf.End();
            });
            validation.Start();
            ValidationModule = validation;
            Validation = validation;
        }
        else
        {
            Validation = new OffModule(on.Missing(LatticeModules.Validation));
        }

        StepPlanner? steps = null;
        BillMaker? bills = null;
        if (on.IsOn(LatticeModules.Steps) && mirror is not null)
        {
            _starting = LatticeModules.Steps;
            steps = new StepPlanner(Ghosts, mirror, catalogue, shapes, ValidationModule is null ? new NoOrder() : new ValidationOrder(ValidationModule));
            bills = new BillMaker(Ghosts, steps, mirror);
            Steps = steps;
            Bills = bills;
        }
        else
        {
            Steps = new OffModule(on.Missing(LatticeModules.Steps));
            Bills = new OffModule(on.Missing(LatticeModules.Steps));
        }

        BuildSafety? safety = null;
        if (on.IsOn(LatticeModules.Safety))
            safety = new BuildSafety(catalogue, grid, new CatalogueSafetyFacts(catalogue, view), openings, OpeningBands.Default, SafetyLimits.Default);

        if (on.IsOn(LatticeModules.Plans) && ValidationModule is not null && bills is not null && safety is not null && mirror is not null)
        {
            _starting = LatticeModules.Plans;
            var planStates = new PlanStateModule(new PlanStateParts(
                Ghosts, BoardEvents, mirror, ValidationModule, bills, new BoardSafety(safety, Ghosts.ViewFor(LatticeIdentity.Actor), view), Scheduler));
            Stop(LatticeModules.Plans, planStates.End);
            planStates.Start();
            _planStates = planStates;
        }

        var effects = analyzing ? new PlanEffects(catalogue, grid, ports.Rooms) : null;
        if (on.IsOn(LatticeModules.Analysis) && mirror is not null && effects is not null && analyzers is not null)
        {
            _starting = LatticeModules.Analysis;
            var analysis = new AnalysisService(new AnalysisParts(Ghosts, ports.Viewer, view, mirror, effects, analyzers, ValidationModule));
            Stop(LatticeModules.Analysis, analysis.End);
            Analysis = analysis;
        }
        else
        {
            Analysis = new OffModule(on.Missing(LatticeModules.Analysis));
        }

        if (on.IsOn(LatticeModules.Preview) && mirror is not null && steps is not null && safety is not null && effects is not null && analyzers is not null)
        {
            _starting = LatticeModules.Preview;
            var preview = new PreviewService(new PreviewParts(
                Ghosts, LocalCommands, view, mirror, steps, effects, safety, analyzers, book, Scheduler, ValidationModule));
            Stop(LatticeModules.Preview, preview.End);
            Preview = preview;
        }
        else
        {
            Preview = new OffModule(on.Missing(LatticeModules.Preview));
        }

        Swaps = swaps is null ? new OffModule(on.Missing(LatticeModules.Swaps)) : swaps;

        if (on.IsOn(LatticeModules.Executors))
        {
            _starting = LatticeModules.Executors;
            var executors = new WorldExecutors(Ghosts, ports.GameClock, ports.Seat);
            Stop(LatticeModules.Executors, executors.Dispose);
            _executors = executors;
            Executors = executors;
        }
        else
        {
            Executors = new OffModule(on.Missing(LatticeModules.Executors));
        }

        Inspector = on.IsOn(LatticeModules.Inspector) && saved is not null
            ? new PlanInspector(new SavedPlanFiles(saved.Saving))
            : new OffModule(on.Missing(LatticeModules.Inspector));

        Commands = LocalCommands;
        _starting = LatticeModules.Sync;
        var link = on.IsOn(LatticeModules.Sync) && ports.Link.TryGet(out var l) ? l : null;
        Action<Reason> reportSync = why => runtime.Log($"Lattice sync: {why}");
        if (host && link is not null)
        {
            var hostSync = new HostSync(LocalCommands, Ghosts, Scheduler, link, SyncLimits.Default, reportSync);
            Stop(LatticeModules.Sync, hostSync.Dispose);
            Host = hostSync;
        }
        else if (!host)
        {
            if (link is null)
            {
                Commands = new OffModule(on.Missing(LatticeModules.Sync));
            }
            else
            {
                Client = new ClientSync(Ghosts, link, SyncLimits.Default, reportSync);
                Commands = Client;
            }
        }

        if (on.IsOn(LatticeModules.Styles))
        {
            _starting = LatticeModules.Styles;
            var styles = new GhostStyles(new StyleParts(Ghosts, ports.Viewer, shapes, grid, Scheduler, runtime.Clock, new CataloguePieceLooks(catalogue, additions))
            {
                Verdicts = ValidationModule,
                Statuses = _executors,
                World = World,
                Drawing = on.IsOn(LatticeModules.Render),
                Library = new StarterLibrary(),
                Paints = ports.Paints,
            });
            Stop(LatticeModules.Styles, styles.End);
            StyleModule = styles;
            Styles = styles;
        }

        Registries = new WorldRegistries(
            rules, supportRules, analyzers, attachments, additions, expanders, policies, sizeLimit,
            on.Missing(LatticeModules.Validation), on.Missing(LatticeModules.Analysis));
        _starting = default;
    }

    private void Stop(ModuleName module, Action stop) => _stops.Add((module.Value, stop));

    private IRuleCalls RuleCalls(WorldRuntime runtime)
    {
        var calls = runtime.RuleCalls();
        if (calls is IDisposable threads)
            Stop(LatticeModules.Validation, threads.Dispose);
        return calls;
    }

    private static void Must<T>(Result<T, Reason> registered) where T : notnull
    {
        if (registered.TryGetError(out var why))
            throw new InvalidOperationException("Lattice's own registration was refused: " + why);
    }

    private void Lapse() => _executors?.Lapse();

    private void ValidationFrame() => ValidationModule?.Frame();

    private void PlanFrame() => _planStates?.Frame();

    private void StyleFrame() => StyleModule?.OwnFrame();

    private void CatchUpCompletion() => _completion?.CatchUp();
}
