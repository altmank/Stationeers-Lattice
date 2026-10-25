using System;
using Lattice.Api;
using Lattice.Core.Slices;
using Lattice.Core.Styles;
using Lattice.Render.Gpu;
using Lattice.Render.Materials;
using UnityEngine;

namespace Lattice.Render.Labels;

/// <summary>One world-space text object from the fixed pool (TextMeshPro in the Unity host). Main thread.</summary>
internal interface ILabelSlot
{
    void Text(string text);

    void Style(TextStyle style);

    /// <summary>Shows it at a world point, facing the camera, at an opacity.</summary>
    void Place(Vector3 world, float alpha);

    void Hide();
}

/// <summary>Where the pool's text objects come from; asked only when drawing opens.</summary>
internal interface ILabelSurface
{
    ILabelSlot NewSlot();

    /// <summary>Whether a style's font draws a glyph (TextMeshPro <c>HasCharacter</c>), asked once per style and glyph.</summary>
    bool Has(TextStyle style, char glyph);
}

/// <summary>What labels say that the drawn thing does not hold: its plan's title, its build step, its bill count.</summary>
internal interface ILabelFacts
{
    bool PlanTitle(PlanId plan, out string title);

    /// <summary>The ghost's position in its plan's validation report, from 1.</summary>
    bool Step(GhostId ghost, out int step);

    bool BillCount(PlanId plan, PrefabKey piece, out int count);
}

/// <summary>A label world without plan facts: those labels say nothing.</summary>
internal sealed class NoLabelFacts : ILabelFacts
{
    public static NoLabelFacts Instance { get; } = new();

    private NoLabelFacts()
    {
    }

    public bool PlanTitle(PlanId plan, out string title)
    {
        title = "";
        return false;
    }

    public bool Step(GhostId ghost, out int step)
    {
        step = 0;
        return false;
    }

    public bool BillCount(PlanId plan, PrefabKey piece, out int count)
    {
        count = 0;
        return false;
    }
}

/// <summary>What Render's labels draw with: the pool's surface, the facts, the text style for unregistered keys, Core's clock.</summary>
internal sealed record LabelPorts(ILabelSurface Surface, ILabelFacts Facts, TextStyle Default, IClock Clock);

/// <summary>Registered text styles by key; <see cref="LibraryText.Default"/> is the ports' default unless registered.</summary>
internal sealed class TextStyles(TextStyle fallback)
{
    private (TextStyleKey Key, TextStyle Style)[] _styles = Array.Empty<(TextStyleKey, TextStyle)>();

    public TextStyle Default { get; } = fallback;

    public TextStyle Of(TextStyleKey key)
    {
        var styles = _styles;
        for (var i = 0; i < styles.Length; i++)
            if (styles[i].Key == key)
                return styles[i].Style;
        return Default;
    }

    /// <summary>Refused (<c>invalid-value</c>) for a size of zero or less, (<c>already-registered</c>) for a key already registered.</summary>
    public Result<IDisposable, Reason> Register(TextStyleKey key, TextStyle style)
    {
        if (style is null)
            throw new ArgumentNullException(nameof(style));
        if (key.Value.Length == 0)
            return Result<IDisposable, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RenderCodes.Rule, ValueMap.Of("text-style", "").With("problem", "no-key")));
        if (!(style.Size > 0f))
            return Result<IDisposable, Reason>.Error(Reason.Of(ReasonCodes.InvalidValue, RenderCodes.Rule, ValueMap.Of("text-style", key.Value).With("problem", "size")));
        var styles = _styles;
        for (var i = 0; i < styles.Length; i++)
            if (styles[i].Key == key)
                return Result<IDisposable, Reason>.Error(Reason.Of(ReasonCodes.AlreadyRegistered, RenderCodes.Rule, ValueMap.Of("text-style", key.Value)));
        var next = new (TextStyleKey, TextStyle)[styles.Length + 1];
        Array.Copy(styles, next, styles.Length);
        next[styles.Length] = (key, style);
        _styles = next;
        Revision++;
        return Result<IDisposable, Reason>.Ok(new Registration(this, key));
    }

    /// <summary>Rises with every registration and removal.</summary>
    public long Revision { get; private set; }

    private void Remove(TextStyleKey key)
    {
        var styles = _styles;
        var at = -1;
        for (var i = 0; i < styles.Length; i++)
            if (styles[i].Key == key)
                at = i;
        if (at < 0)
            return;
        var next = new (TextStyleKey, TextStyle)[styles.Length - 1];
        Array.Copy(styles, 0, next, 0, at);
        Array.Copy(styles, at + 1, next, at, styles.Length - at - 1);
        _styles = next;
        Revision++;
    }

    private sealed class Registration(TextStyles owner, TextStyleKey key) : IDisposable
    {
        public void Dispose() => owner.Remove(key);
    }
}

