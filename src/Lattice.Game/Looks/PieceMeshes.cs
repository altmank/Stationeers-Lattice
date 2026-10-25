using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.UI;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using UnityEngine;
using Vector = Lattice.Core.Catalogue.Vector;
using BuildState = Assets.Scripts.Objects.BuildState;

namespace Lattice.Game.Looks;

/// <summary>When a ghost part shows, by the build step the ghost is drawn at (decompile <c>BuildStateRenderMode.cs</c>).</summary>
internal abstract record PartShows
{
    private PartShows() { }

    /// <summary>At every step.</summary>
    public sealed record Always : PartShows;

    /// <summary>At <see cref="Step"/> and every later one (<c>OnMineAndPreviousStates</c>).</summary>
    public sealed record FromStep(int Step) : PartShows;

    /// <summary>At <see cref="Step"/> only (<c>OnMyState</c>, or under a build state's linked objects).</summary>
    public sealed record AtStep(int Step) : PartShows;

    /// <summary>The one <see cref="Always"/> value.</summary>
    public static PartShows Every { get; } = new Always();

    /// <summary>Whether the part shows at build step <paramref name="step"/>.</summary>
    public bool At(int step) => this switch
    {
        FromStep from => step >= from.Step,
        AtStep at => step == at.Step,
        _ => true,
    };
}

/// <summary>One mesh of a ghost: drawn with <see cref="Local"/> in the piece's own space, when <see cref="Shows"/> says.</summary>
internal readonly record struct PiecePart(Mesh Mesh, Matrix4x4 Local, PartShows Shows);

/// <summary>A piece's ghost meshes, where they come from, and the inactive renderers left out.</summary>
internal sealed record PieceDrawing(IReadOnlyList<PiecePart> Parts, LookSource Source, int Skipped);

/// <summary>
/// The meshes a ghost of each piece is drawn with, read from whatever prefab the game holds, so a modded piece is read
/// like any other. Best first: the prefab's renderers and build-state draw
/// data, else its blueprint mesh, else its bounds box, else one grid cell. Each piece is read once, on first ask, and
/// kept for the world; a later ask allocates nothing. Unity main thread only.
/// </summary>
internal sealed class PieceMeshes
{
    private readonly Dictionary<PrefabKey, PieceDrawing> _read = new(512);
    private readonly List<PiecePart> _scratch = new(32);

    /// <summary>How many pieces read so far drew from each source.</summary>
    public LookCounts Counts { get; private set; } = LookCounts.None;

    /// <summary>The piece's drawing; none when the game holds no structure prefab for it.</summary>
    public Option<PieceDrawing> Of(PrefabKey piece)
    {
        if (_read.TryGetValue(piece, out var known))
            return Option.Some(known);
        if (Prefab.Find(piece.Hash) is not Structure structure || structure == null)
            return Option<PieceDrawing>.None;
        var drawing = Read(structure);
        _read.Add(piece, drawing);
        Counts = Counts.With(drawing);
        return Option.Some(drawing);
    }

    private PieceDrawing Read(Structure structure)
    {
        _scratch.Clear();
        var skipped = Collect(structure, _scratch);
        if (_scratch.Count > 0)
        {
            var parts = _scratch.ToArray();
            return new PieceDrawing(parts, new LookSource.Meshes(parts.Length, ReadableOf(parts), ByStepOf(parts)), skipped);
        }

        if (BlueprintOf(structure) is { } blueprint)
            return new PieceDrawing(new[] { blueprint }, new LookSource.Blueprint(blueprint.Mesh.isReadable), skipped);
        if (BoundsOf(structure).TryGet(out var box))
            return new PieceDrawing(new[] { Cube(box) }, new LookSource.Box(), skipped);
        return new PieceDrawing(new[] { Cube(PieceBox.Cell(structure.GridSize)) }, new LookSource.Cell(), skipped);
    }

    /// <summary>
    /// What the structure prefab offers to draw it by, counted only: its bounds, its meshes, and its blueprint mesh.
    /// </summary>
    public static PrefabLook Summary(Structure structure)
    {
        var parts = new List<PiecePart>(16);
        Collect(structure, parts);
        var blueprint = BlueprintOf(structure);
        return new PrefabLook(
            BoundsOf(structure),
            parts.Count,
            ReadableOf(parts),
            ByStepOf(parts),
            blueprint is { } held ? Option.Some(held.Mesh.isReadable) : Option<bool>.None);
    }

