using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.Render;
using Lattice.Render.Gpu;
using Lattice.Tests.Styles;

namespace Lattice.Tests.Render;

/// <summary>Render's seams as fakes: a recording GPU, a camera, piece parts, and a material source.</summary>
internal sealed class RenderRig
{
    public FakeGpu Gpu { get; } = new();

    public FakeCamera Camera { get; } = new();

    public FakePieceParts Pieces { get; } = new();

    public List<string> Log { get; } = new();

    /// <summary>The bundle's material, one per key, unless a key is left out to stand for the stand-in's gaps.</summary>
    public Dictionary<MaterialKey, FakeMaterial> Bundle { get; } = new();

    public RenderRig(bool bundle = true)
    {
        if (!bundle)
            return;
        foreach (var key in Lattice.Render.Materials.MaterialTable.LatticeKeys)
            Bundle[key] = new FakeMaterial(key.Value, procedural: true);
        Bundle[Lattice.Render.Materials.MaterialTable.FillAndEdgeKey] = new FakeMaterial("ghost-edged", procedural: true);
    }

    public RenderPorts Ports => new(Gpu, Camera, Pieces, key => Bundle.TryGetValue(key, out var m) ? Option.Some<IGpuMaterial>(m) : Option<IGpuMaterial>.None, Log.Add);

    /// <summary>Drawing over the bench's styles, with frames run until its batches are built.</summary>
    public Renderer Over(StyleBench bench)
    {
        var renderer = RenderResults.Ok(Renderer.Over(bench.Styles.Plan, Ports, bench.Scheduler, bench.Styles.Watch));
        Settle(bench, renderer);
        return renderer;
    }

    /// <summary>World frames until the styles and Render's rebuilds have nothing left to do; returns the frames run.</summary>
    public static int Settle(StyleBench bench, Renderer renderer, int most = 10_000)
    {
        var idle = 0;
        for (var i = 0; i < most; i++)
        {
            bench.Frame();
            idle = !renderer.Rebuilding && !bench.Styles.Busy ? idle + 1 : 0;
            if (idle == 2)
                return i + 1;
        }

        throw new Xunit.Sdk.XunitException("the rebuilds never settled");
    }
}