/// <summary>
/// Lattice's glyph icons (<see cref="LibraryIcons"/>): drawn as a character before the label's words. A font without the
/// glyph shows a plain-character stand-in from the same list, so an icon never draws as a missing box.
/// </summary>
internal static class Glyphs
{
    private static readonly (IconKey Key, char Glyph, string Plain)[] Table =
    {
        (LibraryIcons.Fails, '!', "!"),
        (LibraryIcons.Warns, '⚠', "!"),
        (LibraryIcons.Remove, '×', "x"),
        (LibraryIcons.Swap, '⇄', "<>"),
        (LibraryIcons.Finish, '✎', "*"),
        (LibraryIcons.Stale, '?', "?"),
        (LibraryIcons.Waiting, '…', "..."),
        (LibraryIcons.Paused, '⏸', "||"),
    };

    public static int Count => Table.Length;

    public static IconKey KeyAt(int index) => Table[index].Key;

    public static char GlyphAt(int index) => Table[index].Glyph;

    public static int IndexOf(IconKey key)
    {
        for (var i = 0; i < Table.Length; i++)
            if (Table[i].Key == key)
                return i;
        return -1;
    }

    public static string PlainAt(int index) => Table[index].Plain;
}

/// <summary>
/// A label's words, written into a reused buffer so a label whose words did not change allocates nothing: the glyph
/// icon of its look, then what <see cref="LabelText"/> names.
/// </summary>
internal sealed class LabelWords(ILabelFacts facts, ILabelSurface surface)
{
    private readonly char[] _buffer = new char[256];
    private TextStyle? _checkedStyle;
    private readonly bool[] _has = new bool[Glyphs.Count];

    public int Length { get; private set; }

    public ReadOnlySpan<char> Written => new(_buffer, 0, Length);

    public void Write(Thing thing, LabelLook.Shown label, TextStyle style)
    {
        Length = 0;
        var look = thing.Signature.Look;
        if (look is not null && look.Style.Icon.TryGet(out var icon) && icon is IconLook.Shown shown)
        {
            var at = Glyphs.IndexOf(shown.Icon);
            if (at >= 0)
            {
                if (Has(style, at))
                    Put(Glyphs.GlyphAt(at));
                else
                    Put(Glyphs.PlainAt(at));
                Put(' ');
            }
        }

        var before = Length;
        Says(thing, label.Says, style);
        if (Length == before && before > 0)
            Length--;
    }

    private void Says(Thing thing, LabelText says, TextStyle style)
    {
        var record = thing.Record;
        switch (says)
        {
            case LabelText.Literal literal:
                Put(literal.Words.Value);
                break;
            case LabelText.GhostLabel:
                if (record is not null && record.Label.TryGet(out var words))
                    Put(words.Value);
                break;
            case LabelText.FinishName:
                if (FinishOf(thing.Intent) is { } finish && finish.Name.TryGet(out var name))
                    Put(name.Value);
                break;
            case LabelText.FindingReason:
                var why = thing.Finding switch
                {
                    Finding.WillFail fail => fail.Why,
                    Finding.Warns warns => warns.Why,
                    _ => null,
                };
                if (why is null)
                    break;
                if (style.Wording.TryGetValue(why.Code, out var worded))
                    Put(worded.Value);
                else
                    Put(why.Code.Value);
                break;
            case LabelText.PlanTitle:
                if (record is not null && facts.PlanTitle(record.Plan, out var title))
                    Put(title);
                break;
            case LabelText.StepNumber:
                if (record is not null && facts.Step(record.Id, out var step))
                    Put(step);
                break;
            case LabelText.BillCount:
                if (record is not null && facts.BillCount(record.Plan, thing.Piece, out var count))
                    Put(count);
                break;
        }
    }

