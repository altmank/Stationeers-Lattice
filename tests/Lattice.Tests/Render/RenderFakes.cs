using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.Render.Gpu;
using UnityEngine;

namespace Lattice.Tests.Render;

internal sealed class FakeMesh(string name, int submeshes = 1) : IGpuMesh
{
    public string Name { get; } = name;

    public int Submeshes { get; } = submeshes;

    public override string ToString() => Name;
}

/// <summary>
/// A material: a consumer's (made by the test) or one of Lattice's copies of it. <c>procedural</c> stands for Lattice's
/// own shaders, which have the procedural variant.
/// </summary>
internal sealed class FakeMaterial(string name, bool instanced = true, int? ownQueue = null, bool procedural = false) : IGpuMaterial
{
    public string Name { get; } = name;

    public bool Instanced { get; } = instanced;

    public bool HasProcedural { get; } = procedural;

    /// <summary>A copy in the procedural variant.</summary>
    public bool Procedural { get; private init; }

    public Option<int> OwnQueue => ownQueue is { } q ? Option.Some(q) : Option<int>.None;

    /// <summary>The queue it draws at: its own, else the queue a copy was made at.</summary>
    public int Queue { get; private init; }

    public bool Overlay { get; private init; }

    public FakeMaterial? CopyOf { get; private init; }

    public bool Destroyed { get; private set; }

    /// <summary>Copies made of it, for checking every one is destroyed.</summary>
    public List<FakeMaterial> Copies { get; } = new();

    /// <summary>Times anything was written to it; Lattice never writes a consumer's material.</summary>
    public int Writes { get; private set; }

    public IGpuMaterial Copy(int queue, bool overlay, bool procedural = false)
    {
        var copy = new FakeMaterial(Name + "@" + queue + (overlay ? "+offset" : "") + (procedural ? "+procedural" : ""), Instanced, ownQueue, HasProcedural)
        {
            Queue = ownQueue ?? queue, Overlay = overlay, CopyOf = this, Procedural = procedural,
        };
        Copies.Add(copy);
        return copy;
    }

    public void Destroy()
    {
        if (CopyOf is null)
            Writes++;
        Destroyed = true;
    }

    public override string ToString() => Name;
}

internal sealed class FakeBlock : IGpuBlock
{
    public readonly Vector4[] Values = new Vector4[16];
    public readonly Matrix4x4[] Matrices = new Matrix4x4[16];
    public readonly FakeBuffer?[] Buffers = new FakeBuffer?[16];
    public readonly bool[] IsSet = new bool[16];

    /// <summary>Whether a property was last set through the float setter, else through the vector one.</summary>
    public readonly bool[] AsFloat = new bool[16];
    public readonly bool[] AsVector = new bool[16];
    public int Writes;

    public void Set(ShaderProp prop, float value)
    {
        Values[(int)prop] = new Vector4(value, 0f, 0f, 0f);
        IsSet[(int)prop] = true;
        AsFloat[(int)prop] = true;
        Writes++;
    }

    public void Set(ShaderProp prop, Matrix4x4 value)
    {
        Matrices[(int)prop] = value;
        IsSet[(int)prop] = true;
        Writes++;
    }

    public void Set(ShaderProp prop, IGpuBuffer buffer)
    {
        Buffers[(int)prop] = (FakeBuffer)buffer;
        IsSet[(int)prop] = true;
        Writes++;
    }

    public void Set(ShaderProp prop, Vector4 value)
    {
        Values[(int)prop] = value;
        IsSet[(int)prop] = true;
        AsVector[(int)prop] = true;
        Writes++;
    }

    public Vector4 this[ShaderProp prop] => Values[(int)prop];
}

/// <summary>Instance matrices "on the GPU": what the last upload wrote.</summary>
internal sealed class FakeBuffer(int capacity) : IGpuBuffer
{
    public Matrix4x4[] Data { get; } = new Matrix4x4[capacity];

