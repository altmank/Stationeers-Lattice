using System;
using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using Lattice.Render.Gpu;
using Lattice.Render.Materials;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lattice.Render.Unity;

/// <summary>
/// Lattice's materials for one world: the shader bundle shipped beside Lattice.Render.dll (<c>Bundle/lattice.shaders</c>,
/// built with the game's Unity by <c>build-shaders.ps1</c>), loaded at render start and unloaded with everything in it
/// at world end, so the next world loads it again. Without the bundle, the stand-in: the game's
/// transparent shader for the fill keys only (no edges, patterns or glow; development only).
/// </summary>
internal sealed class ShaderBundle
{
    /// <summary>The game's own transparent shader, the stand-in's fill.</summary>
    public const string StandInShader = "Custom/Stationeers Transparent";

    private readonly AssetBundle? _bundle;
    private readonly Dictionary<MaterialKey, IGpuMaterial> _materials;
    private readonly List<Material> _made;

    private ShaderBundle(AssetBundle? bundle, Dictionary<MaterialKey, IGpuMaterial> materials, List<Material> made)
    {
        _bundle = bundle;
        _materials = materials;
        _made = made;
    }

    /// <summary>Whether the materials are Lattice's own, not the stand-in.</summary>
    public bool Own => _bundle != null;

    public int Count => _materials.Count;

    public static string DefaultPath =>
        Path.Combine(Path.GetDirectoryName(typeof(ShaderBundle).Assembly.Location) ?? "", "Bundle", "lattice.shaders");

    /// <summary>
    /// Loads the bundle and its materials, logging the device, Unity's version and each shader's support; the
    /// stand-in when the bundle is missing or holds no material; none at all when the game's shader is gone too.
    /// </summary>
    public static ShaderBundle Load(string path, Action<string> log)
    {
        log($"Lattice render: device {SystemInfo.graphicsDeviceType}, Unity {Application.unityVersion}.");
        var materials = new Dictionary<MaterialKey, IGpuMaterial>(16);
        var bundle = File.Exists(path) ? AssetBundle.LoadFromFile(path) : null;
        if (bundle != null)
        {
            foreach (var key in MaterialTable.LatticeKeys)
                Take(bundle, key, materials, log);
            Take(bundle, MaterialTable.FillAndEdgeKey, materials, log);
            if (materials.Count > 0)
                return new ShaderBundle(bundle, materials, new List<Material>(0));
            bundle.Unload(true);
            log($"Lattice render: the shader bundle {path} holds no material Lattice knows.");
        }
        else
            log($"Lattice render: the shader bundle {path} did not load.");

        var made = new List<Material>(3);
        var shader = Shader.Find(StandInShader);
        if (shader == null)
        {
            log($"Lattice render: no stand-in either (the game's shader {StandInShader} is gone).");
            return new ShaderBundle(null, materials, made);
        }

        log($"Lattice render: drawing with the stand-in ({StandInShader}): fills only.");
        foreach (var key in new[] { MaterialKeys.Ghost, MaterialKeys.Solid, MaterialKeys.Wire })
        {
            var material = new Material(shader) { name = "Lattice stand-in " + key.Value, enableInstancing = true };
            made.Add(material);
            materials[key] = new UnityMaterial(material);
        }

        return new ShaderBundle(null, materials, made);
    }

    public Option<IGpuMaterial> Material(MaterialKey key) =>
        _materials.TryGetValue(key, out var material) ? Option.Some(material) : Option<IGpuMaterial>.None;

    /// <summary>Unloads the bundle with every object in it, and destroys the stand-in's materials (world end).</summary>
    public void Unload()
    {
        foreach (var material in _made)
            Object.Destroy(material);
        _made.Clear();
        _materials.Clear();
        if (_bundle != null)
            _bundle.Unload(true);
    }

    private static void Take(AssetBundle bundle, MaterialKey key, Dictionary<MaterialKey, IGpuMaterial> into, Action<string> log)
    {
        var material = bundle.LoadAsset<Material>(key.Value);
        if (material == null)
        {
            log($"Lattice render: the bundle has no material {key.Value}.");
            return;
        }

        log($"Lattice render: material {key.Value}, shader {material.shader.name}, supported {material.shader.isSupported}.");
        into[key] = new UnityMaterial(material);
    }
}
