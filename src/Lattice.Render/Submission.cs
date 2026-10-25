using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Slices;
using Lattice.Core.Styles;
using Lattice.Render.Claims;
using Lattice.Render.Gpu;
using Lattice.Render.Marks;
using Lattice.Render.Materials;
using UnityEngine;

namespace Lattice.Render;

/// <summary>What the last frame submitted, for the perf run and the tests: calls, not batches.</summary>
internal readonly record struct SubmissionCounts(int Batches, int Visible, int Groups, int Calls, int Instances, int SkippedLayers, int UnknownPieces);

/// <summary>
/// A piece as Render draws it: the parts it shows as it will be built (the game's cursor draws the prefab as it is,
/// <c>InventoryManager.cs:700-712</c>), read once per piece.
/// </summary>
internal sealed class PieceDraw(PrefabKey key, RenderPart[] parts)
{
    public PrefabKey Key { get; } = key;

    public RenderPart[] Parts { get; } = parts;

    public static PieceDraw Of(PrefabKey key, IReadOnlyList<RenderPart> all)
    {
        var last = 0;
        for (var i = 0; i < all.Count; i++)
            last = Math.Max(last, all[i].Shows.Last);
        var shown = 0;
        for (var i = 0; i < all.Count; i++)
            if (all[i].Shows.At(last))
                shown++;
        var parts = new RenderPart[shown];
        var at = 0;
        for (var i = 0; i < all.Count; i++)
            if (all[i].Shows.At(last))
                parts[at++] = all[i];
        return new PieceDraw(key, parts);
    }
}

/// <summary>One look layer of a draw group: the material it draws with, how, and its property block.</summary>
internal sealed class LayerDraw(IGpuBlock block)
{
    public IGpuBlock Block { get; } = block;

    public IGpuMaterial? Material { get; set; }

    /// <summary>Drawn from the group's instance buffer (Lattice's own shader), else instanced from matrix pages.</summary>
    public bool Procedural { get; set; }

    /// <summary>The layer's colour before effects; the frame's effect scales its alpha.</summary>
    public Vector4 Colour { get; set; }

    /// <summary>The edge colour before effects, for the one material that draws fill and edge.</summary>
    public Vector4 EdgeColour { get; set; }

    public bool Combined { get; set; }

    public bool On { get; set; }
}

/// <summary>
/// What merges into one draw group: one piece, one look and one start, placement or existing part, and
/// whether a claim draws the mesh itself (<see cref="ClaimParts.Mesh"/>: the group draws no fill). Core's batches of that
/// key in every chunk in view draw from one instance buffer.
/// </summary>
internal readonly record struct GroupKey(PrefabKey Piece, Style Style, Rgba Fill, Rgba Edge, Rgba Glow, long Start, bool Existing, bool MeshClaimed);

/// <summary>Render's view of one of Core's batches: the group it draws in, and what that group last copied of it.</summary>
internal sealed class BatchDraw(Batch batch)
{
    public Batch Batch { get; } = batch;

    public PrefabKey Piece;
    public DrawGroup? Group;
    public ResolvedLook? Look;
    public long Start = -1;
    public bool Visible;

    /// <summary>The batch version the group's drawn instances hold; -1 before the first copy.</summary>
    public long Copied = -1;

    /// <summary>The batch version a rebuild in progress is copying.</summary>
    public long Copying = -1;

    /// <summary>The claim that draws it, if any, worked out at <see cref="ClaimStamp"/> for <see cref="ClaimSignature"/>.</summary>
    public ClaimEntry? Claim;
    public long ClaimStamp;
    public Signature? ClaimSignature;

    /// <summary>The last sort that found it in the draw plan.</summary>
    public long Sorted;
}

/// <summary>
/// One draw group: its member batches, their visible instances' matrices (an instance buffer for procedural draws, and
/// per-part pages of 1023 for instanced draws, kept only while a layer needs them), and its look layers. Rebuilt in the
/// drawing budget by <see cref="Rebuilds"/>; what a frame draws is always the last complete rebuild.
/// </summary>
internal sealed class DrawGroup
{
    public const int Layers = 4;

    public DrawGroup(GroupKey key, PieceDraw piece, IGpu gpu)
    {
        Key = key;
        Piece = piece;
        var parts = piece.Parts.Length;
        Pages = new List<Matrix4x4[]>[parts];
        NextPages = new List<Matrix4x4[]>[parts];
        for (var i = 0; i < parts; i++)
        {
            Pages[i] = new List<Matrix4x4[]>(1);
            NextPages[i] = new List<Matrix4x4[]>(1);
        }

        for (var i = 0; i < Layers; i++)
            Layer[i] = new LayerDraw(gpu.NewBlock());
        ShowsPorts = key.Style.Ports.TryGet(out var ports) && ports == ShowMode.Shown;
        ShowsOpenEnds = key.Style.OpenEnds.TryGet(out var open) && open == ShowMode.Shown;
        ShowsFlow = key.Style.FlowArrows.TryGet(out var flow) && flow == ShowMode.Shown;
        ShowsWaits = key.Style.WaitsLinks.TryGet(out var waits) && waits == ShowMode.Shown;
        PortLayer = new LayerDraw(gpu.NewBlock());
        OpenLayer = new LayerDraw(gpu.NewBlock());
        FlowLayer = new LayerDraw(gpu.NewBlock());
        WaitsLayer = new LayerDraw(gpu.NewBlock());
    }

    public GroupKey Key { get; }

    public PieceDraw Piece { get; }

    public readonly List<BatchDraw> Members = new(4);
    public readonly LayerDraw[] Layer = new LayerDraw[Layers];

    /// <summary>The newest resolved look among its members (equal in value for all of them).</summary>
    public ResolvedLook? Look;
    public ResolvedLook? StyledLook;
    public long MaterialsRevision;
    public bool Animated;

