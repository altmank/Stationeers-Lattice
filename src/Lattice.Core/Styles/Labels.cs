using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Styles;

/// <summary>A label's rectangle on screen, in pixels.</summary>
internal readonly record struct ScreenRect(float X, float Y, float Width, float Height)
{
    public bool Overlaps(ScreenRect other) =>
        X < other.X + other.Width && other.X < X + Width && Y < other.Y + Height && other.Y < Y + Height;
}

/// <summary>
/// The renderer's half of label choice: where a candidate's label would sit on screen. Asked only of the few candidates
/// Core picked by distance and priority. False when it is off screen.
/// </summary>
internal interface ILabelProjector
{
    bool Project(Thing thing, LabelLook.Shown label, out ScreenRect rect);
}

/// <summary>A label shown or fading out.</summary>
/// <param name="Thing">The thing it labels.</param>
/// <param name="Label">What it says and how.</param>
/// <param name="Since">When it began to appear, or to vanish when <paramref name="Fading"/>.</param>
/// <param name="Fading">Whether it is fading out.</param>
/// <param name="Ease">Its appear or vanish time.</param>
internal readonly record struct ShownLabel(Thing Thing, LabelLook.Shown Label, TimeSpan Since, bool Fading, Duration Ease);

/// <summary>
/// Label choice, 10 times a second: the nearest labels in range by priority, overlaps culled, at most 48 shown. A shown
/// label stays until it is 20 % past its range or 8 places past the cap; labels that leave fade out (at most 16 at a
/// time). A look with an icon and no label is offered as <see cref="IconOnly"/>, so its glyph shows through the same
/// choice. Preallocated: a choice allocates nothing.
/// </summary>
internal sealed class LabelChooser
{
    public const int Cap = 48;

    public const int Slack = 8;

    public const int MostFading = 16;

    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(100);

    private const float Stay = 1.2f;

    /// <summary>The label of a look with an icon and no label: no words, the library's text, above, priority 0, 30 m.</summary>
    public static readonly LabelLook.Shown IconOnly = new(
        new LabelText.Literal(default), LibraryText.Default, LabelAnchor.Above, 0, Known.Value(Metres.Create(30f)));

    private readonly Candidate[] _candidates = new Candidate[Cap + Slack];
    private readonly ScreenRect[] _rects = new ScreenRect[Cap + Slack];
    private readonly ShownLabel[] _shown = new ShownLabel[Cap];
    private readonly ShownLabel[] _next = new ShownLabel[Cap];
    private readonly ShownLabel[] _fading = new ShownLabel[MostFading];
    private int _candidateCount;
    private int _shownCount;
    private int _fadingCount;
    private TimeSpan? _last;

    public int Count => _shownCount + _fadingCount;

    public ShownLabel At(int index) => index < _shownCount ? _shown[index] : _fading[index - _shownCount];

    public bool Due(TimeSpan now) => _last is not { } last || now - last >= Period;

    /// <summary>Chooses the labels for a camera position from the batches' looks and instances.</summary>
    public void Choose(TimeSpan now, WorldPoint camera, IReadOnlyList<Batch> batches, ILabelProjector? projector)
    {
        _last = now;
        Gather(camera, batches);
        var nextCount = 0;
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < _candidateCount && nextCount < Cap; i++)
            {
                var candidate = _candidates[i];
                var shownAt = IndexOfShown(candidate.Thing);
                if ((pass == 0) != (shownAt >= 0))
                    continue;
                if (shownAt < 0 && (candidate.Distance > candidate.Label.Range.Value || i >= Cap))
                    continue;
                if (projector is not null)
                {
                    if (!projector.Project(candidate.Thing, candidate.Label, out var rect) || Overlaps(rect, nextCount))
                        continue;
                    _rects[nextCount] = rect;
                }

                _next[nextCount++] = shownAt >= 0
                    ? _shown[shownAt] with { Label = candidate.Label }
                    : new ShownLabel(candidate.Thing, candidate.Label, now, false, candidate.Appear);
            }

