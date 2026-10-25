using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Lattice.Api;
using UnityEngine;
using Cell = Lattice.Api.Cell;

namespace Lattice.Game.Live;

/// <summary>
/// The known scene the live run builds in a new world, in free air high above the ground (the server builds without
/// checks: <c>Constructor.SpawnConstruct</c>). Large cell centres are odd metres; small cells
/// are multiples of 0.5 m.
/// </summary>
internal static class Scene
{
    public const string FrameName = "StructureFrame";
    public const string MemoryName = "StructureLogicMemory";
    public const float Y = 81f;

    public static readonly PrefabKey Frame = new(Animator.StringToHash(FrameName));

    /// <summary>A cell no structure holds.</summary>
    public static readonly Cell FreeAir = new(410, 810, 410);

    /// <summary>A cell a scene frame holds.</summary>
    public static readonly Cell Occupied = new(90, 810, 90);

    /// <summary>Where scratch structures go, one large cell apart.</summary>
    public static Vector3 Scratch(int n) => new(201f + 2f * (n % 20), Y + 2f * (n / 20), 201f);

    /// <summary>Builds the scene; returns what it built, by name, for the log.</summary>
    public static List<string> Build()
    {
        var built = new List<string>();
        void Put(string prefab, float x, float y, float z, float yaw = 0f, int state = int.MaxValue)
        {
            var structure = Spawn(prefab, new Vector3(x, y, z), Quaternion.Euler(0f, yaw, 0f), state);
            built.Add($"{prefab} at ({x}, {y}, {z}) yaw {yaw}: {(structure == null ? "not built" : structure.ReferenceId.ToString())}");
        }

        // A 3 x 3 x 3 block of frames with its centre cell open: a sealed space if the game makes it a room.
        for (var x = 9; x <= 13; x += 2)
            for (var y = 81; y <= 85; y += 2)
                for (var z = 9; z <= 13; z += 2)
                    if (x != 11 || y != 83 || z != 11)
                        Put(FrameName, x, y, z);

        Put(FrameName, 17, 81, 11, state: 0);
        var painted = Spawn(FrameName, new Vector3(17, 83, 11), Quaternion.identity, int.MaxValue);
        painted?.SetCustomColor(3);
        built.Add($"painted frame: {painted?.ReferenceId}");
        var named = Spawn(FrameName, new Vector3(17, 85, 11), Quaternion.identity, int.MaxValue);
        if (named != null)
            named.CustomName = "Lattice Known";
        built.Add($"named frame: {named?.ReferenceId}");
        Put("StructureFrameIron", 19, 81, 11, 90f);

        for (var i = 0; i < 3; i++)
            Put("StructureCableStraight", 21f, Y, 11f + 0.5f * i);
        Put("StructureBattery", 21f, Y, 12.5f);
        for (var i = 0; i < 3; i++)
            Put("StructurePipeStraight", 23f, Y, 11f + 0.5f * i);
        for (var i = 0; i < 2; i++)
            Put("StructureChuteStraight", 25f, Y, 11f + 0.5f * i, 90f);
        Put("StructureMatterlinkCapacitor", 27f, Y, 11f);
        var memory = Spawn(MemoryName, new Vector3(29f, Y, 11f), Quaternion.identity, int.MaxValue);
        (memory as Assets.Scripts.Objects.Items.ISetable)?.SetLogicValue(Assets.Scripts.Objects.Motherboards.LogicType.Setting, 42);
        built.Add($"logic memory set to 42: {memory?.ReferenceId}");
        return built;
    }

    /// <summary>Builds a structure with the server's own call, at a build state (clamped to its last).</summary>
    public static Structure? Spawn(string prefabName, Vector3 position, Quaternion rotation, int state)
    {
        var prefab = Prefab.Find<Structure>(prefabName);
        if (prefab == null)
            return null;
        var built = Constructor.SpawnConstruct(new CreateStructureInstance(prefab, GridController.World.WorldToLocal(position), rotation, 0UL, -1));
        if (built != null && built.BuildStates.Count > 0)
        {
            built.CurrentBuildStateIndex = Mathf.Clamp(state, 0, built.BuildStates.Count - 1);
            built.UpdateStateVisualizer();
        }

        return built;
    }

    /// <summary>Every structure the world holds.</summary>
    public static List<Structure> AllStructures()
    {
        var all = new List<Structure>(GridController.AllStructuresPool.ActiveCount);
        GridController.AllStructuresPool.ForEach(s =>
        {
            if (s != null && !s.IsCursor)
                all.Add(s);
        });
        return all;
    }
}