    /// <summary>When its look began to play, in unscaled seconds: the first frame it drew; -1 before.</summary>
    public float Since = -1f;
    public bool Queued;
    public bool Retired;

    public Matrix4x4[] Staging = Array.Empty<Matrix4x4>();
    public IGpuBuffer? Buffer;
    public int Count;
    public bool Built;
    public Bounds Bounds;

    /// <summary>A layer draws instanced (an override or the stand-in), so rebuilds also write per-part pages.</summary>
    public bool NeedsPages;
    public bool HasPages;
    public List<Matrix4x4[]>[] Pages;
    public List<Matrix4x4[]>[] NextPages;

    /// <summary>Its look shows every port of its pieces (<see cref="Style.Ports"/>).</summary>
    public bool ShowsPorts { get; }

    /// <summary>Its look shows the ports nothing joins (<see cref="Style.OpenEnds"/>).</summary>
    public bool ShowsOpenEnds { get; }

    /// <summary>Its look shows which way things flow through its pieces' ports (<see cref="Style.FlowArrows"/>).</summary>
    public bool ShowsFlow { get; }

    /// <summary>Its look links each ghost to the ghosts it waits for (<see cref="Style.WaitsLinks"/>).</summary>
    public bool ShowsWaits { get; }

    /// <summary>Rebuilds write marks for its visible instances.</summary>
    public bool Marks => ShowsPorts || ShowsOpenEnds || ShowsFlow || ShowsWaits;

    /// <summary>Drawn marks, in pages of 1023, and the ones a rebuild in progress writes; a complete rebuild swaps them.</summary>
    public MarkPages Ports = new();
    public MarkPages OpenEnds = new();
    public MarkPages NextPorts = new();
    public MarkPages NextOpenEnds = new();
    public MarkPages Flow = new();
    public MarkPages NextFlow = new();
    public MarkPages Waits = new();
    public MarkPages NextWaits = new();

    /// <summary>The end facts' stamp its drawn marks were worked out at; -1 before.</summary>
    public long MarksAt = -1;

    public readonly LayerDraw PortLayer;
    public readonly LayerDraw OpenLayer;
    public readonly LayerDraw FlowLayer;
    public readonly LayerDraw WaitsLayer;

    public bool Existing => Key.Existing;

    public bool MeshClaimed => Key.MeshClaimed;
}

/// <summary>Mark matrices in pages of 1023 taken from the page pool: what one instanced call per page draws.</summary>
internal sealed class MarkPages
{
    public readonly List<Matrix4x4[]> Pages = new(1);

    public int Count;

    public void Add(Matrix4x4 mark, PagePool pool)
    {
        var page = Count / Batch.PageSize;
        if (page == Pages.Count)
            Pages.Add(pool.Rent());
        Pages[page][Count % Batch.PageSize] = mark;
        Count++;
    }

    public void Clear(PagePool pool)
    {
        pool.Return(Pages);
        Count = 0;
    }
}

/// <summary>Matrix pages for reuse: a rebuild takes its pages here and gives back the ones it replaced.</summary>
internal sealed class PagePool
{
    private readonly Stack<Matrix4x4[]> _free = new(64);

    public int Free => _free.Count;

    public Matrix4x4[] Rent() => _free.Count > 0 ? _free.Pop() : new Matrix4x4[Batch.PageSize];

    public void Return(List<Matrix4x4[]> pages)
    {
        for (var i = 0; i < pages.Count; i++)
            _free.Push(pages[i]);
        pages.Clear();
    }
}

/// <summary>Render's state per batch, per draw group and per piece, shared by the submission and the rebuilds.</summary>
internal sealed class DrawState(RenderPorts ports)
{
    private readonly Dictionary<Batch, BatchDraw> _batches = new(256);
    private readonly Dictionary<GroupKey, DrawGroup> _byKey = new(256);
    private readonly Dictionary<PrefabKey, PieceDraw?> _pieces = new(256);
    private readonly HashSet<PrefabKey> _unknown = new();
    private readonly List<BatchDraw> _gone = new(64);

    public PagePool Pool { get; } = new();

    public List<DrawGroup> Groups { get; } = new(256);

    public Queue<DrawGroup> Dirty { get; } = new(64);

    public int UnknownPieces => _unknown.Count;

    public IGpu Gpu => ports.Gpu;

    /// <summary>Ports and joins for the groups that draw marks; none when the seat has no world to ask.</summary>
    public IMarkFacts? Facts => ports.Facts;

    /// <summary>Rises when a piece's parts change (a piece look registered or disposed), so the next sort re-places its batches.</summary>
    public long PieceStamp { get; private set; }

    /// <summary>
    /// Forgets what a piece is drawn with: its batches are placed again at the next sort, into new groups read from the
    /// piece's current parts; the old groups draw until their members have left and retire when rebuilt empty.
    /// </summary>
    public void Repiece(PrefabKey key)
    {
        _pieces.Remove(key);
        _unknown.Remove(key);
        for (var i = 0; i < Groups.Count; i++)
        {
            var group = Groups[i];
            if (group.Key.Piece == key && _byKey.TryGetValue(group.Key, out var held) && ReferenceEquals(held, group))
                _byKey.Remove(group.Key);
        }

        foreach (var draw in _batches.Values)
            if (draw.Piece == key)
                draw.Look = null;
        PieceStamp++;
    }

    /// <summary>Render's view of a batch, made on first sight.</summary>
    public BatchDraw Track(Batch batch)
    {
        if (!_batches.TryGetValue(batch, out var draw))
        {
            draw = new BatchDraw(batch);
            _batches[batch] = draw;
        }

        return draw;
    }