    // Thing.Awake caches every MeshRenderer in the prefab's children, inactive included, that CanCacheRenderer
    // accepts (decompile Assets.Scripts.Objects/Thing.cs:3655-3675); Structure.CanCacheRenderer also refuses a
    // TextMesh's (Structure.cs:932-939). Structure.Awake then ties renderers to build states: a state's Visualizer
    // (state 0's defaults to the root MeshRenderer), shown by the state's RenderMode, and the state's
    // InitialDrawData; in batched mode the root MeshRenderer is destroyed and the draw data draws instead
    // (Structure.cs:863-900). UpdateStateVisualizer shows a state's LinkedGameObjects at that state only
    // (Structure.cs:1047-1060). Returns the inactive renderers no state shows, which are left out.
    private static int Collect(Structure structure, List<PiecePart> parts)
    {
        var root = structure.transform;
        var states = structure.BuildStates ?? new List<BuildState>(0);
        var rootRenderer = structure.GetComponent<MeshRenderer>();
        var batched = structure.structureRenderMode == StructureRenderMode.Batched;
        var skipped = 0;
        foreach (var renderer in structure.GetComponentsInChildren<MeshRenderer>(includeInactive: true))
        {
            if (renderer == null || !structure.CanCacheRenderer(renderer) || renderer.CompareTag("UIHelper"))
                continue;
            if (batched && renderer == rootRenderer)
                continue;
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                continue;
            var mesh = filter.sharedMesh;
            var local = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            var visualizerOf = VisualizerOf(states, renderer, rootRenderer);
            if (visualizerOf >= 0 && Active(renderer.transform, root))
            {
                parts.Add(new PiecePart(mesh, local, ShowsBy(states[visualizerOf].RenderMode, visualizerOf)));
                continue;
            }

            var linked = false;
            for (var j = 0; j < states.Count; j++)
                if (states[j] != null && Under(renderer.transform, states[j].LinkedGameObjects))
                {
                    parts.Add(new PiecePart(mesh, local, new PartShows.AtStep(j)));
                    linked = true;
                }

            if (linked)
                continue;
            if (visualizerOf < 0 && Active(renderer.transform, root))
                parts.Add(new PiecePart(mesh, local, PartShows.Every));
            else
                skipped++;
        }

        for (var j = 0; j < states.Count; j++)
        {
            var state = states[j];
            if (state != null && state.InitialDrawData.IsValid())
                parts.Add(new PiecePart(state.InitialDrawData.mesh, Matrix4x4.identity, ShowsBy(state.RenderMode, j)));
        }

        return skipped;
    }

    private static int VisualizerOf(List<BuildState> states, MeshRenderer renderer, MeshRenderer? rootRenderer)
    {
        for (var j = 0; j < states.Count; j++)
        {
            var state = states[j];
            if (state == null)
                continue;
            var visualizer = state.Visualizer != null ? state.Visualizer : j == 0 ? rootRenderer : null;
            if (visualizer != null && visualizer == renderer)
                return j;
        }

        return -1;
    }

    private static PartShows ShowsBy(BuildStateRenderMode mode, int step) => mode switch
    {
        BuildStateRenderMode.OnMyState => new PartShows.AtStep(step),
        _ => new PartShows.FromStep(step),
    };

    // A prefab is not in a scene, so activeInHierarchy does not say; every object between the renderer and the
    // prefab's root must be active itself.
    private static bool Active(Transform transform, Transform root)
    {
        for (var t = transform; t != null && t != root; t = t.parent)
            if (!t.gameObject.activeSelf)
                return false;
        return true;
    }

    private static bool Under(Transform transform, List<GameObject>? linked)
    {
        if (linked == null)
            return false;
        foreach (var go in linked)
            if (go != null && transform.IsChildOf(go.transform))
                return true;
        return false;
    }

    // The construction cursor draws a prefab with a Blueprint as its Wireframe, whose BlueprintMeshFilter holds the
    // mesh (decompile Assets.Scripts.Inventory/InventoryManager.cs:773-790, Assets.Scripts.UI/Wireframe.cs).
    private static PiecePart? BlueprintOf(Structure structure)
    {
        var blueprint = structure.Blueprint;
        if (blueprint == null)
            return null;
        var wireframe = blueprint.GetComponentInChildren<Wireframe>(includeInactive: true);
        if (wireframe == null || wireframe.BlueprintMeshFilter == null || wireframe.BlueprintMeshFilter.sharedMesh == null)
            return null;
        var local = blueprint.transform.worldToLocalMatrix * wireframe.BlueprintMeshFilter.transform.localToWorldMatrix;
        return new PiecePart(wireframe.BlueprintMeshFilter.sharedMesh, local, PartShows.Every);
    }

