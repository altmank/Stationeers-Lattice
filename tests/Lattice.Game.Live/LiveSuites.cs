using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Game.World;
using Lattice.Tests.Contracts;
using UnityEngine;
using Cell = Lattice.Api.Cell;

namespace Lattice.Game.Live;

/// <summary>The world view over the running game passes the world view suite, for every structure the world holds.</summary>
public sealed class GameWorldViewSuite : WorldViewContract
{
    private readonly GameWorldView _view = new();

    internal override IWorldView Subject => _view;

    internal override IReadOnlyList<StructureInfo> Known => LiveContext.Known(_view);
}

/// <summary>The game's cursor check passes the placement suite: a frame in free air, and one in a cell a frame holds.</summary>
public sealed class GamePlacementSuite : PlacementCheckContract
{
    internal override IPlacementCheck Subject { get; } = new GamePlacementCheck();

    internal override Placement Accepted => new(Scene.Frame, Scene.FreeAir, Turn.None);

    internal override Placement Refused => new(Scene.Frame, Scene.Occupied, Turn.None);
}

/// <summary>
/// The bridge's change signals over the game pass the extension-point suite. Each cause is the game's own call on a
/// scratch frame far from the scene, then the frame's flush: spawn (registered), the build index setter, paint, the
/// name setter, the grid's deregister (removed), detach and attach (moved).
/// </summary>
public sealed class GameWorldChangesSuite : WorldChangesContract, IDisposable
{
    private static int _next;
    private readonly WorldBridge _bridge;
    private readonly Structure _scratch;
    private readonly ISetable _setable;
    private bool _ended;

    public GameWorldChangesSuite()
    {
        _bridge = WorldBridge.Start().TryGet(out var bridge) ? bridge : throw new InvalidOperationException("no world bridge");
        _scratch = Scene.Spawn(Scene.FrameName, Scene.Scratch(_next++), Quaternion.identity, 1)
            ?? throw new InvalidOperationException("no scratch frame");
        _setable = Scene.Spawn(Scene.MemoryName, Scene.Scratch(_next++), Quaternion.identity, int.MaxValue) as ISetable
            ?? throw new InvalidOperationException("no scratch logic memory");
        _bridge.Changes.Flush();
    }

    internal override IWorldChanges Subject => _bridge.Changes;

    internal override void Cause(string eventName)
    {
        var n = _next++;
        switch (eventName)
        {
            case "Registered":
                Scene.Spawn(Scene.FrameName, Scene.Scratch(n), Quaternion.identity, 1);
                break;
            case "StateChanged":
                _scratch.CurrentBuildStateIndex = _scratch.CurrentBuildStateIndex == 0 ? 1 : 0;
                break;
            case "Removed":
                var victim = Scene.Spawn(Scene.FrameName, Scene.Scratch(n), Quaternion.identity, 1)!;
                _bridge.Changes.Flush();
                GridController.World.Deregister(victim);
                break;
            case "Painted":
                _scratch.SetCustomColor(1 + n % 10);
                break;
            case "Renamed":
                _scratch.CustomName = "Scratch " + n;
                break;
            case "SettingChanged":
                _setable.Setting = 100 + n;
                break;
            case "Moved":
                GridController.World.Detatch(_scratch);
                _scratch.ThingTransformPosition += new Vector3(0, 2, 0);
                GridController.World.Attach(_scratch);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(eventName), eventName, null);
        }

        _bridge.Changes.Flush();
    }

    internal override void EndWorld()
    {
        _ended = true;
        _bridge.End();
    }

    public void Dispose()
    {
        if (!_ended)
            _bridge.End();
    }
}

/// <summary>What the live run shares with its suites.</summary>
internal static class LiveContext
{
    /// <summary>Every structure the world holds, as the view's scan reads it.</summary>
    public static IReadOnlyList<StructureInfo> Known(GameWorldView view)
    {
        var known = new List<StructureInfo>();
        foreach (var info in view.Structures())
            known.Add(info);
        return known;
    }

    public static Cell CellOf(Vector3 position) => new(
        (int)Math.Round(position.x * 10.0, MidpointRounding.AwayFromZero),
        (int)Math.Round(position.y * 10.0, MidpointRounding.AwayFromZero),
        (int)Math.Round(position.z * 10.0, MidpointRounding.AwayFromZero));
}