    /// <summary>
    /// Puts a batch in the group its piece, look, start, part kind and claim say; both groups it leaves and joins are
    /// queued for a rebuild. A piece without meshes, or a batch a claim draws all of, joins none.
    /// </summary>
    public void Place(BatchDraw draw, ResolvedLook look)
    {
        var batch = draw.Batch;
        draw.Piece = batch.Piece;
        draw.Look = look;
        draw.Start = batch.Start;
        DrawGroup? group = null;
        if (draw.Claim is not { Parts: ClaimParts.Everything } && Piece(batch.Piece) is { } piece)
        {
            var existing = batch.Part == PartKind.Existing;
            var key = new GroupKey(batch.Piece, look.Style, look.Fill, look.Edge, look.Glow, batch.Start, existing, draw.Claim is not null);
            if (!_byKey.TryGetValue(key, out group) || group.Retired)
            {
                group = new DrawGroup(key, piece, ports.Gpu);
                _byKey[key] = group;
                Groups.Add(group);
            }

            group.Look = look;
        }

        if (ReferenceEquals(draw.Group, group))
            return;
        if (draw.Group is { } left)
        {
            left.Members.Remove(draw);
            MarkDirty(left);
        }

        draw.Group = group;
        draw.Copied = -1;
        if (group is null)
            return;
        group.Members.Add(draw);
        MarkDirty(group);
    }

    /// <summary>
    /// Lets go of the batches a sort did not find in the draw plan (Core pooled them): they leave their group and claim,
    /// and the group is rebuilt without them.
    /// </summary>
    public void DropUnsorted(long sort)
    {
        foreach (var draw in _batches.Values)
            if (draw.Sorted != sort)
                _gone.Add(draw);
        for (var i = 0; i < _gone.Count; i++)
        {
            var draw = _gone[i];
            _batches.Remove(draw.Batch);
            draw.Visible = false;
            draw.Claim?.Members.Remove(draw);
            draw.Claim = null;
            if (draw.Group is { } group)
            {
                group.Members.Remove(draw);
                MarkDirty(group);
                draw.Group = null;
            }
        }

        _gone.Clear();
    }

    public void MarkDirty(DrawGroup group)
    {
        if (group.Queued || group.Retired)
            return;
        group.Queued = true;
        Dirty.Enqueue(group);
    }

    /// <summary>A rebuilt group with no members leaves: its buffer and pages are given back.</summary>
    public void Retire(DrawGroup group)
    {
        group.Retired = true;
        group.Buffer?.Release();
        group.Buffer = null;
        for (var p = 0; p < group.Pages.Length; p++)
        {
            Pool.Return(group.Pages[p]);
            Pool.Return(group.NextPages[p]);
        }

        group.Ports.Clear(Pool);
        group.OpenEnds.Clear(Pool);
        group.NextPorts.Clear(Pool);
        group.NextOpenEnds.Clear(Pool);
        group.Flow.Clear(Pool);
        group.NextFlow.Clear(Pool);
        group.Waits.Clear(Pool);
        group.NextWaits.Clear(Pool);
        if (_byKey.TryGetValue(group.Key, out var held) && ReferenceEquals(held, group))
            _byKey.Remove(group.Key);
        Groups.Remove(group);
    }

    /// <summary>Releases every instance buffer (world end).</summary>
    public void End()
    {
        for (var i = 0; i < Groups.Count; i++)
        {
            Groups[i].Buffer?.Release();
            Groups[i].Buffer = null;
        }
    }

    private PieceDraw? Piece(PrefabKey key)
    {
        if (_pieces.TryGetValue(key, out var known))
            return known;
        PieceDraw? piece = null;
        if (ports.Pieces.Of(key).TryGet(out var parts) && parts.Count > 0)
            piece = PieceDraw.Of(key, parts);
        else if (_unknown.Add(key))
            ports.Log($"Lattice render: no meshes for piece {key}; its ghosts are not drawn.");
        _pieces[key] = piece;
        return piece;
    }
}

/// <summary>
/// Sorting and draw group rebuilds in the drawing budget: a Lattice job of the drawing kind, after the styles' own
/// job and before every consumer's. Once per styles frame it sorts Core's batches into draw groups, 128 a step: a
/// batch whose look, piece or start changed moves group, and a batch whose chunk entered or left the camera's view or
/// whose version changed queues its group. Then a step copies up to 1023 visible instances into a queued group's
/// staging matrices, or writes one page of one part for instanced layers; a complete rebuild uploads the instance buffer
/// and replaces the drawn pages in one go. A member that changed during the copy starts the group again. With nothing
/// left it waits for the next frame.
/// </summary>
internal sealed class Rebuilds(IDrawPlan plan, ICameraView camera, DrawState state, ClaimTable claims) : ISlicedWork
{
    private const int SortPerStep = 128;

    private readonly Plane[] _frustum = new Plane[6];
    private long _sortedFrame = -1;
    private long _sortedClaims = -1;
    private long _sortedPieces;
    private long _sorts;
    private bool _sorting;
    private int _sort;
    private int _seen;
    private DrawGroup? _group;
    private bool _paging;
    private int _member;
    private int _index;
    private int _written;
    private int _part;
    private int _page;
    private Vector3 _min;
    private Vector3 _max;
    private bool _ended;
    private readonly List<EndMark> _ends = new(16);
    private readonly List<FlowMark> _flows = new(8);
    private long _endsStamp = -1;
    private long _marksStamp;

    /// <summary>What copying one instance costs of a step's budget when its group also writes marks (its ports are asked).</summary>
    private const int MarkedInstanceCost = 8;

    // A chunk's box is grown by this much on every side, for pieces that reach past their cell's chunk.
    public const float ChunkMargin = 8f;

