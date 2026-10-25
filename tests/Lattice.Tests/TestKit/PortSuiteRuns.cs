using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.Contracts;
using Lattice.TestKit.Grid;
using Lattice.TestKit.PlacementCheck;
using Lattice.TestKit.Rules;
using Lattice.TestKit.Scenarios;
using Lattice.TestKit.World;

namespace Lattice.Tests.TestKit;

/// <summary>The hand-written catalogue passes the catalogue suite.</summary>
public sealed class FakeCatalogueSuite : CatalogueContract
{
    internal override ICatalogue Subject { get; } = FakeCatalogue.Hand();
}

/// <summary>The fake grid passes the grid suite.</summary>
public sealed class FakeGridSuite : GridContract
{
    internal override IGrid Subject => FakeGrid.Instance;
}

/// <summary>A scenario world (pieces, a network with a device, a long straight, a room) passes the world view suite.</summary>
public sealed class FakeWorldViewSuite : WorldViewContract
{
    private readonly FakeWorld _world = Worlds.Mixed().World;

    internal override IWorldView Subject => _world;

    internal override IReadOnlyList<StructureInfo> Known => new List<StructureInfo>(_world.All);
}

/// <summary>The fake placement check passes the placement check suite.</summary>
public sealed class FakePlacementCheckSuite : PlacementCheckContract
{
    private static readonly PrefabKey Frame = PrefabHash.Piece("StructureFrameIron");

    internal override IPlacementCheck Subject { get; } =
        new FakePlacementCheck().Refuse(p => p.At.Y < 0, ReasonCodes.NeedsSupport, "Must be placed on a frame");

    internal override Placement Accepted => new(Frame, new Cell(10, 10, 10), Turn.None);

    internal override Placement Refused => new(Frame, new Cell(10, -10, 10), Turn.None);
}

/// <summary>The fake world's change events pass the extension-point suite.</summary>
public sealed class FakeWorldChangesSuite : WorldChangesContract
{
    private readonly FakeWorld _world;
    private readonly StructureInfo _frame;
    private long _nextId = 100;
    private int _nextColour;

    public FakeWorldChangesSuite()
    {
        _world = Worlds.Mixed().World;
        _frame = _world.Structure(new ReferenceId(1)).TryGet(out var frame) ? frame : throw new InvalidOperationException();
    }

    internal override IWorldChanges Subject => _world;

    internal override void Cause(string eventName)
    {
        var id = _frame.Ref.Id;
        switch (eventName)
        {
            case "Registered":
                _world.Register(_frame with { Ref = _frame.Ref with { Id = new ReferenceId(_nextId++) } });
                break;
            case "StateChanged":
                _world.ChangeStep(id, default);
                break;
            case "Removed":
                var victim = new ReferenceId(_nextId++);
                _world.Load(_frame with { Ref = _frame.Ref with { Id = victim } });
                _world.Remove(victim);
                break;
            case "Painted":
                _world.Paint(id, Ok(PaintColour.Create(_nextColour++)));
                break;
            case "Renamed":
                _world.Rename(id, Ok(ThingName.Create("Pump room")));
                break;
            case "SettingChanged":
                _world.ChangeSetting(id, Ok(Setting.Create(_nextColour++)));
                break;
            case "Moved":
                _world.Move(ValueList.Of(new StructureMove(_frame.Ref, new Cell(30, 30, 30), Turn.None)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(eventName), eventName, null);
        }
    }

    internal override void EndWorld() => _world.End();

    private static T Ok<T>(Result<T, Reason> r) where T : notnull => r.TryGet(out var v) ? v : throw new InvalidOperationException(r.ToString());
}

/// <summary>The stand-in rule host passes the rule suite, so the suite itself is known to be satisfiable.</summary>
public sealed class FakeRuleHostSuite : RuleHostContract
{
    private readonly ManualClock _clock = new();
    private readonly FakeRuleHost _host;

    public FakeRuleHostSuite() => _host = new(new FakeWorldQuery(Worlds.Mixed().World), TimeSpan.FromMilliseconds(2), _clock);

    internal override Result<IDisposable, Reason> Register(RuleDescriptor rule) => _host.Register(rule);

    internal override ValueList<Finding> Check(Ghost ghost) => _host.Check(ghost);

    internal override void EndWorld() => _host.End();

    internal override TimeSpan Limit => _host.Limit;

    internal override void Elapse(TimeSpan time) => _clock.Advance(time);
}

/// <summary>Shared scenario worlds.</summary>
internal static class Worlds
{
    /// <summary>
    /// An iron frame; a cable line of two straights and a corner turned a half turn, feeding an area power control;
    /// a lone straight past a gap; a 3-long straight; a pipe; a sealed room around the frame.
    /// </summary>
    public static BuiltScenario Mixed() => Scenario.With(FakeCatalogue.Hand())
        .Built("StructureFrameIron", new Cell(10, 10, 10), colour: 3, name: "Store")
        .Built("StructureCableStraight", new Cell(0, 0, 0))
        .Built("StructureCableStraight", new Cell(0, 0, 5))
        .Built("StructureCableCorner", new Cell(0, 0, 10), turn: 2)
        .Built("StructureAreaPowerControl", new Cell(5, 0, 10))
        .Built("StructureCableStraight", new Cell(0, 0, 20))
        .Built("StructureCableStraight3", new Cell(50, 0, 0))
        .Built("StructurePipeStraight", new Cell(0, 5, 0))
        .Built("StructureFrameIron", new Cell(30, 10, 10), step: 1)
        .Room(7, true, new Cell(10, 10, 10))
        .Build();
}
