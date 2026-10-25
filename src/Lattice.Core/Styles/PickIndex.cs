using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Ports;

namespace Lattice.Core.Styles;

/// <summary>One pick box: a footprint cell of a drawn thing, clipped to the piece's bounds, in world metres.</summary>
internal readonly record struct PickBox(WorldPoint Min, WorldPoint Max, Cell Cell)
{
    public float Volume => (Max.X - Min.X) * (Max.Y - Min.Y) * (Max.Z - Min.Z);

    public WorldPoint Centre => new((Min.X + Max.X) * 0.5f, (Min.Y + Max.Y) * 0.5f, (Min.Z + Max.Z) * 0.5f);
}

/// <summary>A box in the index: the thing and which of its boxes.</summary>
internal readonly record struct BoxRef(Thing Thing, int Box);

/// <summary>
/// Pick boxes by 2 m walk cell (the large grid's cells: large cells centre on odd metres, small cells lie inside one).
/// A box is entered in every walk cell it overlaps,
/// so a multi-cell piece is found from each of its cells. Boxes are worked out per change; a pick walks the cells along
/// the ray and stops at the first hit nearer than any box still ahead.
/// </summary>
internal sealed class PickIndex(Shapes shapes, IPieceLooks looks)
{
    /// <summary>The walk cell edge, in metres.</summary>
    public const float WalkMetres = 2f;

    /// <summary>A large cell's edge and a small cell's (<c>Grid3</c>).</summary>
    private const float LargeMetres = 2f;

    private const float SmallMetres = 0.5f;

    /// <summary>How deep a face slot's box is without the piece's bounds: one small step, Lattice's choice.</summary>
    private const float FaceDepth = 0.25f;

    private readonly Dictionary<(int, int, int), List<BoxRef>> _cells = new();

    /// <summary>How many boxes the index holds.</summary>
    public int Count { get; private set; }

    /// <summary>Works out a thing's boxes and enters them. Linear in its footprint.</summary>
    public void Add(Thing thing)
    {
        if (!shapes.Of(new Placement(thing.Piece, thing.At, thing.Turn)).TryGet(out var shape))
            return;
        var bounds = looks.Bounds(thing.Piece).TryGet(out var local) ? Turned(local, thing.At, thing.Turn) : (PickBox?)null;
        var boxes = new PickBox[shape.Spots.Count];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = Clipped(Of(shape.Spots[i], shape.IsSmall), bounds);
        thing.Boxes = boxes;
        for (var i = 0; i < boxes.Length; i++)
            Enter(boxes[i], new BoxRef(thing, i));
        Count += boxes.Length;
    }

    public void Remove(Thing thing)
    {
        if (thing.Boxes is not { } boxes)
            return;
        for (var i = 0; i < boxes.Length; i++)
            Leave(boxes[i], new BoxRef(thing, i));
        Count -= boxes.Length;
        thing.Boxes = null;
    }

    public void Clear()
    {
        _cells.Clear();
        Count = 0;
    }

    /// <summary>
    /// Walks the ray's 2 m cells in order (Amanatides and Woo's grid traversal) and asks the filter about
    /// each box it enters; the nearest accepted entry wins, then the smaller box, then the filter's tie order.
    /// Stops once the best hit is no farther than the current cell's exit.
    /// </summary>
    public bool Walk(PickRay ray, IPickFilter filter, out PickFound found)
    {
        found = default;
        var length = MathF.Sqrt(ray.Direction.X * ray.Direction.X + ray.Direction.Y * ray.Direction.Y + ray.Direction.Z * ray.Direction.Z);
        var d = new WorldVector(ray.Direction.X / length, ray.Direction.Y / length, ray.Direction.Z / length);
        var o = ray.Origin;
        var cx = Cell(o.X);
        var cy = Cell(o.Y);
        var cz = Cell(o.Z);
        var (stepX, nextX, deltaX) = Axis(o.X, d.X, cx);
        var (stepY, nextY, deltaY) = Axis(o.Y, d.Y, cy);
        var (stepZ, nextZ, deltaZ) = Axis(o.Z, d.Z, cz);
        var have = false;
        while (true)
        {
            var exit = MathF.Min(nextX, MathF.Min(nextY, nextZ));
            if (_cells.TryGetValue((cx, cy, cz), out var refs))
                for (var i = 0; i < refs.Count; i++)
                {
                    var box = refs[i].Thing.Boxes![refs[i].Box];
                    if (!Slab(box, o, d, out var t, out var face) || t > ray.MaxMetres)
                        continue;
                    if (have && !Better(t, box.Volume, refs[i].Thing, found, filter))
                        continue;
                    if (!filter.Accept(refs[i].Thing, box))
                        continue;
                    found = new PickFound(refs[i].Thing, box, face, t, new WorldPoint(o.X + d.X * t, o.Y + d.Y * t, o.Z + d.Z * t));
                    have = true;
                }

            if ((have && found.Distance <= exit) || exit > ray.MaxMetres)
                return have;
            if (nextX <= nextY && nextX <= nextZ)
            {
                cx += stepX;
                nextX += deltaX;
            }
            else if (nextY <= nextZ)
            {
                cy += stepY;
                nextY += deltaY;
            }
            else
            {
                cz += stepZ;
                nextZ += deltaZ;
            }
        }
    }