    /// <summary>A sort or a rebuild is under way, a rebuild is queued, or a claim change waits for its sort.</summary>
    public bool Busy => _sorting || _group is not null || state.Dirty.Count > 0 || _sortedClaims != claims.Stamp || claims.Moved || _sortedPieces != state.PieceStamp
        || state.Facts is { } ends && ends.Stamp != _endsStamp;

    /// <summary>The batches in view at the last complete sort.</summary>
    public int Visible { get; private set; }

    public void End() => _ended = true;

    public StepOutcome Step()
    {
        if (_ended)
            return StepOutcome.Done;
        if (_group is not null)
        {
            if (_paging)
                WritePage();
            else
                Copy();
            return StepOutcome.More;
        }

        if (!_sorting && camera.Ready)
            claims.Refresh();
        if (!_sorting && (_sortedFrame != plan.Frame || _sortedClaims != claims.Stamp || _sortedPieces != state.PieceStamp) && camera.Ready)
        {
            _sortedFrame = plan.Frame;
            _sortedClaims = claims.Stamp;
            _sortedPieces = state.PieceStamp;
            camera.Frustum(_frustum);
            _sorting = true;
            _sorts++;
            _sort = 0;
            _seen = 0;
        }

        if (_sorting)
        {
            Sort();
            return StepOutcome.More;
        }

        RefreshMarks();
        if (state.Dirty.Count == 0)
            return StepOutcome.NextFrame;
        _group = state.Dirty.Dequeue();
        _group.Queued = false;
        Begin();
        return StepOutcome.More;
    }

    private void Sort()
    {
        var count = plan.BatchCount;
        var until = Math.Min(count, _sort + SortPerStep);
        for (; _sort < until; _sort++)
            if (Sort(plan.BatchAt(_sort)))
                _seen++;
        if (_sort < count)
            return;
        _sorting = false;
        Visible = _seen;
        state.DropUnsorted(_sorts);
    }

    /// <summary>A batch to its claim and into its group, its visibility and version noted; whether it is in view.</summary>
    private bool Sort(Batch batch)
    {
        var draw = state.Track(batch);
        draw.Sorted = _sorts;
        var look = batch.Look;
        if (claims.Settle(draw) || !ReferenceEquals(draw.Look, look) || draw.Piece != batch.Piece || draw.Start != batch.Start)
            state.Place(draw, look);
        if (draw.Group is not { } group)
        {
            if (draw.Claim is null)
                return false;
            draw.Visible = batch.Count > 0 && !look.Hidden && InView(batch.Chunk);
            return draw.Visible;
        }

        var visible = batch.Count > 0 && !look.Hidden && InView(batch.Chunk);
        if (visible != draw.Visible)
        {
            draw.Visible = visible;
            state.MarkDirty(group);
        }
        else if (visible && draw.Copied != batch.Version)
            state.MarkDirty(group);

        return visible;
    }

    private bool InView(ChunkKey chunk)
    {
        var min = new Vector3(chunk.X * ChunkKey.Metres - ChunkMargin, chunk.Y * ChunkKey.Metres - ChunkMargin, chunk.Z * ChunkKey.Metres - ChunkMargin);
        var size = ChunkKey.Metres + 2 * ChunkMargin;
        for (var i = 0; i < 6; i++)
        {
            var n = _frustum[i].normal;
            // The box corner farthest along the plane's normal: outside if even it is behind the plane.
            var x = n.x >= 0 ? min.x + size : min.x;
            var y = n.y >= 0 ? min.y + size : min.y;
            var z = n.z >= 0 ? min.z + size : min.z;
            if (n.x * x + n.y * y + n.z * z + _frustum[i].distance < 0)
                return false;
        }

        return true;
    }

    private void Begin()
    {
        var group = _group!;
        var total = 0;
        for (var m = 0; m < group.Members.Count; m++)
            if (group.Members[m].Visible)
                total += group.Members[m].Batch.Count;
        if (group.Staging.Length < total)
            group.Staging = new Matrix4x4[Capacity(total)];
        _paging = false;
        _member = 0;
        _index = 0;
        _written = 0;
        _min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        _max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        if (group.Marks)
        {
            group.NextPorts.Clear(state.Pool);
            group.NextOpenEnds.Clear(state.Pool);
            group.NextFlow.Clear(state.Pool);
            group.NextWaits.Clear(state.Pool);
            _marksStamp = state.Facts?.Stamp ?? 0;
        }
    }

    /// <summary>When what joins a port may have changed, every group drawing marks from an older stamp is queued.</summary>
    private void RefreshMarks()
    {
        if (state.Facts is not { } ends)
            return;
        var stamp = ends.Stamp;
        if (stamp == _endsStamp)
            return;
        _endsStamp = stamp;
        var groups = state.Groups;
        for (var i = 0; i < groups.Count; i++)
            if (groups[i].Marks && groups[i].MarksAt != stamp)
                state.MarkDirty(groups[i]);
    }

    private void WriteMarks(DrawGroup group, PrefabKey piece, DrawInstance instance)
    {
        if (state.Facts is not { } facts)
            return;
        var placement = new Placement(piece, instance.At, instance.Turn);
        if (group.ShowsPorts || group.ShowsOpenEnds)
        {
            _ends.Clear();
            facts.Ends(placement, _ends);
            for (var i = 0; i < _ends.Count; i++)
            {
                var end = _ends[i];
                if (group.ShowsPorts)
                    group.NextPorts.Add(MarkShapes.Port(end), state.Pool);
                if (group.ShowsOpenEnds && end.Open)
                    group.NextOpenEnds.Add(MarkShapes.Open(end), state.Pool);
            }
        }

        if (group.ShowsFlow)
        {
            _flows.Clear();
            facts.Flow(placement, _flows);
            for (var i = 0; i < _flows.Count; i++)
            {
                group.NextFlow.Add(MarkShapes.Shaft(_flows[i]), state.Pool);
                group.NextFlow.Add(MarkShapes.Head(_flows[i]), state.Pool);
            }
        }

        if (group.ShowsWaits && instance.Thing.Finding is Finding.WaitsFor waits)
        {
            var from = MarkShapes.Centre(instance.At);
            foreach (var other in waits.Others)
                if (facts.CellOf(other, out var cell))
                {
                    var to = MarkShapes.Centre(cell);
                    group.NextWaits.Add(MarkShapes.Link(from, to), state.Pool);
                    group.NextWaits.Add(MarkShapes.LinkTip(from, to), state.Pool);
                }
        }
    }

