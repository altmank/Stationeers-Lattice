using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;

namespace Lattice.Core.Styles;

/// <summary>
/// The viewer's clips as a stack: the last installed applies, disposing one brings back the one under it. Moving costs
/// nothing but the new shape: drawing applies the clip per draw call and nothing re-signatures. A change is raised at
/// most once a frame, last in the styles' frame, from the subscribers' own array, as a plain call.
/// </summary>
internal sealed class Clips
{
    private readonly List<ClipHandle> _stack = new(4);
    private readonly Subscribers<Action<Option<ClipShape>>> _changed = new();
    private bool _dirty;

    public Option<ClipShape> InForce => _stack.Count == 0 ? Option<ClipShape>.None : Option.Some(_stack[_stack.Count - 1].Shape);

    public event Action<Option<ClipShape>> Changed { add => _changed.Add(value); remove => _changed.Remove(value); }

    public IClip Install(ClipShape shape)
    {
        var clip = new ClipHandle(this, shape);
        _stack.Add(clip);
        _dirty = true;
        return clip;
    }

    /// <summary>Raises the change once if the clip in force changed since the last raise. Allocates nothing.</summary>
    public void Raise()
    {
        if (!_dirty)
            return;
        _dirty = false;
        var now = InForce;
        foreach (var handler in _changed.Current)
            handler(now);
    }

    public void End()
    {
        _stack.Clear();
        _dirty = false;
    }

    /// <summary>Whether a point lies beyond the clip in force.</summary>
    public bool Beyond(WorldPoint point) => _stack.Count > 0 && Beyond(_stack[_stack.Count - 1].Shape, point);

    public static bool Beyond(ClipShape shape, WorldPoint p) => shape switch
    {
        ClipShape.Plane plane => (p.X - plane.Point.X) * plane.Normal.X + (p.Y - plane.Point.Y) * plane.Normal.Y + (p.Z - plane.Point.Z) * plane.Normal.Z > 0f,
        ClipShape.Box box => p.X < box.Min.X || p.Y < box.Min.Y || p.Z < box.Min.Z || p.X > box.Max.X || p.Y > box.Max.Y || p.Z > box.Max.Z,
        _ => false,
    };

    private void Moved(ClipHandle clip)
    {
        if (_stack.Count > 0 && ReferenceEquals(_stack[_stack.Count - 1], clip))
            _dirty = true;
    }

    private void Removed(ClipHandle clip)
    {
        var at = _stack.LastIndexOf(clip);
        if (at < 0)
            return;
        if (at == _stack.Count - 1)
            _dirty = true;
        _stack.RemoveAt(at);
    }

    private sealed class ClipHandle(Clips clips, ClipShape shape) : IClip
    {
        public ClipShape Shape { get; private set; } = shape;

        public void Move(ClipShape to)
        {
            Shape = to;
            clips.Moved(this);
        }

        public void Dispose() => clips.Removed(this);
    }
}
