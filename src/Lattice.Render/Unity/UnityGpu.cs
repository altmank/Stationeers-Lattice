using System;
using Lattice.Api;
using Lattice.Render.Drawing;
using Lattice.Render.Gpu;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Lattice.Render.Unity;

/// <summary>Shader property ids for <see cref="ShaderProp"/>, turned once from the material contract's names.</summary>
internal static class ShaderIds
{
    private static int[]? _ids;

    public static int Of(ShaderProp prop)
    {
        var ids = _ids;
        if (ids is null)
        {
            ids = new int[ShaderNames.Count];
            for (var i = 0; i < ids.Length; i++)
                ids[i] = Shader.PropertyToID(ShaderNames.Of((ShaderProp)i));
            _ids = ids;
        }

        return ids[(int)prop];
    }

    /// <summary>Colours go through <c>SetColor</c>, which converts them into the player's colour space; the rest are plain vectors.</summary>
    public static bool IsColour(ShaderProp prop) => prop is ShaderProp.Color or ShaderProp.PatternColor or ShaderProp.EdgeColor;

    public static readonly int OffsetFactor = Shader.PropertyToID("_LatticeOffsetFactor");
    public static readonly int OffsetUnits = Shader.PropertyToID("_LatticeOffsetUnits");
}

/// <summary>A mesh, its submesh count read once.</summary>
internal sealed class UnityMesh(Mesh mesh) : IGpuMesh
{
    public Mesh Mesh { get; } = mesh;

    public int Submeshes { get; } = mesh.subMeshCount;
}

/// <summary>
/// A material: Lattice's own from the bundle or the stand-in, a consumer's as given, or a copy Lattice made (the only
/// kind it destroys). A copy never writes the material it was made from.
/// </summary>
internal sealed class UnityMaterial : IGpuMaterial
{
    private const string ProceduralKeyword = "LATTICE_PROCEDURAL";

    // Pulls an overlay toward the camera so it wins the depth test over the built original it covers.
    private const float OverlayOffset = -1f;

    private readonly bool _copy;

    public UnityMaterial(Material material, bool copy = false)
    {
        Material = material;
        _copy = copy;
        Instanced = material.enableInstancing;
        // A queue set on the material differs from its shader's; the shader's own queue is no queue of the material's.
        OwnQueue = material.shader != null && material.renderQueue != material.shader.renderQueue ? Option.Some(material.renderQueue) : Option<int>.None;
        HasProcedural = material.shader != null && material.shader.keywordSpace.FindKeyword(ProceduralKeyword).isValid;
    }

    public Material Material { get; }

    public bool Instanced { get; }

    public Option<int> OwnQueue { get; }

    public bool HasProcedural { get; }

    public IGpuMaterial Copy(int queue, bool overlay, bool procedural = false)
    {
        var copy = new Material(Material) { name = Material.name + " (Lattice " + queue + ")" };
        if (!OwnQueue.IsSome)
            copy.renderQueue = queue;
        if (overlay && copy.HasProperty(ShaderIds.OffsetFactor) && copy.HasProperty(ShaderIds.OffsetUnits))
        {
            copy.SetFloat(ShaderIds.OffsetFactor, OverlayOffset);
            copy.SetFloat(ShaderIds.OffsetUnits, OverlayOffset);
        }

        if (procedural && HasProcedural)
            copy.EnableKeyword(new LocalKeyword(copy.shader, ProceduralKeyword));
        return new UnityMaterial(copy, copy: true);
    }

    public void Destroy()
    {
        if (_copy)
            Object.Destroy(Material);
    }
}

/// <summary>Per-draw values in a <see cref="MaterialPropertyBlock"/>.</summary>
internal sealed class UnityBlock : IGpuBlock
{
    public MaterialPropertyBlock Block { get; } = new();

    public void Set(ShaderProp prop, Vector4 value)
    {
        if (ShaderIds.IsColour(prop))
            Block.SetColor(ShaderIds.Of(prop), new Color(value.x, value.y, value.z, value.w));
        else
            Block.SetVector(ShaderIds.Of(prop), value);
    }