    private void Copy()
    {
        var group = _group!;
        var budget = Batch.PageSize;
        while (budget > 0 && _member < group.Members.Count)
        {
            var draw = group.Members[_member];
            var batch = draw.Batch;
            if (!draw.Visible || batch.Count == 0)
            {
                draw.Copying = batch.Version;
                _member++;
                continue;
            }

            if (_index == 0)
            {
                draw.Copying = batch.Version;
                Grow(batch.Chunk);
            }

            var cost = group.Marks ? MarkedInstanceCost : 1;
            var until = Math.Min(batch.Count, _index + Math.Max(1, budget / cost));
            if (_written + (until - _index) > group.Staging.Length)
            {
                Begin();
                return;
            }

            for (; _index < until; _index++)
            {
                var instance = batch[_index];
                group.Staging[_written++] = Submission.Placement(instance.At, instance.Turn);
                if (group.Marks)
                    WriteMarks(group, batch.Piece, instance);
                budget -= cost;
            }

            if (_index >= batch.Count)
            {
                _member++;
                _index = 0;
            }
        }

        if (_member < group.Members.Count)
            return;
        for (var m = 0; m < group.Members.Count; m++)
            if (group.Members[m].Batch.Version != group.Members[m].Copying)
            {
                Begin();
                return;
            }

        if (group.NeedsPages && group.Piece.Parts.Length > 0 && _written > 0)
        {
            var pages = (_written + Batch.PageSize - 1) / Batch.PageSize;
            for (var p = 0; p < group.NextPages.Length; p++)
            {
                state.Pool.Return(group.NextPages[p]);
                for (var i = 0; i < pages; i++)
                    group.NextPages[p].Add(state.Pool.Rent());
            }

            _paging = true;
            _part = 0;
            _page = 0;
            return;
        }

        Complete(pages: false);
    }

    private void WritePage()
    {
        var group = _group!;
        var local = group.Piece.Parts[_part].Local;
        var page = group.NextPages[_part][_page];
        var from = _page * Batch.PageSize;
        var until = Math.Min(_written, from + Batch.PageSize);
        for (var i = from; i < until; i++)
            page[i - from] = group.Staging[i] * local;
        if (until < _written)
        {
            _page++;
            return;
        }

        _page = 0;
        if (++_part == group.NextPages.Length)
            Complete(pages: true);
    }

    private void Complete(bool pages)
    {
        var group = _group!;
        _group = null;
        for (var m = 0; m < group.Members.Count; m++)
            group.Members[m].Copied = group.Members[m].Copying;
        if (group.Members.Count == 0)
        {
            state.Retire(group);
            return;
        }

        if (_written > 0)
        {
            if (group.Buffer is null || group.Buffer.Capacity < _written)
            {
                group.Buffer?.Release();
                group.Buffer = state.Gpu.NewBuffer(Capacity(_written));
                group.StyledLook = null;
            }

            group.Buffer.Set(group.Staging, _written);
        }

        if (pages)
        {
            (group.Pages, group.NextPages) = (group.NextPages, group.Pages);
            for (var p = 0; p < group.NextPages.Length; p++)
                state.Pool.Return(group.NextPages[p]);
            group.HasPages = true;
        }
        else if (!group.NeedsPages && group.HasPages)
        {
            for (var p = 0; p < group.Pages.Length; p++)
                state.Pool.Return(group.Pages[p]);
            group.HasPages = false;
        }

        if (group.Marks)
        {
            (group.Ports, group.NextPorts) = (group.NextPorts, group.Ports);
            (group.OpenEnds, group.NextOpenEnds) = (group.NextOpenEnds, group.OpenEnds);
            (group.Flow, group.NextFlow) = (group.NextFlow, group.Flow);
            (group.Waits, group.NextWaits) = (group.NextWaits, group.Waits);
            group.NextPorts.Clear(state.Pool);
            group.NextOpenEnds.Clear(state.Pool);
            group.NextFlow.Clear(state.Pool);
            group.NextWaits.Clear(state.Pool);
            group.MarksAt = _marksStamp;
        }

        group.Count = _written;
        group.Bounds = _written > 0 ? new Bounds((_min + _max) * 0.5f, _max - _min) : default;
        group.Built = true;
    }

    private void Grow(ChunkKey chunk)
    {
        var min = new Vector3(chunk.X * ChunkKey.Metres - ChunkMargin, chunk.Y * ChunkKey.Metres - ChunkMargin, chunk.Z * ChunkKey.Metres - ChunkMargin);
        var size = ChunkKey.Metres + 2 * ChunkMargin;
        _min = new Vector3(Math.Min(_min.x, min.x), Math.Min(_min.y, min.y), Math.Min(_min.z, min.z));
        _max = new Vector3(Math.Max(_max.x, min.x + size), Math.Max(_max.y, min.y + size), Math.Max(_max.z, min.z + size));
    }

    private static int Capacity(int count)
    {
        var capacity = 64;
        while (capacity < count)
            capacity *= 2;
        return capacity;
    }
}

