using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects.Pipes;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Game.Catalogue;
using Lattice.Game.World;
using TerrainSystem;
using UnityEngine;
using Cell = Lattice.Api.Cell;

namespace Lattice.Game.Live;

/// <summary>
/// Build safety's game facts read back in a running world (mode <c>safety</c>): the catalogue's safety facts at world
/// load (modded pieces registered by then), room and pipe network temperatures against the game's own numbers, and
/// terrain in large cells at, above and below the ground.
/// </summary>
internal static class SafetyRun
{
    private static readonly string[] Named =
    {
        "StructureActiveVent", "StructurePoweredVent", "StructureFiltration", "StructureWallHeater", "StructureWallCooler",
        "StructureAirConditioner", "StructureBattery", "StructureSolarPanel", "StructureAreaPowerControl",
        "StructureSolidFuelGenerator", "StructurePassiveVent", "StructurePipeStraight", "StructureWallLight",
        "StructureConsole", "StructureFrame",
    };

    // The room block's open centre, in metres: a large cell centre (odd metres) well above the Lunar ground.
    private const int RoomX = 11;
    private const int RoomY = 301;
    private const int RoomZ = 11;

    // Ten moles of nitrogen at a temperature (the game's nitrogen specific heat, 20.6 J/(mol K)).
    private static Mole Nitrogen(double kelvin) =>
        new(Chemistry.GasType.Nitrogen, new MoleQuantity(10), new MoleEnergy(10 * 20.6 * kelvin));

    public static IEnumerator Run(string outDir, Action<string> line)
    {
        Catalogue(outDir, line);
        var frames = 0;
        for (var x = RoomX - 2; x <= RoomX + 2; x += 2)
            for (var y = RoomY - 2; y <= RoomY + 2; y += 2)
                for (var z = RoomZ - 2; z <= RoomZ + 2; z += 2)
                    if ((x != RoomX || y != RoomY || z != RoomZ) && Scene.Spawn(Scene.FrameName, new Vector3(x, y, z), Quaternion.identity, int.MaxValue) != null)
                        frames++;
        line($"safety room block: {frames} frames around ({RoomX}, {RoomY}, {RoomZ})");
        var pipe = Scene.Spawn("StructurePipeStraight", new Vector3(RoomX + 10f, RoomY, RoomZ), Quaternion.identity, int.MaxValue);
        yield return new WaitForSecondsRealtime(3f);
        if (pipe is Pipe held && held.PipeNetwork?.Atmosphere is { } atmosphere)
            atmosphere.Add(Nitrogen(400));
        else
            line("FAIL safety: no pipe network to fill");
        // The game's own way to put gas in a cell (AtmosphericEventInstance.cs:486).
        AtmosphericsController.World.CloneGlobalAtmosphere(new WorldGrid(new Vector3(RoomX, RoomY, RoomZ))).Add(Nitrogen(300));
        Rooms.RoomEvaluator.Instance?.CheckGrid(GameReads.Grid(new Cell(RoomX * 10, RoomY * 10, RoomZ * 10)));
        var waited = 0f;
        for (; waited < 20f && Assets.Scripts.GridSystem.Room.AllRooms.Count == 0; waited += 1f)
            yield return new WaitForSecondsRealtime(1f);
        line($"safety rooms in the world after {waited:F0} s: {Assets.Scripts.GridSystem.Room.AllRooms.Count}");
        yield return new WaitForSecondsRealtime(3f);

        var view = new GameWorldView();
        Room(view, line);
        if (pipe != null)
            Network(view, pipe, line);
        Terrain(view, line);
    }

    private static void Catalogue(string outDir, Action<string> line)
    {
        var path = Path.Combine(outDir, "catalogue-world.lcat");
        line("safety catalogue: " + CatalogueExport.Write(path).Match(r => $"{r.Report} -> {r.Path}", e => "FAIL " + e));
        var source = new GamePrefabSource();
        var catalogue = new PrefabCatalogue(source);
        int air = 0, heat = 0, supplies = 0, vents = 0, controls = 0, modded = 0;
        var byName = new Dictionary<string, PieceFacts>();
        foreach (var key in source.Structures)
        {
            if (!catalogue.Piece(key).TryGet(out var facts) || !catalogue.NameOf(key).TryGet(out var name))
                continue;
            byName[name] = facts;
            air += (facts.Keeps & Keeps.Air) != 0 ? 1 : 0;
            heat += (facts.Keeps & Keeps.Heat) != 0 ? 1 : 0;
            supplies += facts.Supplies ? 1 : 0;
            vents += facts.Vents ? 1 : 0;
            controls += facts.ControlFace.IsSome ? 1 : 0;
            if (name.StartsWith("StructureMatterlink", StringComparison.Ordinal))
            {
                modded++;
                line("safety modded piece " + Describe(name, facts));
            }
        }

        line($"safety counts: pieces {byName.Count}, keeps air {air}, keeps heat {heat}, supplies {supplies}, vents {vents}, control faces {controls}, matterlink pieces {modded}");
        foreach (var name in Named)
            line(byName.TryGetValue(name, out var facts) ? "safety piece " + Describe(name, facts) : $"safety piece {name}: not in the catalogue");
    }