    public int Capacity => Data.Length;

    public int Uploads { get; private set; }

    public int Written { get; private set; }

    public bool Released { get; private set; }

    public void Set(Matrix4x4[] matrices, int count)
    {
        System.Array.Copy(matrices, Data, count);
        Written = count;
        Uploads++;
    }

    public void Release() => Released = true;
}

/// <summary>
/// One recorded draw. <see cref="First"/> is the first instance's world matrix either way: the page's first matrix, or
/// for a procedural draw the buffer's first matrix times the block's part matrix. Recording reuses a preallocated list,
/// so a warm frame allocates nothing.
/// </summary>
internal struct DrawCall
{
    public IGpuMesh Mesh;
    public int Submesh;
    public IGpuMaterial Material;
    public int Count;
    public Matrix4x4 First;
    public FakeBlock Block;
    public bool Procedural;
    public Bounds Bounds;
}

internal sealed class FakeGpu : IGpu
{
    public readonly List<DrawCall> Draws = new(4096);
    public readonly Vector4[] Globals = new Vector4[16];
    public readonly bool[] GlobalSet = new bool[16];
    public readonly bool[] GlobalAsFloat = new bool[16];
    public readonly bool[] GlobalAsVector = new bool[16];
    public int Calls;
    public int Blocks;
    public readonly List<FakeBuffer> Buffers = new();

    /// <summary>Every matrix the marker mesh was drawn with this frame (port and open-end marks).</summary>
    public readonly List<Matrix4x4> Marks = new(4096);

    public IGpuMesh Marker { get; } = new FakeMesh("marker");

    /// <summary>Off: draws are counted, not recorded, so a timing measures only the caller.</summary>
    public bool Recording { get; set; } = true;

    public IGpuBlock NewBlock()
    {
        Calls++;
        Blocks++;
        return new FakeBlock();
    }

    public void Draw(IGpuMesh mesh, int submesh, IGpuMaterial material, Matrix4x4[] matrices, int count, IGpuBlock block)
    {
        Calls++;
        if (!Recording)
            return;
        Draws.Add(new DrawCall { Mesh = mesh, Submesh = submesh, Material = material, Count = count, First = matrices[0], Block = (FakeBlock)block });
        if (ReferenceEquals(mesh, Marker))
            for (var i = 0; i < count; i++)
                Marks.Add(matrices[i]);
    }

    public IGpuBuffer NewBuffer(int capacity)
    {
        Calls++;
        var buffer = new FakeBuffer(capacity);
        Buffers.Add(buffer);
        return buffer;
    }

    public void DrawProcedural(IGpuMesh mesh, int submesh, IGpuMaterial material, Bounds bounds, int count, IGpuBlock block)
    {
        Calls++;
        if (!Recording)
            return;
        var fake = (FakeBlock)block;
        var first = fake.Buffers[(int)ShaderProp.Instances]!.Data[0] * fake.Matrices[(int)ShaderProp.Local];
        Draws.Add(new DrawCall { Mesh = mesh, Submesh = submesh, Material = material, Count = count, First = first, Block = fake, Procedural = true, Bounds = bounds });
    }

    public void SetGlobal(ShaderProp prop, Vector4 value)
    {
        Calls++;
        Globals[(int)prop] = value;
        GlobalSet[(int)prop] = true;
        GlobalAsVector[(int)prop] = true;
    }

    public void SetGlobal(ShaderProp prop, float value)
    {
        Calls++;
        Globals[(int)prop] = new Vector4(value, 0f, 0f, 0f);
        GlobalSet[(int)prop] = true;
        GlobalAsFloat[(int)prop] = true;
    }

    public Vector4 Global(ShaderProp prop) => Globals[(int)prop];

    public void NewFrame()
    {
        Draws.Clear();
        Marks.Clear();
    }
}

/// <summary>A camera at a point; sees everything unless planes are given.</summary>
internal sealed class FakeCamera : ICameraView
{
    public bool Ready { get; set; } = true;