/// <summary>
/// Instanced submission over the draw groups <see cref="Rebuilds"/> sorts
/// Core's batches into, culled per 32 m chunk: per drawn group, per look layer, per part, per submesh, one call:
/// procedural from the group's
/// instance buffer for Lattice's own shaders (fill and edge in one material while neither is overridden), instanced from
/// matrix pages for an override or the stand-in. A group draws its last complete rebuild and nothing before its first;
/// a steady frame allocates nothing.
/// </summary>
internal sealed class Submission
{
    private const int Fill = 0;
    private const int Edge = 1;
    private const int Glow = 2;
    private const int SeeThrough = 3;

    private readonly IDrawPlan _plan;
    private readonly RenderPorts _ports;
    private readonly MaterialTable _materials;
    private readonly DrawState _state;
    private readonly Rebuilds _rebuilds;
    private int _skippedLayers;

    public Submission(IDrawPlan plan, RenderPorts ports, MaterialTable materials, DrawState state, Rebuilds rebuilds)
    {
        _plan = plan;
        _ports = ports;
        _materials = materials;
        _state = state;
        _rebuilds = rebuilds;
    }

    public SubmissionCounts Counts { get; private set; }

    /// <summary>Lattice's draws for this frame, <paramref name="now"/> in unscaled seconds.</summary>
    public void Submit(float now)
    {
        int groups = 0, calls = 0, instances = 0;
        var all = _state.Groups;
        for (var g = 0; g < all.Count; g++)
        {
            var group = all[g];
            if (!group.Built || group.Count == 0 || group.Look is not { } look || look.Hidden)
                continue;
            if (!ReferenceEquals(group.StyledLook, look) || group.MaterialsRevision != _materials.Revision)
                Restyle(group, look);
            if (group.Animated)
                Animate(group, now);
            groups++;
            instances += group.Count;
            calls += Issue(group);
        }

        Counts = new SubmissionCounts(_plan.BatchCount, _rebuilds.Visible, groups, calls, instances, _skippedLayers, _state.UnknownPieces);
    }

    private int Issue(DrawGroup group)
    {
        var calls = 0;
        var parts = group.Piece.Parts;
        for (var l = 0; l < DrawGroup.Layers; l++)
        {
            var layer = group.Layer[l];
            if (!layer.On || layer.Material is not { } material)
                continue;
            if (layer.Procedural)
            {
                for (var p = 0; p < parts.Length; p++)
                {
                    var part = parts[p];
                    layer.Block.Set(ShaderProp.Local, part.Local);
                    for (var sub = 0; sub < part.Mesh.Submeshes; sub++)
                    {
                        _ports.Gpu.DrawProcedural(part.Mesh, sub, material, group.Bounds, group.Count, layer.Block);
                        calls++;
                    }
                }

                continue;
            }

            if (!group.HasPages)
                continue;
            for (var p = 0; p < parts.Length; p++)
            {
                var part = parts[p];
                var pages = group.Pages[p];
                for (var page = 0; page < pages.Count; page++)
                {
                    var inPage = Math.Min(Batch.PageSize, group.Count - page * Batch.PageSize);
                    for (var sub = 0; sub < part.Mesh.Submeshes; sub++)
                    {
                        _ports.Gpu.Draw(part.Mesh, sub, material, pages[page], inPage, layer.Block);
                        calls++;
                    }
                }
            }
        }

        if (group.Marks)
        {
            calls += DrawMarks(group.PortLayer, group.Ports);
            calls += DrawMarks(group.OpenLayer, group.OpenEnds);
            calls += DrawMarks(group.FlowLayer, group.Flow);
            calls += DrawMarks(group.WaitsLayer, group.Waits);
        }

        return calls;
    }

    private int DrawMarks(LayerDraw layer, MarkPages marks)
    {
        if (!layer.On || layer.Material is not { } material || marks.Count == 0)
            return 0;
        var pages = marks.Pages;
        for (var page = 0; page < pages.Count; page++)
            _ports.Gpu.Draw(_ports.Gpu.Marker, 0, material, pages[page], Math.Min(Batch.PageSize, marks.Count - page * Batch.PageSize), layer.Block);
        return pages.Count;
    }

    /// <summary>A piece's matrix in the world: its cell's position (cells are decimetres) and its turn's axes.</summary>
    internal static Matrix4x4 Placement(Cell at, Turn turn)
    {
        var axes = turn.Axes;
        var m = Matrix4x4.identity;
        m.SetColumn(0, Column(axes.X));
        m.SetColumn(1, Column(axes.Y));
        m.SetColumn(2, Column(axes.Z));
        m.SetColumn(3, new Vector4(at.X * 0.1f, at.Y * 0.1f, at.Z * 0.1f, 1f));
        return m;
    }

    private static Vector4 Column(SignedAxis axis)
    {
        var sign = axis.Sign == AxisSign.Minus ? -1f : 1f;
        return axis.Axis switch
        {
            Axis.X => new Vector4(sign, 0f, 0f, 0f),
            Axis.Y => new Vector4(0f, sign, 0f, 0f),
            _ => new Vector4(0f, 0f, sign, 0f),
        };
    }

