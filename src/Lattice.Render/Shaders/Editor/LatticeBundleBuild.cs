// Unity editor script (compiled by the Unity project build-shaders.ps1 makes, never by Lattice.Render): builds
// Lattice's shaders and one instanced material per key into the asset bundle `lattice.shaders`. The materials keep
// the instancing variants in the build (Unity strips variants no material in the build uses). Only Unity built-in
// types (shaders, materials) go in the bundle: script or TextMeshPro assets would not load against the game's dlls.
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class LatticeBundleBuild
{
    private const string Root = "Assets/Lattice";

    private static readonly (string Key, string Shader)[] Materials =
    {
        ("ghost", "Lattice/Ghost"),
        ("solid", "Lattice/Solid"),
        ("wire", "Lattice/Wire"),
        ("edge", "Lattice/Edge"),
        ("see-through", "Lattice/SeeThrough"),
        ("glow", "Lattice/Glow"),
        ("faded", "Lattice/Faded"),
        ("ghost-edged", "Lattice/GhostEdged"),
    };

    // The game's own shaders are compiled for D3D11, OpenGLCore and Vulkan (ShaderCompilerPlatform 4, 15 and 18 on
    // every Shader in rocketstation_Data/resources.assets), so Lattice's are too.
    private static readonly GraphicsDeviceType[] GameApis =
    {
        GraphicsDeviceType.Direct3D11,
        GraphicsDeviceType.Vulkan,
        GraphicsDeviceType.OpenGLCore,
    };

    public static void Build()
    {
        var output = Argument("-latticeOut");
        Directory.CreateDirectory(output);
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, GameApis);
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

        var folder = Root + "/Materials";
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(Root, "Materials");

        var assets = new string[Materials.Length * 2];
        for (var i = 0; i < Materials.Length; i++)
        {
            var (key, shaderName) = Materials[i];
            var shader = Shader.Find(shaderName);
            if (shader == null)
                Fail("shader " + shaderName + " did not compile or import");
            var path = folder + "/" + key + ".mat";
            AssetDatabase.DeleteAsset(path);
            var material = new Material(shader) { name = key, enableInstancing = true };
            if (key == "ghost-edged")
                material.EnableKeyword("LATTICE_EDGE_OWN_COLOUR");
            AssetDatabase.CreateAsset(material, path);
            assets[i * 2] = path;
            assets[i * 2 + 1] = AssetDatabase.GetAssetPath(shader);
        }

        AssetDatabase.SaveAssets();
        var build = new AssetBundleBuild { assetBundleName = "lattice.shaders", assetNames = assets };
        var manifest = BuildPipeline.BuildAssetBundles(
            output,
            new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
            BuildTarget.StandaloneWindows64);
        if (manifest == null)
            Fail("BuildAssetBundles failed");
        File.WriteAllText(Path.Combine(output, "lattice.shaders.unity"), Application.unityVersion);
        Debug.Log("LATTICE BUNDLE BUILT " + Path.Combine(output, "lattice.shaders"));
    }

    private static string Argument(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
                return args[i + 1];
        Fail("missing " + name);
        return "";
    }

    private static void Fail(string why)
    {
        Debug.LogError("LATTICE BUNDLE FAILED: " + why);
        EditorApplication.Exit(1);
    }
}
