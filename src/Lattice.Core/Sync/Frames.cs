using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Written;

namespace Lattice.Core.Sync;

/// <summary>
/// Host messages cut to the size the network carries: each frame is <c>&lt;k&gt; &lt;n&gt;</c>, a line break, then
/// the next piece of the text, at most <see cref="SyncLimits.MaxMessageBytes"/> UTF-8 bytes in all. A text that fits
/// is one frame. Pieces end on a character, never inside one.
/// </summary>
internal static class Frames
{
    // Room for "<k> <n>\n" with two ten-digit numbers.
    private const int HeaderRoom = 24;

    /// <summary>Where each piece starts in the text; the text's length ends the last one.</summary>
    public static List<int> Cut(string text, int maxBytes)
    {
        var room = maxBytes - HeaderRoom;
        var starts = new List<int>(1) { 0 };
        var bytes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var pair = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            var size = pair ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
            if (bytes + size > room)
            {
                starts.Add(i);
                bytes = 0;
            }

            bytes += size;
            if (pair)
                i++;
        }

        return starts;
    }

    public static string Frame(string text, List<int> starts, int index)
    {
        var from = starts[index];
        var to = index + 1 < starts.Count ? starts[index + 1] : text.Length;
        return WrittenValues.Number(index) + " " + WrittenValues.Number(starts.Count) + "\n" + text.Substring(from, to - from);
    }
}

/// <summary>A client's frames of one message kind, joined back into the host's text.</summary>
internal sealed class FrameGather
{
    private readonly System.Text.StringBuilder _text = new();
    private int _next;

    /// <summary>The whole text once its last frame is in, none before; a frame out of order drops what was gathered.</summary>
    public Result<Option<string>, Reason> Take(string frame)
    {
        var lineEnd = frame.IndexOf('\n');
        var space = lineEnd > 0 ? frame.IndexOf(' ', 0, lineEnd) : -1;
        if (space <= 0 || !WrittenValues.Int(frame.Substring(0, space)).TryGet(out var index) ||
            !WrittenValues.Int(frame.Substring(space + 1, lineEnd - space - 1)).TryGet(out var count) || count < 1 || index >= count)
            return Drop("bad frame");
        if (index != _next && index != 0)
            return Drop("frame out of order");
        if (index == 0)
            _text.Clear();
        _text.Append(frame, lineEnd + 1, frame.Length - lineEnd - 1);
        _next = index + 1;
        if (_next < count)
            return Result<Option<string>, Reason>.Ok(Option<string>.None);
        _next = 0;
        var text = _text.ToString();
        _text.Clear();
        return Result<Option<string>, Reason>.Ok(Option.Some(text));
    }

    private Result<Option<string>, Reason> Drop(string problem)
    {
        _next = 0;
        _text.Clear();
        return Result<Option<string>, Reason>.Error(SyncRules.Problem(problem));
    }
}