    private const float Tie = 1e-4f;

    private static bool Better(float t, float volume, Thing thing, PickFound best, IPickFilter filter)
    {
        if (t < best.Distance - Tie)
            return true;
        if (t > best.Distance + Tie)
            return false;
        var bestVolume = best.Box.Volume;
        if (volume < bestVolume - Tie)
            return true;
        if (volume > bestVolume + Tie)
            return false;
        return filter.Before(thing, best.Thing);
    }

    private static int Cell(float metres) => (int)MathF.Floor(metres / WalkMetres);

    private static (int Step, float Next, float Delta) Axis(float origin, float direction, int cell)
    {
        if (direction > 0f)
            return (1, ((cell + 1) * WalkMetres - origin) / direction, WalkMetres / direction);
        if (direction < 0f)
            return (-1, (cell * WalkMetres - origin) / direction, -WalkMetres / direction);
        return (0, float.PositiveInfinity, float.PositiveInfinity);
    }

    /// <summary>The ray's entry into the box (0 when it starts inside) and the face it entered by.</summary>
    private static bool Slab(PickBox box, WorldPoint o, WorldVector d, out float entry, out Face face)
    {
        entry = 0f;
        face = Face.PlusY;
        var exit = float.PositiveInfinity;
        if (!Through(o.X, d.X, box.Min.X, box.Max.X, Face.MinusX, Face.PlusX, ref entry, ref exit, ref face)
            || !Through(o.Y, d.Y, box.Min.Y, box.Max.Y, Face.MinusY, Face.PlusY, ref entry, ref exit, ref face)
            || !Through(o.Z, d.Z, box.Min.Z, box.Max.Z, Face.MinusZ, Face.PlusZ, ref entry, ref exit, ref face))
            return false;
        return entry <= exit;
    }

    private static bool Through(float o, float d, float min, float max, Face low, Face high, ref float entry, ref float exit, ref Face face)
    {
        if (d == 0f)
            return o >= min && o <= max;
        var near = ((d > 0f ? min : max) - o) / d;
        var far = ((d > 0f ? max : min) - o) / d;
        if (near > entry)
        {
            entry = near;
            face = d > 0f ? low : high;
        }

        exit = MathF.Min(exit, far);
        return exit >= 0f;
    }

    private static PickBox Of(SpotKey spot, bool small)
    {
        var c = spot.Cell;
        var x = c.X * 0.1f;
        var y = c.Y * 0.1f;
        var z = c.Z * 0.1f;
        var half = (small ? SmallMetres : LargeMetres) * 0.5f;
        var min = new WorldPoint(x - half, y - half, z - half);
        var max = new WorldPoint(x + half, y + half, z + half);
        return spot.Slot switch
        {
            Slot.FaceXPlus => new PickBox(min with { X = max.X - FaceDepth }, max, c),
            Slot.FaceXMinus => new PickBox(min, max with { X = min.X + FaceDepth }, c),
            Slot.FaceYPlus => new PickBox(min with { Y = max.Y - FaceDepth }, max, c),
            Slot.FaceYMinus => new PickBox(min, max with { Y = min.Y + FaceDepth }, c),
            Slot.FaceZPlus => new PickBox(min with { Z = max.Z - FaceDepth }, max, c),
            Slot.FaceZMinus => new PickBox(min, max with { Z = min.Z + FaceDepth }, c),
            _ => new PickBox(min, max, c),
        };
    }