    private static Finish? FinishOf(Intent? intent) => intent switch
    {
        Intent.Build build => build.Finish.TryGet(out var f) ? f : null,
        Intent.Swap swap => swap.Finish.TryGet(out var f) ? f : null,
        Intent.FinishOnly only => only.Finish,
        _ => null,
    };

    private bool Has(TextStyle style, int glyph)
    {
        if (!ReferenceEquals(style, _checkedStyle))
        {
            _checkedStyle = style;
            for (var i = 0; i < _has.Length; i++)
                _has[i] = surface.Has(style, Glyphs.GlyphAt(i));
        }

        return _has[glyph];
    }

    private void Put(char c)
    {
        if (Length < _buffer.Length)
            _buffer[Length++] = c;
    }

    private void Put(string s)
    {
        for (var i = 0; i < s.Length; i++)
            Put(s[i]);
    }

    private void Put(int value)
    {
        if (value < 0)
        {
            Put('-');
            value = -value;
        }

        var start = Length;
        do
        {
            Put((char)('0' + value % 10));
            value /= 10;
        }
        while (value > 0);
        Array.Reverse(_buffer, start, Length - start);
    }
}

/// <summary>
/// Labels and glyph icons: Core chooses which labels show (48, and 16 fading); Render
/// keeps a slot per shown label from a fixed pool, sets a slot's words and style only when they change, places it at its
/// anchor and eases it in and out every frame, and projects candidates to screen for Core's overlap cull.
/// </summary>
internal sealed class LabelDraw : ILabelProjector
{
    public const int Pool = 64;

    /// <summary>A label's line height in metres per point of its text style's size.</summary>
    public const float MetresPerPoint = 0.01f;

    /// <summary>How far an Above or Below label sits from its thing's bounds, in metres.</summary>
    public const float Gap = 0.15f;

    private readonly IDrawPlan _plan;
    private readonly ICameraView _camera;
    private readonly LabelPorts _ports;
    private readonly LabelWords _words;
    private readonly Slot[] _slots = new Slot[Pool];

    public LabelDraw(IDrawPlan plan, ICameraView camera, LabelPorts ports)
    {
        _plan = plan;
        _camera = camera;
        _ports = ports;
        Styles = new TextStyles(ports.Default);
        _words = new LabelWords(ports.Facts, ports.Surface);
        for (var i = 0; i < Pool; i++)
            _slots[i] = new Slot(ports.Surface.NewSlot());
    }

    public TextStyles Styles { get; }

    /// <summary>Labels placed in the last frame.</summary>
    public int Shown { get; private set; }

    /// <summary>Words set on slots since drawing opened.</summary>
    public int TextWrites { get; private set; }

    public void Frame()
    {
        var now = _ports.Clock.Now;
        for (var i = 0; i < Pool; i++)
            _slots[i].Used = false;
        var count = Math.Min(_plan.LabelCount, Pool);
        var shown = 0;
        for (var i = 0; i < count; i++)
        {
            var label = _plan.LabelAt(i);
            var slot = SlotFor(label.Thing);
            if (slot is null)
                continue;
            slot.Used = true;
            var style = Styles.Of(label.Label.Style);
            if (!ReferenceEquals(slot.Style, style))
            {
                slot.Style = style;
                slot.Target.Style(style);
            }

            SetWords(slot, label, style);
            slot.Target.Place(Anchor(label.Thing, label.Label.Anchor, style), Alpha(label, now));
            slot.Placed = true;
            shown++;
        }

        for (var i = 0; i < Pool; i++)
        {
            var slot = _slots[i];
            if (slot.Used)
                continue;
            slot.Thing = null;
            if (!slot.Placed)
                continue;
            slot.Placed = false;
            slot.Target.Hide();
        }

        Shown = shown;
    }

    public void End()
    {
        for (var i = 0; i < Pool; i++)
            if (_slots[i].Placed)
            {
                _slots[i].Placed = false;
                _slots[i].Target.Hide();
            }
    }