    public void Set(ShaderProp prop, float value) => Block.SetFloat(ShaderIds.Of(prop), value);

    public void Set(ShaderProp prop, Matrix4x4 value) => Block.SetMatrix(ShaderIds.Of(prop), value);

    public void Set(ShaderProp prop, IGpuBuffer buffer) => Block.SetBuffer(ShaderIds.Of(prop), ((UnityBuffer)buffer).Buffer);
}

/// <summary>A draw group's instance matrices: a structured buffer of 64-byte matrices.</summary>
internal sealed class UnityBuffer(int capacity) : IGpuBuffer
{
    private const int MatrixBytes = 64;

    public GraphicsBuffer Buffer { get; } = new(GraphicsBuffer.Target.Structured, capacity, MatrixBytes);

    public int Capacity { get; } = capacity;

    public void Set(Matrix4x4[] matrices, int count) => Buffer.SetData(matrices, 0, 0, count);

    public void Release() => Buffer.Release();
}

/// <summary>Lattice's draws for the camera it draws for: no shadows cast or received, layer 0.</summary>
internal sealed class UnityGpu(MainCamera camera) : IGpu
{
    private UnityMesh? _marker;

    public IGpuBlock NewBlock() => new UnityBlock();

    public IGpuBuffer NewBuffer(int capacity) => new UnityBuffer(capacity);

    public IGpuMesh Marker => _marker ??= new UnityMesh(Resources.GetBuiltinResource<Mesh>("Cube.fbx"));

    public void Draw(IGpuMesh mesh, int submesh, IGpuMaterial material, Matrix4x4[] matrices, int count, IGpuBlock block) =>
        Graphics.DrawMeshInstanced(((UnityMesh)mesh).Mesh, submesh, ((UnityMaterial)material).Material, matrices, count, ((UnityBlock)block).Block,
            ShadowCastingMode.Off, false, 0, camera.Current);

    public void DrawProcedural(IGpuMesh mesh, int submesh, IGpuMaterial material, Bounds bounds, int count, IGpuBlock block) =>
        Graphics.DrawMeshInstancedProcedural(((UnityMesh)mesh).Mesh, submesh, ((UnityMaterial)material).Material, bounds, count, ((UnityBlock)block).Block,
            ShadowCastingMode.Off, false, 0, camera.Current);

    public void SetGlobal(ShaderProp prop, Vector4 value) => Shader.SetGlobalVector(ShaderIds.Of(prop), value);

    public void SetGlobal(ShaderProp prop, float value) => Shader.SetGlobalFloat(ShaderIds.Of(prop), value);
}

/// <summary>The game's main camera, read live: none while the game has none.</summary>
internal sealed class MainCamera : ICameraView
{
    public Camera? Current
    {
        get
        {
            var camera = Camera.main;
            return camera != null ? camera : null;
        }
    }

    public bool Ready => Current is not null;

    public Vector3 Position => Current is { } camera ? camera.transform.position : Vector3.zero;

    public Quaternion Rotation => Current is { } camera ? camera.transform.rotation : Quaternion.identity;

    public void Frustum(Plane[] planes)
    {
        if (Current is { } camera)
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
    }

    public bool ToScreen(Vector3 world, out Vector2 screen)
    {
        screen = default;
        if (Current is not { } camera)
            return false;
        var point = camera.WorldToScreenPoint(world);
        if (point.z <= 0f)
            return false;
        screen = new Vector2(point.x, point.y);
        return true;
    }
}

/// <summary>Engine objects to Render's seams and back.</summary>
internal sealed class UnityObjects : IUnityObjects
{
    public IGpuMaterial Material(Material material) => new UnityMaterial(material);

    public IGpuMesh Mesh(Mesh mesh) => new UnityMesh(mesh);

    public Material Unwrap(IGpuMaterial material) => ((UnityMaterial)material).Material;
}
