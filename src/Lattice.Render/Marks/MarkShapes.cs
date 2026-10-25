using Lattice.Api;
using UnityEngine;

namespace Lattice.Render.Marks;

/// <summary>
/// Where marks sit, as matrices for the unit-cube marker mesh. A port mark is a short bar from inside its small cell out
/// to the cell's face, pointing the way the port leaves; an open-end mark is a block on that face, where a joining piece
/// would meet it. A flow arrow is a bar across the port's face with a block at the end the flow goes toward. A waits
/// link is a thin bar from a ghost to one it waits for, with a block near the one waited for. Cells are decimetres; a
/// small cell is 0.5 m.
/// </summary>
internal static class MarkShapes
{
    /// <summary>Half a small cell: from a port's cell centre to the face it leaves through.</summary>
    public const float ToFace = 0.25f;

    public const float PortLength = 0.2f;
    public const float PortWidth = 0.06f;
    public const float OpenSize = 0.16f;

    public static Matrix4x4 Port(EndMark end) => Box(end, ToFace - PortLength * 0.5f, PortLength, PortWidth);

    public static Matrix4x4 Open(EndMark end) => Box(end, ToFace, OpenSize, OpenSize);

    public const float ShaftLength = 0.3f;
    public const float ShaftWidth = 0.04f;
    public const float HeadSize = 0.11f;
    public const float LinkWidth = 0.03f;
    public const float LinkHead = 0.14f;

    /// <summary>A flow arrow's shaft: a bar across the port's face, along the flow.</summary>
    public static Matrix4x4 Shaft(FlowMark flow) => Box(new EndMark(flow.At, flow.Toward, false), ToFace, ShaftLength, ShaftWidth);

    /// <summary>A flow arrow's head: a block at the shaft's end the flow goes toward.</summary>
    public static Matrix4x4 Head(FlowMark flow) =>
        Box(new EndMark(flow.At, flow.Toward, false), ToFace + (flow.Inward ? -1f : 1f) * ShaftLength * 0.5f, HeadSize, HeadSize);

    /// <summary>A cell's centre in metres (cells are decimetres).</summary>
    public static Vector3 Centre(Cell cell) => new(cell.X * 0.1f, cell.Y * 0.1f, cell.Z * 0.1f);

    /// <summary>A thin bar from one point to another, any direction: a waits link's line.</summary>
    public static Matrix4x4 Link(Vector3 from, Vector3 to) => Bar(from, to, LinkWidth);

    /// <summary>A block on a waits link near the ghost waited for, so the link reads which way it waits.</summary>
    public static Matrix4x4 LinkTip(Vector3 from, Vector3 to)
    {
        var d = to - from;
        var length = d.magnitude;
        var at = length > 0.6f ? to - d * (0.4f / length) : (from + to) * 0.5f;
        var m = Matrix4x4.identity;
        m.m00 = LinkHead;
        m.m11 = LinkHead;
        m.m22 = LinkHead;
        m.m03 = at.x;
        m.m13 = at.y;
        m.m23 = at.z;
        return m;
    }

    // A box `width` across whose length runs from `from` to `to`: its z column is the whole span, x and y are two
    // directions square to it.
    private static Matrix4x4 Bar(Vector3 from, Vector3 to, float width)
    {
        var d = to - from;
        var length = d.magnitude;
        var u = length > 1e-5f ? d / length : new Vector3(0f, 0f, 1f);
        var up = Mathf.Abs(u.y) < 0.9f ? new Vector3(0f, 1f, 0f) : new Vector3(1f, 0f, 0f);
        var v = Vector3.Cross(up, u).normalized;
        var w = Vector3.Cross(u, v);
        var mid = (from + to) * 0.5f;
        var m = Matrix4x4.identity;
        m.SetColumn(0, new Vector4(v.x * width, v.y * width, v.z * width, 0f));
        m.SetColumn(1, new Vector4(w.x * width, w.y * width, w.z * width, 0f));
        m.SetColumn(2, new Vector4(d.x, d.y, d.z, 0f));
        m.SetColumn(3, new Vector4(mid.x, mid.y, mid.z, 1f));
        return m;
    }

    /// <summary>The direction a face points, as a unit vector.</summary>
    public static Vector3 Unit(Face face) => face switch
    {
        Face.PlusX => new Vector3(1f, 0f, 0f),
        Face.MinusX => new Vector3(-1f, 0f, 0f),
        Face.PlusY => new Vector3(0f, 1f, 0f),
        Face.MinusY => new Vector3(0f, -1f, 0f),
        Face.PlusZ => new Vector3(0f, 0f, 1f),
        _ => new Vector3(0f, 0f, -1f),
    };

    // An axis-aligned box: along the port's direction it is `along` long, across it `across` wide, centred `out`
    // metres from the cell centre toward the port's face.
    private static Matrix4x4 Box(EndMark end, float out_, float along, float across)
    {
        var d = Unit(end.Toward);
        var m = Matrix4x4.identity;
        m.m00 = d.x != 0f ? along : across;
        m.m11 = d.y != 0f ? along : across;
        m.m22 = d.z != 0f ? along : across;
        m.m03 = end.From.X * 0.1f + d.x * out_;
        m.m13 = end.From.Y * 0.1f + d.y * out_;
        m.m23 = end.From.Z * 0.1f + d.z * out_;
        return m;
    }
}
