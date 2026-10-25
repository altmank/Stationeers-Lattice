using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.EntryPoint;
using Lattice.Core.Modules;
using Lattice.Core.Ports;
using Lattice.Core.Slices;
using Lattice.Core.Validation;
using Lattice.TestKit.World;

namespace Lattice.Testing.Host;

/// <summary>A worker whose posted work runs when the host's frame runs it, on the test's thread.</summary>
internal sealed class InlineWorker : IValidationWorker
{
    private readonly Queue<Action> _work = new(16);

    public bool Idle => _work.Count == 0;

    public void Post(Action work) => _work.Enqueue(work);

    public void Stop(TimeSpan join) => _work.Clear();

    public void RunAll()
    {
        while (_work.Count > 0)
            _work.Dequeue()();
    }
}

/// <summary>
/// The frame clock of an inline world: every read moves it on by one tick, so a frame's slices end within their budgets
/// after a fixed number of steps and every run of a scenario takes the same steps.
/// </summary>
internal sealed class StepClock : IClock
{
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(100);
    private TimeSpan _now;

    public TimeSpan Now => _now += Tick;
}

/// <summary>Game time that moves only when the test moves it.</summary>
internal sealed class HandGameClock : IGameClock
{
    public GameTime Now { get; private set; } = new(100);

    public void Advance(double seconds) => Now = new GameTime(Now.Seconds + seconds);
}

/// <summary>Wall time fixed at the start of 2026, so ghosts' placing times are equal across runs.</summary>
internal sealed class FixedTime : ITimeSource
{
    public DateTimeOffset Now { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>A game that has every member Lattice's modules use: every module is on.</summary>
internal sealed class AllMembers : IGameMembers
{
    public ValueList<GameMember> Missing(ModuleManifest manifest) => ValueList<GameMember>.Empty;
}

/// <summary>The world's structures as Lattice scans them at start.</summary>
internal sealed class WorldScan(FakeWorld world) : IWorldScan
{
    public ValueList<StructureInfo> Structures()
    {
        var all = new List<StructureInfo>(world.Count);
        foreach (var structure in world.All)
            all.Add(structure);
        return ValueList.From(all);
    }
}
