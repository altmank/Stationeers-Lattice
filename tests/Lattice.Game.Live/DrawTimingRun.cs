using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Assets.Scripts.Objects;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lattice.Game.Live;

/// <summary>
/// The cost of <see cref="Graphics.DrawMeshInstanced(Mesh, int, Material, Matrix4x4[], int, MaterialPropertyBlock, ShadowCastingMode, bool, int, Camera)"/>
/// on the game's own Mono and player (mode <c>render-timing</c>, from the main menu of the client copy with graphics):
/// per configuration (calls per frame, instances per call) the main-thread time of the submission loop and the whole
/// frame time against a baseline with no draws. The meshes come from a real piece prefab and the material from
/// Lattice's bundle (the game's instanced transparent shader when the bundle is missing).
/// </summary>
internal static class DrawTimingRun
{
    private const int Frames = 120;
    private const int Warm = 20;

    private static readonly (int Calls, int Instances)[] Configurations =
    {
        (0, 0), (250, 1), (250, 64), (250, 512), (250, 1023), (1000, 1), (1000, 64), (1000, 256), (2500, 16), (2500, 64),
    };

    public static IEnumerator Run(string bundlePath, Action<string> line)
    {
        var until = Time.realtimeSinceStartup + 120f;
        while (Time.realtimeSinceStartup < until && Prefab.AllPrefabs.Count < 500)
            yield return null;
        until = Time.realtimeSinceStartup + 20f;
        while (Time.realtimeSinceStartup < until && Camera.main == null)
            yield return null;
        yield return new WaitForSecondsRealtime(5f);
        line($"batch mode {Application.isBatchMode}; cameras {Camera.allCamerasCount}; prefabs {Prefab.AllPrefabs.Count}");

        line($"unity {Application.unityVersion}; graphics {SystemInfo.graphicsDeviceType} {SystemInfo.graphicsDeviceName}; " +
             $"instancing {SystemInfo.supportsInstancing}; cpu {SystemInfo.processorType} x{SystemInfo.processorCount}");
        var material = Material(bundlePath, line);
        var mesh = PieceMesh("StructureFrame", line);
        if (material == null || mesh == null)
        {
            line("FAIL timing: no material or mesh");
            yield break;
        }

        var camera = Camera.main;
        line("camera " + (camera == null ? "none (draws go to every camera)" : camera.name + " at " + camera.transform.position));
        var origin = camera == null ? Vector3.zero : camera.transform.position + camera.transform.forward * 30f;
        var matrices = new Matrix4x4[1023];
        for (var i = 0; i < matrices.Length; i++)
            matrices[i] = Matrix4x4.TRS(origin + new Vector3(i % 32 - 16, i / 32 % 32 - 16, i / 1024) * 0.6f, Quaternion.identity, Vector3.one * 0.25f);
        var block = new MaterialPropertyBlock();
        var colour = Shader.PropertyToID("_Color");
        block.SetColor(colour, new Color(0.3f, 0.7f, 1f, 0.3f));

        var frameTimes = new double[Frames];
        var submitTimes = new double[Frames];
        var watch = new Stopwatch();
        var last = Stopwatch.GetTimestamp();
        foreach (var (calls, instances) in Configurations)
        {
            for (var f = -Warm; f < Frames; f++)
            {
                watch.Restart();
                for (var c = 0; c < calls; c++)
                    Graphics.DrawMeshInstanced(mesh, 0, material, matrices, instances, block, ShadowCastingMode.Off, false, 0, camera);
                watch.Stop();
                yield return null;
                var now = Stopwatch.GetTimestamp();
                if (f >= 0)
                {
                    submitTimes[f] = watch.Elapsed.TotalMilliseconds;
                    frameTimes[f] = (now - last) * 1000.0 / Stopwatch.Frequency;
                }

                last = now;
            }

            Array.Sort(frameTimes);
            Array.Sort(submitTimes);
            var submit = submitTimes[Frames / 2];
            line($"TIMING calls {calls} instances {instances}: submit median {submit:F3} ms ({(calls == 0 ? 0 : submit * 1000.0 / calls):F2} us per call), " +
                 $"frame median {frameTimes[Frames / 2]:F2} ms p90 {frameTimes[Frames * 9 / 10]:F2} ms");
        }
    }

    private static Material? Material(string bundlePath, Action<string> line)
    {
        if (File.Exists(bundlePath))
        {
            var bundle = AssetBundle.LoadFromFile(bundlePath);
            if (bundle != null)
            {
                Material? ghost = null;
                foreach (var loaded in bundle.LoadAllAssets<Material>())
                {
                    line($"bundle material {loaded.name}: shader {loaded.shader.name} supported {loaded.shader.isSupported} instancing {loaded.enableInstancing} queue {loaded.renderQueue}");
                    if (loaded.name == "ghost")
                        ghost = loaded;
                }

                if (ghost != null)
                    return ghost;
            }

            line("bundle at " + bundlePath + " did not load a ghost material");
        }
        else
            line("no bundle at " + bundlePath);

        var shader = Shader.Find("Custom/Stationeers Transparent");
        line("stand-in shader " + (shader == null ? "missing" : shader.name + " supported " + shader.isSupported));
        return shader == null ? null : new Material(shader) { enableInstancing = true };
    }

    private static Mesh? PieceMesh(string prefabName, Action<string> line)
    {
        var filters = new List<MeshFilter>();
        foreach (var prefab in Prefab.AllPrefabs)
        {
            if (prefab == null || prefab.PrefabName != prefabName)
                continue;
            prefab.GetComponentsInChildren(true, filters);
            foreach (var filter in filters)
            {
                if (filter.sharedMesh == null)
                    continue;
                line($"mesh from {prefabName}: {filter.sharedMesh.vertexCount} vertices, {filter.sharedMesh.subMeshCount} submeshes");
                return filter.sharedMesh;
            }
        }

        line($"no mesh on {prefabName} (prefabs loaded {Prefab.AllPrefabs.Count}); a cube instead");
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var mesh = cube.GetComponent<MeshFilter>().sharedMesh;
        UnityEngine.Object.Destroy(cube);
        return mesh;
    }
}
