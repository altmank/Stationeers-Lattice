using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Lattice.Game.Live;

/// <summary>
/// One side of a with/without pair, block by block: every frame's whole time (unscaled delta), main-thread time, GPU
/// span, the GPU's time in the camera's transparent pass (Lattice's draws) and the render thread's time submitting that
/// pass; each block's medians; frames with no GPU time; and the player's draw-call counter at the last block.
/// </summary>
internal sealed class Side(string name)
{
    public string Name { get; } = name;

    public List<double> Frame { get; } = new(512);

    public List<double> Cpu { get; } = new(512);

    public List<double> Gpu { get; } = new(512);

    public List<double> Lattice { get; } = new(512);

    public List<double> Submit { get; } = new(512);

    public List<double> FrameBlocks { get; } = new(16);

    public List<double> CpuBlocks { get; } = new(16);

    public List<double> GpuBlocks { get; } = new(16);

    public List<double> LatticeBlocks { get; } = new(16);

    public int Missing { get; private set; }

    public long DrawCalls { get; private set; }

    public void Add(List<double> frames, List<FrameTimes> times, int missing, long drawCalls)
    {
        Frame.AddRange(frames);
        FrameBlocks.Add(RenderRun.Median(frames));
        if (times.Count > 0)
        {
            var cpu = times.ConvertAll(static t => t.CpuMs);
            var gpu = times.ConvertAll(static t => t.SpanMs);
            var lattice = times.ConvertAll(static t => t.LatticeMs);
            Cpu.AddRange(cpu);
            Gpu.AddRange(gpu);
            Lattice.AddRange(lattice);
            Submit.AddRange(times.ConvertAll(static t => t.SubmitMs));
            CpuBlocks.Add(RenderRun.Median(cpu));
            GpuBlocks.Add(RenderRun.Median(gpu));
            LatticeBlocks.Add(RenderRun.Median(lattice));
        }

        Missing += missing;
        DrawCalls = drawCalls;
    }
}

/// <summary>
/// Full-screen quads half a metre in front of the test camera in the ghost material, drawn in instanced calls of up to
/// 1,023: GPU work that grows with <see cref="Layers"/> while the CPU's part stays a few calls.
/// </summary>
internal sealed class Overdraw : IDisposable
{
    private const int Batch = 1023;
    private readonly Matrix4x4[] _matrices = new Matrix4x4[Batch];
    private readonly Material _material;
    private readonly Mesh _quad;
    private readonly Camera _camera;

    public Overdraw(Material ghost, Camera camera)
    {
        _material = new Material(ghost) { name = "Live overdraw", color = new Color(0.3f, 0.5f, 1f, 0.02f), enableInstancing = true };
        _material.DisableKeyword("LATTICE_PROCEDURAL");
        _material.DisableKeyword("LATTICE_ICON_PATTERN");
        _camera = camera;
        _quad = new Mesh { name = "Live overdraw quad" };
        _quad.SetVertices(new List<Vector3> { new(-1f, -1f, 0f), new(-1f, 1f, 0f), new(1f, 1f, 0f), new(1f, -1f, 0f) });
        _quad.SetNormals(new List<Vector3> { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
        _quad.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        _quad.RecalculateBounds();
    }

    public int Layers { get; set; }

    /// <summary>Places the layers in front of the camera where it stands now.</summary>
    public void Aim()
    {
        var t = _camera.transform;
        for (var i = 0; i < Batch; i++)
            _matrices[i] = Matrix4x4.TRS(t.position + t.forward * (0.5f + i * 0.0001f), t.rotation, new Vector3(4f, 4f, 1f));
    }

    public void Draw()
    {
        for (var left = Layers; left > 0; left -= Batch)
            Graphics.DrawMeshInstanced(_quad, 0, _material, _matrices, Math.Min(Batch, left), null, ShadowCastingMode.Off, false, 0, _camera);
    }

    public void Dispose()
    {
        Layers = 0;
        Object.Destroy(_material);
        Object.Destroy(_quad);
    }
}
