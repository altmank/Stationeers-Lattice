using System;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Lattice.Api;
using Lattice.Core.Analysis;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.EntryPoint;
using Lattice.Core.Executors;
using Lattice.Core.Grid;
using Lattice.Core.Styles;
using Lattice.Game.Catalogue;
using Lattice.Game.Looks;
using Lattice.Game.Saving;
using Lattice.Game.Sync;
using Lattice.Game.World;
using Lattice.Render.Unity;
using UnityEngine;

namespace Lattice.Game.EntryPoint;

/// <summary>
/// Starts Lattice when a world starts running and ends it on every way the world ends. It reads the game's state each
/// frame (the game's own change event is skipped while quitting): a world starts on entering Running from outside
/// Running and Paused, and ends when the state leaves both (quit to menu, loading another save, a disconnect, a server
/// stop), when the game quits and when this object goes. Once a frame, while a world runs: sync's poll, then Lattice's
/// frame. Lattice's frame runs in Update and raises the world bridge's signals first, so each frame reads the signals
/// the game gave since the last one, inside the frame's time.
/// </summary>
internal sealed class WorldWatcher : MonoBehaviour
{
    private CachedGameMembers _members = null!;
    private Action<string> _log = static _ => { };
    private bool _networking;
    private bool _exportAsked;
    private bool _inWorld;
    private LatticeWorld? _world;
    private WorldBridge? _bridge;
    private RenderHost? _render;

    public void Prepare(CachedGameMembers members, bool networking, Action<string> log)
    {
        _members = members;
        _networking = networking;
        _log = log;
    }

    private void Update()
    {
        var state = GameManager.GameState;
        var running = state is GameState.Running or GameState.Paused;
        if (!running)
        {
            if (_inWorld)
                EndWorld();
            _inWorld = false;
            return;
        }

        if (!_inWorld && state == GameState.Running)
        {
            _inWorld = true;
            StartWorld();
        }

        if (_world is { } world)
        {
            GameSync.Current?.Poll();
            world.Frame();
        }
    }

    private void OnApplicationQuit() => EndWorld();

    private void OnDestroy() => EndWorld();

    private void StartWorld()
    {
        if (!_exportAsked)
        {
            _exportAsked = true;
            CatalogueExport.RunIfAsked(_log);
        }

        try
        {
            Open();
        }
        catch (Exception error)
        {
            _log($"Lattice does not start for this world: {error}");
            EndWorld();
        }
    }

    private void Open()
    {
        var seat = NetworkManager.IsClient ? WorldSeat.Client : WorldSeat.Host;
        var modules = ModuleSwitches.Decide(LatticeModules.All, _members, seat, drawing: !GameManager.IsBatchMode);
        if (modules.Why(LatticeModules.Catalogue).TryGet(out var noCatalogue))
        {
            _log($"Lattice does not start for this world: the catalogue cannot be read. {noCatalogue}");
            return;
        }

        var listing = System.Diagnostics.Stopwatch.StartNew();
        var catalogue = GameCatalogue.Read();
        _ = catalogue.Listing;
        listing.Stop();
        var ports = new WorldPorts(catalogue, new CubeGrid(), modules, seat, Viewer(), new GameClock())
        {
            Rooms = Rooms(),
            Paints = GamePaintColours.Read().TryGet(out var paints) ? paints : new NoPaintColours(),
        };

        if (modules.IsOn(LatticeModules.World))
        {
            if (!WorldBridge.Start().TryGet(out var bridge))
            {
                _log("Lattice does not start for this world: the world bridge did not start.");
                return;
            }

            _bridge = bridge;
            var changes = bridge.Changes;
            ports = ports with { World = Option.Some(new GamePorts(bridge.View, bridge.View, changes, bridge.Placement) { FlushChanges = changes.Flush }) };
        }

        GameSaving? saving = null;
        if (seat == WorldSeat.Host && modules.IsOn(LatticeModules.Saving))
        {
            saving = GameSaving.Open(SaveHooks.TakeLoaded(), StationSaveUtils.GetSavePathSavesSubDir().FullName, _log);
            SaveHooks.Current = saving;
            ports = ports with { Saving = Option.Some(new OpenedSaving(saving.Saving, saving.Opened)) };
        }

        if (_networking && modules.IsOn(LatticeModules.Sync))
            ports = ports with { Link = Option.Some<Lattice.Core.Sync.ISyncLink>(new LaunchPadLink()) };

        if (!LatticeWorld.Start(ports, WorldRuntime.Threads(_log)).TryGet(out var world))
        {
            EndWorld();
            return;
        }

        _world = world;
        if (saving is not null)
            saving.CatchUp = world.CatchUp;
        Action<Reason> report = why => _log($"Lattice sync: {why}");
        GameSync.Current = world.Host is { } host ? GameSync.ForHost(host, report)
            : world.Client is { } client ? GameSync.ForClient(client, report)
            : null;
        var off = 0;
        foreach (var module in world.Modules)
            if (module.State is ModuleState.Off)
                off++;
        _log($"Lattice: catalogue listed in {(long)listing.Elapsed.TotalMilliseconds} ms: {CatalogueSummary.Of(world.Catalogue)}.");
        _log($"Lattice: started for this world ({seat}, {(GameManager.IsBatchMode ? "dedicated" : "with a local player")}): {world.Modules.Count - off} modules on, {off} off.");
        if (modules.IsOn(LatticeModules.Render))
            OpenRender(world);
        world.Publish();
    }

    /// <summary>Drawing for the world, published before the world so a consumer's <c>Started</c> handler finds it.</summary>
    private void OpenRender(LatticeWorld world)
    {
        var opened = RenderHost.Open(world, new GamePieceParts(new PieceMeshes()), GameText.Font(), _log);
        if (opened.TryGet(out var render))
        {
            _render = render;
            LatticeDrawing.Publish(render.Drawing);
        }
        else if (opened.TryGetError(out var why))
            _log($"Lattice render: not drawing for this world: {why}");
    }

    private void EndWorld()
    {
        var world = _world;
        _world = null;
        var render = _render;
        _render = null;
        try
        {
            LatticeDrawing.Clear();
            render?.End();
            world?.End();
            if (world is not null)
                _log("Lattice: ended for this world.");
        }
        finally
        {
            GameSync.Current = null;
            SaveHooks.Current = null;
            var bridge = _bridge;
            _bridge = null;
            bridge?.End();
        }
    }

    /// <summary>The local viewer: a dedicated server has no local player, so Lattice's own actor; else the local player.</summary>
    private static Actor Viewer() =>
        GameManager.IsBatchMode ? LatticeIdentity.Actor : new Actor.Player(new ClientId(NetworkManager.LocalClientId));

    /// <summary>
    /// The game's room rule: a flood fill past RoomFloodFiller's iteration limit makes no room (decompile
    /// Rooms/RoomFloodFiller.cs, MAX_ITERATIONS; Rooms/RoomEvaluator.cs). Read from the game; the cited default when the
    /// constant is gone.
    /// </summary>
    private static GameRooms Rooms()
    {
        var filler = Type.GetType("Rooms.RoomFloodFiller, Assembly-CSharp");
        var limit = filler?.GetField("MAX_ITERATIONS", BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue();
        return limit is int cells && cells > 0 ? new GameRooms(cells) : GameRooms.Default;
    }
}
