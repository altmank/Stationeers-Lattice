using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Util;
using BepInEx;
using Lattice.Api;
using UnityEngine;

namespace LatticeLiveChecks;

/// <summary>
/// Live checks in a VM rig. At plugin load: which Lattice assemblies loaded, where the
/// IsExternalInit shim lives (the headless catalogue export runs through Lattice's own plugin at the first Running). Once a world runs:
/// records across assemblies, the catalogue export, placement on the server's cursors, player ids; then a done file.
/// Every line starts "LIVECHECK" so the report can grep it.
/// </summary>
[BepInPlugin("lattice.livechecks", "Lattice Live Checks", "0.1.0")]
public sealed class Plugin : BaseUnityPlugin
{
    // -lattice-out <folder> moves the output otherwise a lattice-out folder under BepInEx.
    private static readonly string OutDir = OutArg() ?? Path.Combine(BepInEx.Paths.BepInExRootPath, "lattice-out");
    private bool _ran;
    private float _runningSince = -1f;

    private void Awake()
    {
        Directory.CreateDirectory(OutDir);
        Line("plugin loaded; prefabs loaded so far: " + Prefab.AllPrefabs.Count);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("Lattice")))
        {
            Type shim = assembly.GetType("System.Runtime.CompilerServices.IsExternalInit", false);
            Line($"assembly {assembly.GetName().Name} {assembly.GetName().Version} from {Location(assembly)}; IsExternalInit: {(shim == null ? "none" : (shim.IsPublic ? "public" : "internal"))}");
        }

        string[] startArgs = Environment.GetCommandLineArgs();
        int liveArg = Array.IndexOf(startArgs, "-lattice-live");
        if (liveArg >= 0 && liveArg + 1 < startArgs.Length)
        {
            Type live = Type.GetType("Lattice.Game.Live.LiveRun, Lattice.Game.Live", false);
            live?.GetMethod("Prepare", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
            Line("live run prepared (saving hooks, Lattice timing): " + (live != null));
        }
    }

    private void Update()
    {
        // The two-player client, the render modes and a big-base run on a client start from the main menu; a server waits for its world.
        string[] clientArgs = Environment.GetCommandLineArgs();
        int clientArg = Array.IndexOf(clientArgs, "-lattice-live");
        if (!_ran && clientArg >= 0 && clientArg + 1 < clientArgs.Length && (clientArgs[clientArg + 1] == "two-player-client" || clientArgs[clientArg + 1] == "builder-turns" || clientArgs[clientArg + 1].StartsWith("render", StringComparison.Ordinal)
            || (clientArgs[clientArg + 1].StartsWith("bigbase-", StringComparison.Ordinal) && !GameManager.IsBatchMode)))
        {
            _ran = true;
            Line("starting Lattice.Game.Live mode " + clientArgs[clientArg + 1] + " from the main menu");
            Type client = Type.GetType("Lattice.Game.Live.LiveRun, Lattice.Game.Live", false);
            client?.GetMethod("Start", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, new object[] { OutDir, clientArgs[clientArg + 1] });
            return;
        }

        if (_ran || GameManager.GameState != GameState.Running || !GameManager.RunSimulation)
            return;
        if (_runningSince < 0f)
            _runningSince = Time.realtimeSinceStartup;
        if (Time.realtimeSinceStartup - _runningSince < 10f)
            return;
        _ran = true;
        string[] args = Environment.GetCommandLineArgs();
        int live = Array.IndexOf(args, "-lattice-live");
        if (live >= 0 && live + 1 < args.Length)
        {
            Line("starting Lattice.Game.Live mode " + args[live + 1]);
            Type run = Type.GetType("Lattice.Game.Live.LiveRun, Lattice.Game.Live", false);
            if (run == null)
            {
                Line("Lattice.Game.Live not found");
                File.WriteAllText(Path.Combine(OutDir, "done.txt"), DateTime.Now.ToString("O"));
                return;
            }

            run.GetMethod("Start", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, new object[] { OutDir, args[live + 1] });
            return;
        }

        Run("records", Records);
        Run("catalogue", Catalogue);
        Run("cursors", Cursors);
        Run("placement", Placement);
        Run("identity", Identity);
        File.WriteAllText(Path.Combine(OutDir, "done.txt"), DateTime.Now.ToString("O"));
        Line("all checks done");
    }

    private static void Records()
    {
        // Api records built here, in another assembly, inside the game's Mono.
        Reason a = Reason.Of(ReasonCodes.NotFound, RuleId.Contract);
        Reason b = Reason.Of(ReasonCodes.NotFound, RuleId.Contract);
        Reason c = a with { Code = ReasonCodes.TooLarge };
        Line($"record equality across assemblies: a == b {a == b}, a.Equals(b) {a.Equals(b)}, hash equal {a.GetHashCode() == b.GetHashCode()}");
        Line($"record with across assemblies: c.Code {c.Code}, a.Code {a.Code}, c == a {c == a}, c.From == a.From {c.From == a.From}");
        PrefabKey key = new(42);
        PrefabKey copy = key with { Hash = 43 };
        Line($"record struct with: {key} -> {copy}, equal to new(43): {copy == new PrefabKey(43)}");
        ValueList<int> list1 = ValueList.From(new[] { 1, 2, 3 });
        ValueList<int> list2 = ValueList.From(new[] { 1, 2, 3 });
        Line($"ValueList value equality: {list1.Equals(list2)}");
    }

    private static void Catalogue()
    {
        Type export = Type.GetType("Lattice.Game.Catalogue.CatalogueExport, Lattice.Game", true);
        string path = Path.Combine(OutDir, "catalogue.lcat");
        object result = export.GetMethod("Write", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, new object[] { path });
        Line("CatalogueExport.Write: " + result);
        if (File.Exists(path))
            foreach (string line in File.ReadLines(path).Take(6))
                Line("  " + line);
    }

    private static void Cursors()
    {
        Dictionary<string, Structure> cursors = CursorTable();
        int prefabs = 0, missing = 0;
        foreach (Structure prefab in Structure.AllStructurePrefabs)
        {
            if (prefab == null || prefab.IsCustomThing)
                continue;
            prefabs++;
            if (!cursors.TryGetValue(prefab.name, out Structure cursor) || cursor == null)
                missing++;
        }

        Line($"construction cursors on this server: {cursors.Count}; structure prefabs {prefabs}, without a cursor {missing}; batch mode {GameManager.IsBatchMode}");
    }

    private static void Placement()
    {
        Dictionary<string, Structure> cursors = CursorTable();
        Structure frameCursor = Cursor(cursors, "StructureFrame");
        Structure chuteCursor = Cursor(cursors, "StructureChuteStraight");
        Structure pipeCursor = Cursor(cursors, "StructurePipeStraight");
        Structure apcCursor = Cursor(cursors, "StructureAreaPowerControl");

        // Ground: the server's physics under a few spots of the new world.
        bool ground = false;
        foreach (Vector3 spot in new[] { Vector3.zero, new Vector3(50f, 0f, 50f), new Vector3(-80f, 0f, 30f), new Vector3(200f, 0f, -150f) })
        {
            if (!Physics.Raycast(new Vector3(spot.x, 1000f, spot.z), Vector3.down, out RaycastHit hit, 2000f))
                continue;
            Vector3 cell = (hit.point + Vector3.up * 1.01f).GridCenter(2f, 0f);
            Line($"terrain at {hit.point} ({hit.collider?.name}); frame on the ground at {cell}: {Verdict(frameCursor, cell, Quaternion.identity) ?? "yes"}");
            ground = true;
            break;
        }

        if (!ground)
            Line("no terrain collider under four spots from the server's physics (no player near: terrain not built)");

        // Built here: a steel frame in free air, then the cells around it.
        Vector3 site = new Vector3(1f, 61f, 1f).GridCenter(2f, 0f);
        Line($"frame in free air at {site}: {Verdict(frameCursor, site, Quaternion.identity) ?? "yes"}");
        Structure frame = Spawn("StructureFrame", site, Quaternion.identity);
        Line($"built {frame?.PrefabName} at {frame?.ThingTransformPosition} state {frame?.CurrentBuildStateIndex}");
        Line($"frame on that frame at {site + Vector3.up * 2f}: {Verdict(frameCursor, site + Vector3.up * 2f, Quaternion.identity) ?? "yes"}");
        Line($"frame in the same cell {site}: {Verdict(frameCursor, site, Quaternion.identity) ?? "yes"}");

        Vector3 high = (site + new Vector3(0f, 20f, 0f)).GridCenter(0.5f, 0.25f);
        Line($"area power control with no wall at {high}: {Verdict(apcCursor, high, Quaternion.identity) ?? "yes"}");

        Vector3 small = (site + new Vector3(6f, 0f, 0f)).GridCenter(0.5f, 0.25f);
        Structure cable = Spawn("StructureCableStraight", small, Quaternion.identity);
        Line($"built {cable?.PrefabName} at {cable?.ThingTransformPosition}");
        Line($"small-cell clash: chute straight in the cable's cell, same turn: {Verdict(chuteCursor, small, Quaternion.identity) ?? "yes"}");
        Line($"small-cell clash: pipe straight in the cable's cell, same turn: {Verdict(pipeCursor, small, Quaternion.identity) ?? "yes"}");
        Line($"small-cell clash: pipe straight in the cable's cell, turned 90 about y: {Verdict(pipeCursor, small, Quaternion.Euler(0f, 90f, 0f)) ?? "yes"}");
        Line($"control: chute straight 3 small cells above: {Verdict(chuteCursor, small + Vector3.up * 1.5f, Quaternion.identity) ?? "yes"}");
    }

    private static Structure Spawn(string prefabName, Vector3 position, Quaternion rotation)
    {
        Structure prefab = Prefab.Find<Structure>(prefabName);
        Structure built = Constructor.SpawnConstruct(new Assets.Scripts.Networking.CreateStructureInstance(
            prefab, GridController.World.WorldToLocal(position), rotation, 0UL, -1));
        if (built != null && built.BuildStates.Count > 0)
        {
            built.CurrentBuildStateIndex = built.BuildStates.Count - 1;
            built.UpdateStateVisualizer();
        }

        return built;
    }

    private static void Identity()
    {
        Line($"humans in the world: {Human.AllHumans.Count}");
        foreach (Human human in Human.AllHumans.Take(10))
            Line($"  human {human.DisplayName} ReferenceId {human.ReferenceId} OwnerClientId {human.OwnerClientId} (Steam ids are 7656119...)");
        const ulong probeId = 76561190000000042UL;
        Human created = Human.CreateCharacter(probeId, "LatticeProbe");
        Line($"created character for client id {probeId}: {(created == null ? "null" : created.DisplayName)}, OwnerClientId {created?.OwnerClientId}");
        Entity found = Entity.GetClientEntity(probeId);
        Line($"Entity.GetClientEntity({probeId}) finds it again: {found != null && found == created}; Human.Find: {Human.Find(probeId) == created}");
        Line($"this host's client id: {Assets.Scripts.Networking.NetworkManager.LocalClientId}; cookie: {(Assets.Scripts.Networking.NetworkManager.Cookie == null ? "none (batch mode)" : "present")}");
    }

    private static Dictionary<string, Structure> CursorTable() =>
        typeof(InventoryManager).GetField("_constructionCursors", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Dictionary<string, Structure>
        ?? new Dictionary<string, Structure>();

    private static Structure Cursor(Dictionary<string, Structure> cursors, string name) =>
        cursors.TryGetValue(name, out Structure cursor) ? cursor : null;

    /// <summary>The game's verdict with the cursor at the spot: null = yes, else its message.</summary>
    private static string Verdict(Structure cursor, Vector3 position, Quaternion rotation)
    {
        if (cursor == null)
            return "no cursor";
        Transform transform = cursor.ThingTransform;
        Vector3 oldPosition = transform.position;
        Quaternion oldRotation = transform.rotation;
        try
        {
            transform.SetPositionAndRotation(position, rotation);
            cursor.Position = position;
            cursor.Rotation = rotation;
            CanConstructInfo info = cursor.CanConstruct();
            if (!info.CanConstruct)
                return "no: " + (string.IsNullOrEmpty(info.ErrorMessage) ? "(no message)" : info.ErrorMessage);
            if (cursor.PlacementType == PlacementSnap.FaceMount)
            {
                CanMountResult mount = cursor.CanMountOnWall();
                if (!mount)
                    return "no (mount): " + mount.result;
            }

            return null;
        }
        finally
        {
            transform.SetPositionAndRotation(oldPosition, oldRotation);
            cursor.Position = oldPosition;
            cursor.Rotation = oldRotation;
        }
    }

    private static void Run(string name, Action check)
    {
        try
        {
            check();
        }
        catch (Exception e)
        {
            Line($"{name} threw {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
        }
    }

    private static string Location(Assembly assembly)
    {
        try
        {
            return assembly.Location;
        }
        catch (NotSupportedException)
        {
            return "(dynamic)";
        }
    }

    private static string OutArg()
    {
        string[] args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "-lattice-out");
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static void Line(string text)
    {
        string line = "LIVECHECK " + text;
        UnityEngine.Debug.Log(line);
        File.AppendAllText(Path.Combine(OutDir, "livechecks.log"), DateTime.Now.ToString("HH:mm:ss ") + line + "\n");
    }
}
