using System.Collections.Generic;
using Lattice.Api;
using UnityEngine;

namespace Lattice.Render.Gpu;

/// <summary>
/// The shader names Lattice sets (the material contract, <see cref="MaterialKeys"/>). The Unity side turns each into
/// its property id once.
/// </summary>
internal enum ShaderProp
{
    /// <summary><c>_Color</c>: the final colour, eases, effects and dim applied on the CPU.</summary>
    Color = 0,

    /// <summary><c>_LatticePattern</c> (x: 0 none, 1 hatch, 2 cross-hatch, 3 dots).</summary>
    Pattern = 1,

    /// <summary><c>_LatticePatternColor</c>.</summary>
    PatternColor = 2,

    /// <summary><c>_LatticeEffect</c>: kind, period, low or duty, phase.</summary>
    Effect = 3,

    /// <summary><c>_LatticeTime</c>: seconds, unscaled.</summary>
    Time = 4,

    /// <summary><c>_LatticeSeeThrough</c>: strength, range.</summary>
    SeeThrough = 5,

    /// <summary><c>_LatticeEdge</c>: line style, width in pixels.</summary>
    Edge = 6,

    /// <summary><c>_LatticeBeyond</c>: mode, dim. A global (the viewer's default) and per draw (a style's own).</summary>
    Beyond = 7,

    /// <summary><c>_LatticeClipMode</c>, global: 0 none, 1 plane, 2 box.</summary>
    ClipMode = 8,

    /// <summary><c>_LatticeClipPlane</c>, global: xyz normal, w distance.</summary>
    ClipPlane = 9,

    /// <summary><c>_LatticeClipMin</c>, global.</summary>
    ClipMin = 10,

    /// <summary><c>_LatticeClipMax</c>, global.</summary>
    ClipMax = 11,

    /// <summary><c>_LatticeInstances</c>: a draw group's instance matrices (procedural draws of Lattice's own shaders).</summary>
    Instances = 12,

    /// <summary><c>_LatticeLocal</c>: the part's matrix in its piece's space (procedural draws).</summary>
    Local = 13,

    /// <summary><c>_LatticeEdgeColor</c>: the edge colour when one material draws fill and edge.</summary>
    EdgeColor = 14,
}

/// <summary>A mesh the GPU draws: how many submeshes it has.</summary>
internal interface IGpuMesh
{
    int Submeshes { get; }
}

/// <summary>
/// A material Lattice draws with: its own (from the bundle or the stand-in) or a consumer's override. Lattice never
/// writes a consumer's material; it copies it (<see cref="Copy"/>) and sets values per draw through property blocks.
/// </summary>
internal interface IGpuMaterial
{
    /// <summary>Whether GPU instancing is on (the contract's one requirement, checked at the call).</summary>
    bool Instanced { get; }

    /// <summary>The render queue it sets itself; none when it takes its shader's.</summary>
    Option<int> OwnQueue { get; }

    /// <summary>
    /// Whether its shader has Lattice's procedural variant (keyword <c>LATTICE_PROCEDURAL</c>: instance matrices from
    /// <c>_LatticeInstances</c> times <c>_LatticeLocal</c>). Lattice's own shaders have it; the stand-in and a
    /// consumer's override draw instanced.
    /// </summary>
    bool HasProcedural { get; }

    /// <summary>
    /// A copy at <paramref name="queue"/>, with the depth offset for drawing over a built original when
    /// <paramref name="overlay"/> (shaders that declare <c>_LatticeOffsetFactor</c> and <c>_LatticeOffsetUnits</c>),
    /// in its procedural variant when <paramref name="procedural"/>.
    /// </summary>
    IGpuMaterial Copy(int queue, bool overlay, bool procedural = false);

    /// <summary>Destroys a copy Lattice made. Never called on a consumer's own material.</summary>
    void Destroy();
}

/// <summary>Per-draw values: one style per batch, never per instance.</summary>
internal interface IGpuBlock
{
    void Set(ShaderProp prop, Vector4 value);

    void Set(ShaderProp prop, Matrix4x4 value);

    void Set(ShaderProp prop, IGpuBuffer buffer);
}

/// <summary>Instance matrices on the GPU for procedural draws: written only when a draw group is rebuilt.</summary>
internal interface IGpuBuffer
{
    /// <summary>How many matrices it holds at most.</summary>
    int Capacity { get; }

    /// <summary>Uploads the first <paramref name="count"/> matrices.</summary>
    void Set(Matrix4x4[] matrices, int count);

    void Release();
}

/// <summary>Lattice's calls into the engine's drawing: instanced draws, shader globals and property blocks.</summary>
internal interface IGpu
{
    IGpuBlock NewBlock();

    IGpuBuffer NewBuffer(int capacity);

    /// <summary>One instanced draw for the camera Lattice draws for, no shadows cast or received (at most 1023).</summary>
    void Draw(IGpuMesh mesh, int submesh, IGpuMaterial material, Matrix4x4[] matrices, int count, IGpuBlock block);

    /// <summary>
    /// One procedural draw of <paramref name="count"/> instances whose matrices the block's <see cref="ShaderProp.Instances"/>
    /// buffer holds, culled as one by <paramref name="bounds"/>; same camera and shadows as <see cref="Draw"/>.
    /// </summary>
    void DrawProcedural(IGpuMesh mesh, int submesh, IGpuMaterial material, Bounds bounds, int count, IGpuBlock block);

    void SetGlobal(ShaderProp prop, Vector4 value);
}

/// <summary>The camera Lattice draws for, read once a frame.</summary>
internal interface ICameraView
{
    /// <summary>Whether the game has a camera this frame; nothing draws without one.</summary>
    bool Ready { get; }

    Vector3 Position { get; }

    /// <summary>Fills six planes (normals inward) of the camera's view.</summary>
    void Frustum(Plane[] planes);

    /// <summary>Where a world point lands on screen, in pixels; false behind the camera.</summary>
    bool ToScreen(Vector3 world, out Vector2 screen);
}

/// <summary>When a part shows, by the build step a ghost is drawn at.</summary>
internal abstract record PartShows
{
    private PartShows() { }

    /// <summary>At every step.</summary>
    public sealed record Always : PartShows;

    /// <summary>At <see cref="Step"/> and every later one.</summary>
    public sealed record FromStep(int Step) : PartShows;

    /// <summary>At <see cref="Step"/> only.</summary>
    public sealed record AtStep(int Step) : PartShows;

    public static PartShows Every { get; } = new Always();

    public bool At(int step) => this switch
    {
        FromStep from => step >= from.Step,
        AtStep at => step == at.Step,
        _ => true,
    };

    /// <summary>The last step this part names; 0 for a part shown at every step.</summary>
    public int Last => this switch
    {
        FromStep from => from.Step,
        AtStep at => at.Step,
        _ => 0,
    };
}

/// <summary>One mesh of a piece, drawn with <see cref="Local"/> in the piece's own space when <see cref="Shows"/> says.</summary>
internal readonly record struct RenderPart(IGpuMesh Mesh, Matrix4x4 Local, PartShows Shows);

/// <summary>
/// The meshes a piece is drawn with, from whatever prefab the game holds (modded pieces included). Game implements it
/// over the prefabs; Render never reads a game type. Main thread; a piece is read once and kept for the world.
/// </summary>
internal interface IPieceParts
{
    /// <summary>The piece's parts; none when the game holds no prefab for it.</summary>
    Option<IReadOnlyList<RenderPart>> Of(PrefabKey piece);
}