    public Vector3 Position { get; set; }

    /// <summary>Six inward planes; null sees everything.</summary>
    public Plane[]? Planes { get; set; }

    public void Frustum(Plane[] planes)
    {
        for (var i = 0; i < 6; i++)
            planes[i] = Planes is null ? new Plane(Vector3.up, 1e9f) : Planes[i];
    }

    public bool ToScreen(Vector3 world, out Vector2 screen)
    {
        screen = new Vector2(world.x * 10f, world.z * 10f);
        return true;
    }

    /// <summary>A box-shaped view: sees only points inside [min, max].</summary>
    public static Plane[] Box(Vector3 min, Vector3 max) => new[]
    {
        new Plane(Vector3.right, -min.x), new Plane(Vector3.left, max.x),
        new Plane(Vector3.up, -min.y), new Plane(Vector3.down, max.y),
        new Plane(Vector3.forward, -min.z), new Plane(Vector3.back, max.z),
    };
}

internal sealed class FakePieceParts : IPieceParts
{
    private readonly Dictionary<PrefabKey, IReadOnlyList<RenderPart>> _parts = new();

    public int Asks { get; private set; }

    public FakePieceParts With(PrefabKey piece, params RenderPart[] parts)
    {
        _parts[piece] = parts;
        return this;
    }

    /// <summary>The parts of every piece not given its own; none draws such pieces as unknown.</summary>
    public IReadOnlyList<RenderPart>? Fallback { get; set; } = new[] { new RenderPart(new FakeMesh("cube"), Matrix4x4.identity, PartShows.Every) };

    public Option<IReadOnlyList<RenderPart>> Of(PrefabKey piece)
    {
        Asks++;
        return _parts.TryGetValue(piece, out var parts) ? Option.Some(parts)
            : Fallback is { } all ? Option.Some(all)
            : Option<IReadOnlyList<RenderPart>>.None;
    }
}

/// <summary>A text object that records what Render set on it.</summary>
internal sealed class FakeLabelSlot : Lattice.Render.Labels.ILabelSlot
{
    public string? Words { get; private set; }

    public int TextWrites { get; private set; }

    public TextStyle? Style { get; private set; }

    public Vector3 At { get; private set; }

    public float Alpha { get; private set; }

    public bool Visible { get; private set; }

    public void Text(string text)
    {
        Words = text;
        TextWrites++;
    }

    void Lattice.Render.Labels.ILabelSlot.Style(TextStyle style) => Style = style;

    public void Place(Vector3 world, float alpha)
    {
        At = world;
        Alpha = alpha;
        Visible = true;
    }

    public void Hide() => Visible = false;
}

/// <summary>The pool's text objects, and a font that lacks the glyphs in <see cref="Missing"/>.</summary>
internal sealed class FakeLabelSurface : Lattice.Render.Labels.ILabelSurface
{
    public List<FakeLabelSlot> Slots { get; } = new();

    public HashSet<char> Missing { get; } = new();

    public Lattice.Render.Labels.ILabelSlot NewSlot()
    {
        var slot = new FakeLabelSlot();
        Slots.Add(slot);
        return slot;
    }

    public bool Has(TextStyle style, char glyph) => !Missing.Contains(glyph);
}

/// <summary>Plan facts by hand: every ghost's step and every plan's title and bill count as set.</summary>
internal sealed class FakeLabelFacts : Lattice.Render.Labels.ILabelFacts
{
    public Dictionary<GhostId, int> Steps { get; } = new();

    public string? Title { get; set; }

    public bool PlanTitle(PlanId plan, out string title)
    {
        title = Title ?? "";
        return Title is not null;
    }

    public bool Step(GhostId ghost, out int step) => Steps.TryGetValue(ghost, out step);

    public bool BillCount(PlanId plan, PrefabKey piece, out int count)
    {
        count = 0;
        return false;
    }
}