    public bool Project(Thing thing, LabelLook.Shown label, out ScreenRect rect)
    {
        rect = default;
        var style = Styles.Of(label.Style);
        var anchor = Anchor(thing, label.Anchor, style);
        var height = style.Size * MetresPerPoint;
        if (!_camera.ToScreen(anchor, out var low) || !_camera.ToScreen(anchor + new Vector3(0f, height, 0f), out var high))
            return false;
        _words.Write(thing, label, style);
        var pixels = Math.Max(1f, Math.Abs(high.y - low.y));
        var width = pixels * 0.6f * Math.Max(1, _words.Length);
        rect = new ScreenRect(low.x - width * 0.5f, low.y, width, pixels);
        return true;
    }

    private Slot? SlotFor(Thing thing)
    {
        Slot? free = null;
        for (var i = 0; i < Pool; i++)
        {
            var slot = _slots[i];
            if (ReferenceEquals(slot.Thing, thing))
                return slot;
            if (free is null && !slot.Used && slot.Thing is null)
                free = slot;
        }

        if (free is null)
        {
            for (var i = 0; i < Pool && free is null; i++)
                if (!_slots[i].Used && !IsShown(_slots[i].Thing))
                    free = _slots[i];
        }

        if (free is not null)
        {
            free.Thing = thing;
            free.Length = -1;
        }

        return free;
    }

    private bool IsShown(Thing? thing)
    {
        if (thing is null)
            return false;
        var count = Math.Min(_plan.LabelCount, Pool);
        for (var i = 0; i < count; i++)
            if (ReferenceEquals(_plan.LabelAt(i).Thing, thing))
                return true;
        return false;
    }

    private void SetWords(Slot slot, ShownLabel label, TextStyle style)
    {
        _words.Write(label.Thing, label.Label, style);
        var written = _words.Written;
        if (slot.Length == written.Length && written.SequenceEqual(new ReadOnlySpan<char>(slot.Chars, 0, slot.Length)))
            return;
        written.CopyTo(slot.Chars);
        slot.Length = written.Length;
        slot.Target.Text(written.ToString());
        TextWrites++;
    }

    /// <summary>Where a label sits: above, at the centre of or below its thing's footprint boxes (its cell without them).</summary>
    private static Vector3 Anchor(Thing thing, LabelAnchor anchor, TextStyle style)
    {
        Vector3 min, max;
        if (thing.Boxes is { Length: > 0 } boxes)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (var i = 0; i < boxes.Length; i++)
            {
                min = Vector3.Min(min, new Vector3(boxes[i].Min.X, boxes[i].Min.Y, boxes[i].Min.Z));
                max = Vector3.Max(max, new Vector3(boxes[i].Max.X, boxes[i].Max.Y, boxes[i].Max.Z));
            }
        }
        else
        {
            min = max = new Vector3(thing.At.X * 0.1f, thing.At.Y * 0.1f, thing.At.Z * 0.1f);
        }

        var centre = (min + max) * 0.5f;
        var line = style.Size * MetresPerPoint;
        return anchor switch
        {
            LabelAnchor.Above => new Vector3(centre.x, max.y + Gap, centre.z),
            LabelAnchor.Below => new Vector3(centre.x, min.y - Gap - line, centre.z),
            _ => new Vector3(centre.x, centre.y - line * 0.5f, centre.z),
        };
    }

    /// <summary>Eased in over its appear time, out over its vanish time (Core's clock).</summary>
    private static float Alpha(ShownLabel label, TimeSpan now)
    {
        var ease = label.Ease.Seconds;
        var t = ease > 0f ? (float)((now - label.Since).TotalSeconds / ease) : 1f;
        t = Math.Min(1f, Math.Max(0f, t));
        return label.Fading ? 1f - t : t;
    }

    private sealed class Slot(ILabelSlot target)
    {
        public ILabelSlot Target { get; } = target;

        public readonly char[] Chars = new char[256];
        public int Length = -1;
        public Thing? Thing;
        public TextStyle? Style;
        public bool Used;
        public bool Placed;
    }
}