    /// <summary>
    /// The group's look into its layers: which material each draws with, how, and its static block values. A look that
    /// needs instanced pages the group does not have yet keeps the layers it has until the next rebuild writes them, so
    /// an override never leaves a frame without the group.
    /// </summary>
    private void Restyle(DrawGroup group, ResolvedLook look)
    {
        if (NeedsPages(group, look) && !group.HasPages)
        {
            group.NeedsPages = true;
            _state.MarkDirty(group);
            return;
        }

        group.StyledLook = look;
        group.MaterialsRevision = _materials.Revision;
        group.Animated = look.Animated;
        var style = look.Style;
        var beyond = Beyond(style);
        var needsPages = false;

        var fillKey = FillKey(group, look);
        var combined = look.Edge.A > 0 && fillKey.Value.Length > 0 ? _materials.Combined(fillKey, group.Existing) : null;

        var fill = group.Layer[Fill];
        if (combined is not null)
            SetOwn(group, fill, combined, Colour(look.Fill), beyond);
        else
            Set(group, fill, fillKey, Colour(look.Fill), beyond, ref needsPages);
        fill.Combined = combined is not null;
        if (fill.On)
        {
            var pattern = PatternCode(style.Pattern.Reduce(Pattern.None));
            fill.Block.Set(ShaderProp.Pattern, pattern);
            var patternColour = look.Edge.A > 0 ? Colour(look.Edge) : Colour(look.Fill);
            fill.Block.Set(ShaderProp.PatternColor, new Vector4(patternColour.x, patternColour.y, patternColour.z, Math.Min(1f, patternColour.w * 2f)));
        }

        var edge = group.Layer[Edge];
        Set(group, edge, look.Edge.A > 0 && combined is null ? MaterialKeys.Edge : default, Colour(look.Edge), beyond, ref needsPages);
        var line = style.Edge.TryGet(out var edgeLook) && edgeLook is EdgeLook.Line l ? new Vector4(LineCode(l.Style), l.Width.Value, 0f, 0f) : Vector4.zero;
        if (edge.On)
            edge.Block.Set(ShaderProp.Edge, line);
        if (fill.Combined)
        {
            fill.EdgeColour = Colour(look.Edge);
            fill.Block.Set(ShaderProp.EdgeColor, fill.EdgeColour);
            fill.Block.Set(ShaderProp.Edge, line);
        }

        Set(group, group.Layer[Glow], look.Glow.A > 0 ? MaterialKeys.Glow : default, Colour(look.Glow), beyond, ref needsPages);

        var through = style.SeeThrough.TryGet(out var seeThrough) && seeThrough is SeeThroughLook.Through t ? t : null;
        var see = group.Layer[SeeThrough];
        var seeColour = through is not null ? SeeThroughColour(look, through) : default;
        Set(group, see, seeColour.w > 0f ? MaterialKeys.SeeThrough : default, seeColour, beyond, ref needsPages);
        if (see.On && through is not null)
        {
            var strength = through.Strength.Value;
            see.Block.Set(ShaderProp.SeeThrough, new Vector4(strength, through.Range.Value, SeeThroughCurve.SquareOn(strength), 0f));
        }

        // Port marks and flow arrows in the look's edge colour (its fill without an edge); open ends and waits links in
        // its glow colour, else its fill (then edge), so they stand apart from the port marks on a look without a glow.
        // All draw with the solid material, never the additive glow, so a mark reads on a light surface too, and at
        // least nearly opaque, so it reads on a faint ghost.
        var markLine = MarkColour(look.Edge.A > 0 ? look.Edge : look.Fill);
        var markApart = MarkColour(look.Glow.A > 0 ? look.Glow : look.Fill.A > 0 ? look.Fill : look.Edge);
        SetMark(group, group.PortLayer, group.ShowsPorts ? MaterialKeys.Solid : default, markLine, beyond);
        SetMark(group, group.OpenLayer, group.ShowsOpenEnds ? MaterialKeys.Solid : default, markApart, beyond);
        SetMark(group, group.FlowLayer, group.ShowsFlow ? MaterialKeys.Solid : default, markLine, beyond);
        SetMark(group, group.WaitsLayer, group.ShowsWaits ? MaterialKeys.Solid : default, markApart, beyond);

        var effect = EffectCode(style);
        for (var i = 0; i < DrawGroup.Layers; i++)
            if (group.Layer[i].On)
                group.Layer[i].Block.Set(ShaderProp.Effect, effect);

        group.NeedsPages = needsPages;
    }

    /// <summary>Whether any of the look's layers would draw instanced (an override or the stand-in).</summary>
    private bool NeedsPages(DrawGroup group, ResolvedLook look)
    {
        var style = look.Style;
        var fillKey = FillKey(group, look);
        var combined = look.Edge.A > 0 && fillKey.Value.Length > 0 && _materials.Combined(fillKey, group.Existing) is not null;
        return Instanced(group, fillKey) && !combined
            || look.Edge.A > 0 && !combined && Instanced(group, MaterialKeys.Edge)
            || look.Glow.A > 0 && Instanced(group, MaterialKeys.Glow)
            || style.SeeThrough.TryGet(out var through) && through is SeeThroughLook.Through t && SeeThroughColour(look, t).w > 0f
                && Instanced(group, MaterialKeys.SeeThrough);
    }

    /// <summary>The hidden part's colour: the fill paint at the look's own see-through opacity (times its dim), else the fill as it is.</summary>
    private static Vector4 SeeThroughColour(ResolvedLook look, SeeThroughLook.Through through)
    {
        var fill = Colour(look.Fill);
        return through.Opacity.TryGet(out var opacity)
            ? new Vector4(fill.x, fill.y, fill.z, opacity.Value * look.Style.Dim.Reduce(Fraction.One).Value)
            : fill;
    }

    /// <summary>The fill's material key; none for a look without fill or a mesh a claim draws.</summary>
    private static MaterialKey FillKey(DrawGroup group, ResolvedLook look) =>
        look.Fill.A > 0 && !group.MeshClaimed ? look.Style.Material.Reduce(MaterialKeys.Ghost) : default;

    private bool Instanced(DrawGroup group, MaterialKey key)
    {
        if (key.Value.Length == 0)
            return false;
        var entry = _materials.Entry(key);
        var procedural = group.Existing ? entry.ProceduralOverlay : entry.ProceduralPlacement;
        return procedural is null && (group.Existing ? entry.Overlay : entry.Placement) is not null;
    }

