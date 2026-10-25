using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Analysis;
using Lattice.Core.Executors;
using Lattice.Core.Ports;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.Core.Styles;
using Lattice.Core.Sync;
using Lattice.Core.Validation;

namespace Lattice.Core.EntryPoint;

/// <summary>The running game's world, as the world bridge gives it.</summary>
internal sealed record GamePorts(IWorldView View, IWorldScan Scan, IWorldChanges Changes, IPlacementCheck Placement)
{
    /// <summary>
    /// Raises the changes the world reported since the last call; the world's frame calls it first. None for a world
    /// that raises its changes as they happen.
    /// </summary>
    public Action FlushChanges { get; init; } = static () => { };
}

/// <summary>Saving opened for this world, and the board it starts from.</summary>
internal sealed record OpenedSaving(PlanSaving Saving, OpenedPlans Opened);

/// <summary>
/// Everything a world's Lattice is built over: the catalogue and grid, the modules decided for this world, the seat,
/// the local viewer (<see cref="IGhostSystem.Board"/>'s actor), the game clock, and the ports of the modules that are on.
/// </summary>
internal sealed record WorldPorts(ICatalogue Catalogue, IGrid Grid, ModuleTable Modules, WorldSeat Seat, Actor Viewer, IGameClock GameClock)
{
    /// <summary>The world bridge's ports; none when the world module is off.</summary>
    public Option<GamePorts> World { get; init; }

    /// <summary>Saving opened for the world; none when saving is off (and always on a client).</summary>
    public Option<OpenedSaving> Saving { get; init; }

    /// <summary>The link to the other peers; none when sync is off.</summary>
    public Option<ISyncLink> Link { get; init; }

    /// <summary>The game's room rule for effects.</summary>
    public GameRooms Rooms { get; init; } = GameRooms.Default;

    /// <summary>The game's paint colours, for finish paint; none known by default (finish paint shows its fallback token).</summary>
    public IPaintColours Paints { get; init; } = new NoPaintColours();
}

/// <summary>
/// How a world's Lattice runs: its frame clock and budgets, wall time, the threads it starts (a named worker for
/// validation, rule-call threads per guard, a worker for analyzers) and its log. <see cref="Threads"/> is the game's.
/// </summary>
internal sealed record WorldRuntime(
    IClock Clock,
    ITimeSource Time,
    FrameBudgets Budgets,
    Func<string, IValidationWorker> Workers,
    Func<IRuleCalls> RuleCalls,
    Action<string> Log)
{
    /// <summary>The decided time limit of other mods' rules.</summary>
    public TimeSpan RuleLimit { get; init; } = RuleGuard.DefaultLimit;

    /// <summary>The worker analyzers run on; none runs them on validation's worker.</summary>
    public Func<IValidationWorker?> AnalyzerWorker { get; init; } = static () => null;

    /// <summary>The game's runtime: a stopwatch, the default budgets and Lattice's own named threads.</summary>
    public static WorldRuntime Threads(Action<string> log) =>
        new(new StopwatchClock(), new SystemTime(), FrameBudgets.Default, static name => new ThreadWorker(name), static () => new RuleThreads(), log)
        {
            AnalyzerWorker = static () => new ThreadWorker("Lattice analyzers"),
        };
}

/// <summary>
/// Faults of Lattice's own modules and jobs (architecture section 12, "caught per module and logged once"): the first
/// of each source is logged in full, later ones are counted. Consumer code never comes here. Main thread.
/// </summary>
internal sealed class ModuleFaults(Action<string> log)
{
    private readonly Dictionary<string, int> _counts = new();

    /// <summary>Runs a module's frame work; what it throws is reported and the frame goes on. No allocation when it returns.</summary>
    public void Run(ModuleName module, Action work)
    {
        try
        {
            work();
        }
        catch (Exception error)
        {
            Report(module.Value, error);
        }
    }

    /// <summary>Reports one fault of <paramref name="source"/>.</summary>
    public void Report(string source, Exception error)
    {
        _counts.TryGetValue(source, out var count);
        _counts[source] = count + 1;
        if (count == 0)
            log($"Lattice: {source} failed and goes on; later failures are counted. {error}");
    }

    /// <summary>How many faults <paramref name="source"/> has had.</summary>
    public int Count(string source) => _counts.TryGetValue(source, out var count) ? count : 0;

    /// <summary>Logs the counts once the world ends.</summary>
    public void LogCounts()
    {
        foreach (var source in _counts)
            if (source.Value > 1)
                log($"Lattice: {source.Key} failed {source.Value} times in this world.");
    }
}

/// <summary>Saving's slice: every board change set is recorded, and its queued lines are written in the saving budget.</summary>
internal sealed class SavingSlice(PlanSaving saving, FrameScheduler scheduler) : ISlicedWork
{
    private SlicedJob? _job;

    public void Record(ChangeSet set)
    {
        saving.Record(set);
        if (_job is { State: JobState.Running })
            return;
        _job = scheduler.Schedule(WorkKind.Saving, this, Priority.Normal).TryGet(out var job) ? job : null;
    }

    public StepOutcome Step() => saving.Step();
}
