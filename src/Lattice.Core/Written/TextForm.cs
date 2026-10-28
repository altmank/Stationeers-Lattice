using System.Collections.Generic;
using System.Text;
using Lattice.Api;

namespace Lattice.Core.Written;

/// <summary>
/// The text encoding of an <see cref="Entry"/>: one line of space-separated tokens. The first <c>lead</c> fields are
/// written as bare values in a fixed order; every other field is <c>key=value</c>; a token without '=' starts a group
/// (its word), and the fields after it belong to that group. Values escape only what would break the line: space,
/// '%' and control characters as <c>%XX</c> (keys also '=' and '#'), so text stays readable and other characters are
/// kept as written. A newer schema adds fields after the ones it knew, so a reader that keeps unknown fields and
/// writes them after its own writes the line unchanged.
/// </summary>
internal static class TextForm
{
    public static string Escape(string text, bool key) => TextEscape.Escape(text, key);

    public static Result<string, string> Unescape(string text)
    {
        if (text.IndexOf('%') < 0)
            return Result<string, string>.Ok(text);
        var plain = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '%')
            {
                plain.Append(text[i]);
                continue;
            }

            if (i + 2 >= text.Length || Hex(text[i + 1]) < 0 || Hex(text[i + 2]) < 0)
                return Result<string, string>.Error($"bad escape in '{text}'");
            plain.Append((char)((Hex(text[i + 1]) << 4) | Hex(text[i + 2])));
            i += 2;
        }

        return Result<string, string>.Ok(plain.ToString());
    }

    public static void Write(StringBuilder line, Entry entry, int lead)
    {
        var first = true;
        for (var i = 0; i < entry.Fields.Count; i++)
        {
            if (!first)
                line.Append(' ');
            first = false;
            var field = entry.Fields[i];
            if (i >= lead)
                line.Append(Escape(field.Key, key: true)).Append('=');
            line.Append(Escape(field.Value, key: false));
        }

        foreach (var group in entry.Groups)
        {
            if (!first)
                line.Append(' ');
            first = false;
            WriteGroup(line, group);
        }
    }

    /// <summary>One group as <see cref="Write(StringBuilder, Entry, int)"/> writes it: its word, then its fields.</summary>
    public static void WriteGroup(StringBuilder line, Group group)
    {
        line.Append(group.Word);
        foreach (var field in group.Fields)
            line.Append(' ').Append(Escape(field.Key, key: true)).Append('=').Append(Escape(field.Value, key: false));
    }

    public static string Write(Entry entry, int lead)
    {
        var line = new StringBuilder(128);
        Write(line, entry, lead);
        return line.ToString();
    }

    /// <summary>Reads a line written by <see cref="Write(Entry, int)"/>; the lead values get the given keys.</summary>
    public static Result<Entry, string> Read(string line, IReadOnlyList<string> leadKeys)
    {
        var tokens = line.Split(' ');
        if (tokens.Length < leadKeys.Count)
            return Result<Entry, string>.Error("too few values");
        var fields = new List<Field>(tokens.Length);
        var groups = new List<Group>();
        string? word = null;
        var groupFields = new List<Field>();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length == 0)
                return Result<Entry, string>.Error("empty value");
            if (i < leadKeys.Count)
            {
                if (!Unescape(token).TryGet(out var value))
                    return Result<Entry, string>.Error($"bad escape in '{token}'");
                fields.Add(new Field(leadKeys[i], value));
                continue;
            }

            var equals = token.IndexOf('=');
            if (equals < 0)
            {
                if (word is not null)
                    groups.Add(new Group(word, ValueList.From(groupFields)));
                word = token;
                groupFields.Clear();
                continue;
            }

            if (equals == 0)
                return Result<Entry, string>.Error($"empty key in '{token}'");
            if (!Unescape(token.Substring(0, equals)).TryGet(out var key) || !Unescape(token.Substring(equals + 1)).TryGet(out var text))
                return Result<Entry, string>.Error($"bad escape in '{token}'");
            (word is null ? fields : groupFields).Add(new Field(key, text));
        }

        if (word is not null)
            groups.Add(new Group(word, ValueList.From(groupFields)));
        return Result<Entry, string>.Ok(new Entry(ValueList.From(fields), ValueList.From(groups)));
    }

    private static int Hex(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}

/// <summary>CRC-32 (IEEE 802.3) of a line's UTF-8 bytes, as 8 lowercase hex digits.</summary>
internal static class LineChecksum
{
    public static string Of(string text)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in Encoding.UTF8.GetBytes(text))
            crc = Add(crc, b);
        return Text(crc);
    }

    private static uint Add(uint crc, byte b)
    {
        crc ^= b;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }

    private static string Text(uint crc) => (~crc).ToString("x8");

    /// <summary>
    /// The checksum of a line written in parts: each part is added as it is appended, and <see cref="Value"/> equals
    /// <see cref="Of"/> of the whole. Parts end between whole characters. Linear in the characters added.
    /// </summary>
    internal sealed class Running
    {
        private uint _crc = 0xFFFFFFFFu;
        private char[] _chars = new char[256];
        private byte[] _bytes = new byte[1024];

        /// <summary>Adds the characters of <paramref name="text"/> from <paramref name="start"/> to its end.</summary>
        public void Add(StringBuilder text, int start)
        {
            var count = text.Length - start;
            if (count <= 0)
                return;
            if (_chars.Length < count)
                _chars = new char[count];
            text.CopyTo(start, _chars, 0, count);
            var most = Encoding.UTF8.GetMaxByteCount(count);
            if (_bytes.Length < most)
                _bytes = new byte[most];
            var bytes = Encoding.UTF8.GetBytes(_chars, 0, count, _bytes, 0);
            for (var i = 0; i < bytes; i++)
                _crc = LineChecksum.Add(_crc, _bytes[i]);
        }

        public string Value => Text(_crc);
    }
}