        for (var i = 0; i < _shownCount; i++)
            if (!Holds(_shown[i].Thing, nextCount))
                Fade(_shown[i], now);
        Array.Copy(_next, _shown, nextCount);
        Array.Clear(_shown, nextCount, Cap - nextCount);
        _shownCount = nextCount;
        Expire(now);
    }

    public void Clear()
    {
        Array.Clear(_shown, 0, _shown.Length);
        Array.Clear(_fading, 0, _fading.Length);
        Array.Clear(_candidates, 0, _candidates.Length);
        _last = null;
        _shownCount = 0;
        _fadingCount = 0;
        _candidateCount = 0;
    }

    /// <summary>Keeps the best cap-plus-slack candidates in range (shown ones up to 20 % past theirs), by priority then distance.</summary>
    private void Gather(WorldPoint camera, IReadOnlyList<Batch> batches)
    {
        _candidateCount = 0;
        for (var b = 0; b < batches.Count; b++)
        {
            var batch = batches[b];
            if (batch.Count == 0)
                continue;
            var look = batch.Look;
            if (look.Hidden || !TryLabel(look, out var label))
                continue;
            var reach = label.Range.Value * Stay;
            if (ChunkDistance(camera, batch.Chunk) > reach)
                continue;
            var appear = look.Style.Appear.Reduce(Duration.Zero);
            for (var i = 0; i < batch.Count; i++)
            {
                var thing = batch[i].Thing;
                var distance = Distance(camera, thing.At);
                if (distance > reach || (distance > label.Range.Value && IndexOfShown(thing) < 0))
                    continue;
                Offer(new Candidate(thing, label, distance, appear));
            }
        }
    }

    private static bool TryLabel(ResolvedLook look, out LabelLook.Shown label)
    {
        if (look.Label.TryGet(out var own))
        {
            label = own;
            return true;
        }

        label = IconOnly;
        return look.Style.Icon.TryGet(out var icon) && icon is IconLook.Shown;
    }

    private void Offer(Candidate candidate)
    {
        var at = _candidateCount;
        while (at > 0 && candidate.Beats(_candidates[at - 1]))
            at--;
        if (at >= _candidates.Length)
            return;
        var last = Math.Min(_candidateCount, _candidates.Length - 1);
        for (var i = last; i > at; i--)
            _candidates[i] = _candidates[i - 1];
        _candidates[at] = candidate;
        if (_candidateCount < _candidates.Length)
            _candidateCount++;
    }

    private int IndexOfShown(Thing thing)
    {
        for (var i = 0; i < _shownCount; i++)
            if (ReferenceEquals(_shown[i].Thing, thing))
                return i;
        return -1;
    }

    private bool Holds(Thing thing, int count)
    {
        for (var i = 0; i < count; i++)
            if (ReferenceEquals(_next[i].Thing, thing))
                return true;
        return false;
    }

    private bool Overlaps(ScreenRect rect, int count)
    {
        for (var i = 0; i < count; i++)
            if (_rects[i].Overlaps(rect))
                return true;
        return false;
    }

    private void Fade(ShownLabel label, TimeSpan now)
    {
        var vanish = label.Thing.Signature.Look?.Style.Vanish.Reduce(Duration.Zero) ?? Duration.Zero;
        if (vanish.Seconds <= 0f)
            return;
        if (_fadingCount == MostFading)
        {
            Array.Copy(_fading, 1, _fading, 0, MostFading - 1);
            _fadingCount--;
        }

        _fading[_fadingCount++] = new ShownLabel(label.Thing, label.Label, now, true, vanish);
    }

    private void Expire(TimeSpan now)
    {
        var kept = 0;
        for (var i = 0; i < _fadingCount; i++)
            if ((now - _fading[i].Since).TotalSeconds < _fading[i].Ease.Seconds && !_fading[i].Thing.Gone)
                _fading[kept++] = _fading[i];
        Array.Clear(_fading, kept, _fadingCount - kept);
        _fadingCount = kept;
    }

    private static float Distance(WorldPoint camera, Cell at)
    {
        var dx = at.X * 0.1f - camera.X;
        var dy = at.Y * 0.1f - camera.Y;
        var dz = at.Z * 0.1f - camera.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float ChunkDistance(WorldPoint camera, ChunkKey chunk)
    {
        var dx = Gap(camera.X, chunk.X);
        var dy = Gap(camera.Y, chunk.Y);
        var dz = Gap(camera.Z, chunk.Z);
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float Gap(float at, int chunk)
    {
        var min = chunk * ChunkKey.Metres;
        var max = min + ChunkKey.Metres;
        return at < min ? min - at : at > max ? at - max : 0f;
    }

    private readonly record struct Candidate(Thing Thing, LabelLook.Shown Label, float Distance, Duration Appear)
    {
        public bool Beats(Candidate other) =>
            Label.Priority != other.Label.Priority ? Label.Priority > other.Label.Priority : Distance < other.Distance;
    }
}