    private void Set(DrawGroup group, LayerDraw layer, MaterialKey key, Vector4 colour, Vector4 beyond, ref bool needsPages)
    {
        layer.On = false;
        layer.Material = null;
        layer.Combined = false;
        if (key.Value.Length == 0)
            return;
        var entry = _materials.Entry(key);
        var procedural = group.Existing ? entry.ProceduralOverlay : entry.ProceduralPlacement;
        if (procedural is not null)
        {
            SetOwn(group, layer, procedural, colour, beyond);
            return;
        }

        var material = group.Existing ? entry.Overlay : entry.Placement;
        if (material is null)
        {
            _skippedLayers++;
            return;
        }

        needsPages = true;
        layer.Material = material;
        layer.Procedural = false;
        layer.Colour = colour;
        layer.On = true;
        layer.Block.Set(ShaderProp.Color, colour);
        layer.Block.Set(ShaderProp.Beyond, beyond);
    }

    /// <summary>A mark layer: drawn instanced with the key's material (an override's, if any), none when the key has none.</summary>
    private void SetMark(DrawGroup group, LayerDraw layer, MaterialKey key, Vector4 colour, Vector4 beyond)
    {
        layer.On = false;
        layer.Material = null;
        if (key.Value.Length == 0)
            return;
        var entry = _materials.Entry(key);
        var material = group.Existing ? entry.Overlay : entry.Placement;
        if (material is null)
        {
            _skippedLayers++;
            return;
        }

        layer.Material = material;
        layer.Procedural = false;
        layer.Colour = colour;
        layer.On = true;
        layer.Block.Set(ShaderProp.Color, colour);
        layer.Block.Set(ShaderProp.Beyond, beyond);
    }

    private static Vector4 MarkColour(Rgba c) => new(c.R / 255f, c.G / 255f, c.B / 255f, Math.Max(c.A / 255f, 0.85f));

    private static void SetOwn(DrawGroup group, LayerDraw layer, IGpuMaterial material, Vector4 colour, Vector4 beyond)
    {
        layer.Material = material;
        layer.Procedural = true;
        layer.Colour = colour;
        layer.On = group.Buffer is not null;
        layer.Block.Set(ShaderProp.Color, colour);
        layer.Block.Set(ShaderProp.Beyond, beyond);
        if (group.Buffer is { } buffer)
            layer.Block.Set(ShaderProp.Instances, buffer);
    }

    /// <summary>This frame's colour for an animated group's layers (the contract: <c>_Color</c> carries effects).</summary>
    private static void Animate(DrawGroup group, float now)
    {
        var style = group.StyledLook!.Style;
        if (group.Since < 0f)
            group.Since = now;
        var t = now - group.Since;
        var factor = Effects.Factor(style, t);
        for (var l = 0; l < DrawGroup.Layers; l++)
        {
            var layer = group.Layer[l];
            if (!layer.On)
                continue;
            var c = layer.Colour;
            layer.Block.Set(ShaderProp.Color, new Vector4(c.x, c.y, c.z, c.w * factor));
            if (layer.Combined)
            {
                var e = layer.EdgeColour;
                layer.Block.Set(ShaderProp.EdgeColor, new Vector4(e.x, e.y, e.z, e.w * factor));
            }

            layer.Block.Set(ShaderProp.Time, t);
        }
    }

    private static Vector4 Colour(Rgba c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    private static Vector4 Beyond(Style style) =>
        style.BeyondClip.TryGet(out var beyond)
            ? beyond switch
            {
                BeyondClipLook.Hidden => new Vector4(1f, 0f, 0f, 0f),
                BeyondClipLook.Dim dim => new Vector4(2f, dim.By.Value, 0f, 0f),
                _ => new Vector4(0f, 1f, 0f, 0f),
            }
            : new Vector4(0f, 1f, 0f, 0f);

    private static float PatternCode(Pattern pattern) => pattern switch
    {
        Pattern.Hatch => 1f,
        Pattern.CrossHatch => 2f,
        Pattern.Dots => 3f,
        _ => 0f,
    };

    private static float LineCode(LineStyle style) => style switch
    {
        LineStyle.Dashed => 1f,
        LineStyle.Dotted => 2f,
        _ => 0f,
    };

    private static Vector4 EffectCode(Style style) =>
        style.Effect.TryGet(out var effect)
            ? effect switch
            {
                EffectLook.Pulse pulse => new Vector4(1f, pulse.Period.Seconds, pulse.Low.Value, 0f),
                EffectLook.Blink blink => new Vector4(2f, blink.Period.Seconds, blink.Duty.Value, 0f),
                _ => Vector4.zero,
            }
            : Vector4.zero;
}

/// <summary>
/// Effects on the CPU, as the factor on a layer's opacity some seconds after its look started: an
/// entry flash first, then the repeating effect. Motion was already applied by Core (reduced or off motion resolves
/// effects to steady).
/// </summary>
internal static class Effects
{
    public static float Factor(Style style, float t)
    {
        if (style.Enter.TryGet(out var enter) && enter is EnterLook.Flash flash)
        {
            var period = Math.Max(flash.Period.Seconds, 1e-3f);
            if (t < flash.Count.Count * period)
                return (t % period) < period * 0.5f ? 1f : 0.15f;
        }

        if (!style.Effect.TryGet(out var effect))
            return 1f;
        switch (effect)
        {
            case EffectLook.Pulse pulse:
            {
                var period = Math.Max(pulse.Period.Seconds, 1e-3f);
                var phase = (t % period) / period;
                var wave = 0.5f + 0.5f * (float)Math.Cos(phase * 2 * Math.PI);
                return pulse.Low.Value + (1f - pulse.Low.Value) * wave;
            }

            case EffectLook.Blink blink:
            {
                var period = Math.Max(blink.Period.Seconds, 1e-3f);
                return (t % period) < blink.Duty.Value * period ? 1f : 0f;
            }

            default:
                return 1f;
        }
    }
}