    private static string Describe(string name, PieceFacts facts) =>
        $"{name}: keeps={(facts.Keeps == Keeps.None ? "none" : CatalogueText.Keeps(facts.Keeps))} supplies={(facts.Supplies ? "yes" : "no")} " +
        $"control={facts.ControlFace.Map(static d => CatalogueText.Direction(d)).Reduce("none")} vents={(facts.Vents ? "yes" : "no")}";

    private static void Room(GameWorldView view, Action<string> line)
    {
        var cell = new Cell(RoomX * 10, RoomY * 10, RoomZ * 10);
        var game = RoomController.World?.GetRoom(GameReads.Grid(cell));
        if (game == null || !view.RoomAt(cell).TryGet(out var room) || !view.Room(room).TryGet(out var info))
        {
            // The headless test server forms no room around spawned frames (none in the world after the wait), so
            // room temperatures are read on a full base's save instead.
            var evaluator = Rooms.RoomEvaluator.Instance;
            line($"safety room temperature: not runnable headless, no room formed (rooms {Assets.Scripts.GridSystem.Room.AllRooms.Count}, " +
                 $"evaluator {(evaluator == null ? "none" : $"paused {evaluator.Pause} queue {evaluator.LastQueueDepth}")})");
            return;
        }

        var kelvin = game.Temperature.ToDouble();
        var read = info.Temperature.Map(static k => k.Value);
        var agrees = read.Map(r => Math.Abs(r - kelvin) < 0.01).Reduce(kelvin <= 0);
        line($"{(agrees ? "PASS" : "FAIL")} safety room temperature: lattice {read.Map(static r => r.ToString("F2")).Reduce("none")}, game {kelvin:F2}");
    }

    private static void Network(GameWorldView view, Assets.Scripts.Objects.Structure pipe, Action<string> line)
    {
        var id = new ReferenceId(pipe.ReferenceId);
        var game = (pipe as Pipe)?.PipeNetwork?.Atmosphere;
        if (game == null || !view.NetworkOf(id).TryGet(out var network) || !view.Network(network).TryGet(out var info))
        {
            line("FAIL safety network temperature: no network read");
            return;
        }

        var kelvin = game.Temperature.ToDouble();
        var read = info.Temperature.Map(static k => k.Value);
        var agrees = kelvin > 0 && read.Map(r => Math.Abs(r - kelvin) < 0.01).Reduce(false);
        line($"{(agrees ? "PASS" : "FAIL")} safety network temperature: lattice {read.Map(static r => r.ToString("F2")).Reduce("none")}, game {kelvin:F2}");
    }

    private static void Terrain(GameWorldView view, Action<string> line)
    {
        const float X = 101f;
        const float Z = 101f;
        var surface = float.NaN;
        for (var y = 400f; y > -100f; y -= 0.5f)
            if (VoxelTerrain.GetDensityWorldSpace(new Vector3(X, y, Z)) > 0.5f)
            {
                surface = y;
                break;
            }

        if (float.IsNaN(surface))
        {
            line("FAIL safety terrain: no voxel below (101, 101)");
            return;
        }

        var centre = (int)(Math.Floor(surface / 2) * 2 + 1);
        void Expect(string what, int metres, bool expected, int x = (int)X, int z = (int)Z)
        {
            var cell = new Cell(x * 10, metres * 10, z * 10);
            var found = view.Terrain(cell);
            line($"{(found == expected ? "PASS" : "FAIL")} safety terrain {what} at {cell.X},{cell.Y},{cell.Z}: {found}");
        }

        line($"safety terrain: surface voxel at y {surface} under ({X}, {Z})");
        Expect("in the surface cell", centre, true);
        Expect("ten metres down", centre - 10, true);
        Expect("twenty metres up", centre + 20, false);
        Expect("at the room block's centre", RoomY, false, RoomX, RoomZ);
    }
}