    // Thing.CheckBounds caches the prefab's bounds in its own space when they are empty (decompile
    // Assets.Scripts.Objects/Thing.cs:2394-2400, 5151); Structure adds its build states' draw data
    // (Structure.cs:2138-2152). Empty bounds are none.
    private static Option<PieceBox> BoundsOf(Structure structure)
    {
        structure.CheckBounds();
        var bounds = structure.Bounds;
        if (bounds.extents.magnitude <= float.Epsilon)
            return Option<PieceBox>.None;
        return Option.Some(new PieceBox(
            new Vector(bounds.center.x, bounds.center.y, bounds.center.z),
            new Vector(bounds.size.x, bounds.size.y, bounds.size.z)));
    }

    private static int ReadableOf(IReadOnlyList<PiecePart> parts)
    {
        var readable = 0;
        foreach (var part in parts)
            if (part.Mesh.isReadable)
                readable++;
        return readable;
    }

    private static int ByStepOf(IReadOnlyList<PiecePart> parts)
    {
        var byStep = 0;
        foreach (var part in parts)
            if (part.Shows is not PartShows.Always)
                byStep++;
        return byStep;
    }

    private static PiecePart Cube(PieceBox box) => new(
        UnitCube.Mesh,
        Matrix4x4.TRS(
            new Vector3((float)box.Centre.X, (float)box.Centre.Y, (float)box.Centre.Z),
            Quaternion.identity,
            new Vector3((float)box.Size.X, (float)box.Size.Y, (float)box.Size.Z)),
        PartShows.Every);
}

/// <summary>How many pieces read drew from each source, with their readable and per-step meshes and skipped renderers.</summary>
internal sealed record LookCounts(int Meshes, int Blueprint, int Box, int Cell, int ReadableMeshes, int AllMeshes, int ByStep, int Skipped)
{
    /// <summary>Nothing read yet.</summary>
    public static LookCounts None { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>These counts with one more piece's drawing.</summary>
    public LookCounts With(PieceDrawing drawing) => drawing.Source switch
    {
        LookSource.Meshes m => this with
        {
            Meshes = Meshes + 1,
            ReadableMeshes = ReadableMeshes + m.Readable,
            AllMeshes = AllMeshes + m.Count,
            ByStep = ByStep + m.ByStep,
            Skipped = Skipped + drawing.Skipped,
        },
        LookSource.Blueprint => this with { Blueprint = Blueprint + 1, Skipped = Skipped + drawing.Skipped },
        LookSource.Box => this with { Box = Box + 1, Skipped = Skipped + drawing.Skipped },
        _ => this with { Cell = Cell + 1, Skipped = Skipped + drawing.Skipped },
    };

    /// <inheritdoc />
    public override string ToString() =>
        $"looks: meshes {Meshes} ({AllMeshes} meshes, {ReadableMeshes} readable, {ByStep} by step), blueprint {Blueprint}, box {Box}, cell {Cell}; inactive renderers left out {Skipped}";
}

/// <summary>A unit cube centred on the origin, made once on first use, for pieces drawn as a box or a cell.</summary>
internal static class UnitCube
{
    private static Mesh? _mesh;

    /// <summary>The cube.</summary>
    public static Mesh Mesh => _mesh != null ? _mesh : _mesh = Make();

    private static Mesh Make()
    {
        var vertices = new List<Vector3>(24);
        var normals = new List<Vector3>(24);
        var triangles = new List<int>(36);
        Face(Vector3.right, Vector3.up, vertices, normals, triangles);
        Face(Vector3.left, Vector3.up, vertices, normals, triangles);
        Face(Vector3.up, Vector3.forward, vertices, normals, triangles);
        Face(Vector3.down, Vector3.forward, vertices, normals, triangles);
        Face(Vector3.forward, Vector3.up, vertices, normals, triangles);
        Face(Vector3.back, Vector3.up, vertices, normals, triangles);
        var mesh = new Mesh { name = "Lattice unit cube" };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void Face(Vector3 normal, Vector3 up, List<Vector3> vertices, List<Vector3> normals, List<int> triangles)
    {
        var side = Vector3.Cross(normal, up);
        var centre = normal * 0.5f;
        var first = vertices.Count;
        vertices.Add(centre + (-side - up) * 0.5f);
        vertices.Add(centre + (side - up) * 0.5f);
        vertices.Add(centre + (side + up) * 0.5f);
        vertices.Add(centre + (-side + up) * 0.5f);
        for (var i = 0; i < 4; i++)
            normals.Add(normal);
        triangles.Add(first);
        triangles.Add(first + 2);
        triangles.Add(first + 1);
        triangles.Add(first);
        triangles.Add(first + 3);
        triangles.Add(first + 2);
    }
}