    /// <summary>The piece's bounds turned and placed at its cell, as a world box.</summary>
    private static PickBox Turned(LocalBox local, Cell at, Turn turn)
    {
        var axes = turn.Axes;
        var a = Apply(axes, local.Min);
        var b = Apply(axes, local.Max);
        var x = at.X * 0.1f;
        var y = at.Y * 0.1f;
        var z = at.Z * 0.1f;
        return new PickBox(
            new WorldPoint(x + MathF.Min(a.X, b.X), y + MathF.Min(a.Y, b.Y), z + MathF.Min(a.Z, b.Z)),
            new WorldPoint(x + MathF.Max(a.X, b.X), y + MathF.Max(a.Y, b.Y), z + MathF.Max(a.Z, b.Z)),
            at);
    }

    private static WorldVector Apply(TurnAxes axes, WorldVector v)
    {
        var (x1, y1, z1) = Image(axes.X, v.X);
        var (x2, y2, z2) = Image(axes.Y, v.Y);
        var (x3, y3, z3) = Image(axes.Z, v.Z);
        return new WorldVector(x1 + x2 + x3, y1 + y2 + y3, z1 + z2 + z3);
    }

    private static (float X, float Y, float Z) Image(SignedAxis axis, float length)
    {
        var signed = axis.Sign == AxisSign.Minus ? -length : length;
        return axis.Axis switch
        {
            Api.Axis.X => (signed, 0f, 0f),
            Api.Axis.Y => (0f, signed, 0f),
            _ => (0f, 0f, signed),
        };
    }

    /// <summary>The cell box cut down to the bounds; the whole cell box when they do not meet.</summary>
    private static PickBox Clipped(PickBox cell, PickBox? bounds)
    {
        if (bounds is not { } b)
            return cell;
        var min = new WorldPoint(MathF.Max(cell.Min.X, b.Min.X), MathF.Max(cell.Min.Y, b.Min.Y), MathF.Max(cell.Min.Z, b.Min.Z));
        var max = new WorldPoint(MathF.Min(cell.Max.X, b.Max.X), MathF.Min(cell.Max.Y, b.Max.Y), MathF.Min(cell.Max.Z, b.Max.Z));
        return min.X < max.X && min.Y < max.Y && min.Z < max.Z ? new PickBox(min, max, cell.Cell) : cell;
    }

    private void Enter(PickBox box, BoxRef entry)
    {
        for (var x = Cell(box.Min.X); x <= Cell(Inside(box.Max.X)); x++)
            for (var y = Cell(box.Min.Y); y <= Cell(Inside(box.Max.Y)); y++)
                for (var z = Cell(box.Min.Z); z <= Cell(Inside(box.Max.Z)); z++)
                {
                    if (!_cells.TryGetValue((x, y, z), out var list))
                        _cells[(x, y, z)] = list = new List<BoxRef>(4);
                    list.Add(entry);
                }
    }

    private void Leave(PickBox box, BoxRef entry)
    {
        for (var x = Cell(box.Min.X); x <= Cell(Inside(box.Max.X)); x++)
            for (var y = Cell(box.Min.Y); y <= Cell(Inside(box.Max.Y)); y++)
                for (var z = Cell(box.Min.Z); z <= Cell(Inside(box.Max.Z)); z++)
                    if (_cells.TryGetValue((x, y, z), out var list))
                    {
                        list.Remove(entry);
                        if (list.Count == 0)
                            _cells.Remove((x, y, z));
                    }
    }

    /// <summary>A max edge on a walk cell's boundary belongs to the cell below it.</summary>
    private static float Inside(float max) => max - 1e-4f;
}

/// <summary>What a pick asks of each box it reaches: whether it counts, and the tie order past distance and size.</summary>
internal interface IPickFilter
{
    bool Accept(Thing thing, PickBox box);

    /// <summary>Whether <paramref name="thing"/> wins a tie against <paramref name="other"/>: a ghost over a draft, then the lower id or index.</summary>
    bool Before(Thing thing, Thing other);
}

/// <summary>The best box a pick found.</summary>
internal readonly record struct PickFound(Thing Thing, PickBox Box, Face Face, float Distance, WorldPoint Point);
