using System.Collections.Generic;
using System.Text;
using Lattice.Api;

namespace Lattice.Core.Written;

/// <summary>
/// The JSON encoding of the same tree, for agents: a document is one object (its header fields, then <c>"items"</c>);
/// an entry is a flat object of its fields, with its groups as <c>"ops"</c>, each an object whose <c>"op"</c> is the
/// group's word. A value written as an integer by the text form is a JSON number; everything else is a string; a key
/// that repeats is an array. One item per line, so a diff of two exports stays readable.
/// </summary>
internal static class JsonForm
{
    private const string Items = "items";
    private const string Ops = "ops";
    private const string Op = "op";

    public static string Write(Document document)
    {
        var json = new StringBuilder(256);
        json.Append('{');
        var any = WriteFields(json, document.Header.Fields);
        if (any)
            json.Append(',');
        json.Append('"').Append(Items).Append("\":[");
        for (var i = 0; i < document.Items.Count; i++)
        {
            json.Append(i == 0 ? "\n" : ",\n");
            WriteEntry(json, document.Items[i]);
        }

        json.Append(document.Items.Count == 0 ? "]}\n" : "\n]}\n");
        return json.ToString();
    }

    public static string Write(Entry entry)
    {
        var json = new StringBuilder(256);
        WriteEntry(json, entry);
        return json.ToString();
    }

    public static Result<Document, string> ReadDocument(string text)
    {
        var reader = new JsonReader(text);
        return reader.Value().Bind(value => reader.End().Map(_ => value)).Bind(static value =>
        {
            if (value is not JsonValue.Object document)
                return Result<Document, string>.Error("a document is an object");
            var header = new List<Field>();
            var items = new List<Entry>();
            foreach (var (key, member) in document.Members)
            {
                if (key != Items)
                {
                    if (!AddField(header, key, member))
                        return Result<Document, string>.Error($"bad value of '{key}'");
                    continue;
                }

                if (member is not JsonValue.Array array)
                    return Result<Document, string>.Error("items is an array");
                foreach (var item in array.Items)
                {
                    if (!ToEntry(item).TryGet(out var entry))
                        return Result<Document, string>.Error("bad item");
                    items.Add(entry);
                }
            }

            return Result<Document, string>.Ok(new Document(Entry.Of(ValueList.From(header)), ValueList.From(items)));
        });
    }

    public static Result<Entry, string> ReadEntry(string text)
    {
        var reader = new JsonReader(text);
        return reader.Value().Bind(value => reader.End().Map(_ => value)).Bind(ToEntry);
    }

    private static void WriteEntry(StringBuilder json, Entry entry)
    {
        json.Append('{');
        var any = WriteFields(json, entry.Fields);
        if (entry.Groups.Count > 0)
        {
            if (any)
                json.Append(',');
            json.Append('"').Append(Ops).Append("\":[");
            for (var i = 0; i < entry.Groups.Count; i++)
            {
                if (i > 0)
                    json.Append(',');
                json.Append("{\"").Append(Op).Append("\":");
                String(json, entry.Groups[i].Word);
                if (entry.Groups[i].Fields.Count > 0)
                    json.Append(',');
                WriteFields(json, entry.Groups[i].Fields);
                json.Append('}');
            }

            json.Append(']');
        }

        json.Append('}');
    }

    /// <summary>Writes the fields as members; a key that repeats becomes one array at its first place.</summary>
    private static bool WriteFields(StringBuilder json, ValueList<Field> fields)
    {
        var any = false;
        var done = new HashSet<string>();
        for (var i = 0; i < fields.Count; i++)
        {
            var key = fields[i].Key;
            if (done.Contains(key))
                continue;
            var count = 0;
            for (var j = i; j < fields.Count; j++)
                if (fields[j].Key == key)
                    count++;
            if (any)
                json.Append(',');
            any = true;
            String(json, key);
            json.Append(':');
            if (count == 1)
            {
                Scalar(json, fields[i].Value);
                continue;
            }

            done.Add(key);
            json.Append('[');
            var first = true;
            for (var j = i; j < fields.Count; j++)
                if (fields[j].Key == key)
                {
                    if (!first)
                        json.Append(',');
                    first = false;
                    Scalar(json, fields[j].Value);
                }

            json.Append(']');
        }

        return any;
    }

    private static void Scalar(StringBuilder json, string value)
    {
        if (WrittenValues.IsCanonicalInteger(value) && value.Length <= 15)
            json.Append(value);
        else
            String(json, value);
    }

    private static void String(StringBuilder json, string text)
    {
        json.Append('"');
        foreach (var c in text)
            switch (c)
            {
                case '"':
                    json.Append("\\\"");
                    break;
                case '\\':
                    json.Append("\\\\");
                    break;
                case '\n':
                    json.Append("\\n");
                    break;
                case '\r':
                    json.Append("\\r");
                    break;
                case '\t':
                    json.Append("\\t");
                    break;
                default:
                    if (c < ' ' || c == '\u007f')
                        json.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        json.Append(c);
                    break;
            }

        json.Append('"');
    }

    private static Result<Entry, string> ToEntry(JsonValue value)
    {
        if (value is not JsonValue.Object entry)
            return Result<Entry, string>.Error("an entry is an object");
        var fields = new List<Field>();
        var groups = new List<Group>();
        foreach (var (key, member) in entry.Members)
        {
            if (key != Ops)
            {
                if (!AddField(fields, key, member))
                    return Result<Entry, string>.Error($"bad value of '{key}'");
                continue;
            }

            if (member is not JsonValue.Array ops)
                return Result<Entry, string>.Error("ops is an array");
            foreach (var op in ops.Items)
            {
                if (op is not JsonValue.Object group || group.Members.Count == 0 || group.Members[0].Key != Op ||
                    group.Members[0].Value is not JsonValue.Text word)
                    return Result<Entry, string>.Error("an op is an object whose first member is op");
                var groupFields = new List<Field>();
                for (var i = 1; i < group.Members.Count; i++)
                    if (!AddField(groupFields, group.Members[i].Key, group.Members[i].Value))
                        return Result<Entry, string>.Error($"bad value of '{group.Members[i].Key}'");
                groups.Add(new Group(word.Value, ValueList.From(groupFields)));
            }
        }

        return Result<Entry, string>.Ok(new Entry(ValueList.From(fields), ValueList.From(groups)));
    }

    private static bool AddField(List<Field> fields, string key, JsonValue value)
    {
        switch (value)
        {
            case JsonValue.Text text:
                fields.Add(new Field(key, text.Value));
                return true;
            case JsonValue.Literal literal:
                fields.Add(new Field(key, literal.Value));
                return true;
            case JsonValue.Array array:
                foreach (var item in array.Items)
                    if (!AddField(fields, key, item) || item is JsonValue.Array)
                        return false;
                return array.Items.Count > 0;
            default:
                return false;
        }
    }
}

/// <summary>A parsed JSON value. Numbers, true, false and null keep their text as written.</summary>
internal abstract record JsonValue
{
    private JsonValue() { }

    public sealed record Text(string Value) : JsonValue;

    public sealed record Literal(string Value) : JsonValue;

    public sealed record Array(List<JsonValue> Items) : JsonValue;

    public sealed record Object(List<KeyValuePair<string, JsonValue>> Members) : JsonValue;
}

/// <summary>A small strict JSON reader (RFC 8259) for the written forms; nothing else in Lattice reads JSON.</summary>
internal sealed class JsonReader(string text)
{
    private const int MaxDepth = 32;
    private int _at;
    private int _depth;

    public Result<JsonValue, string> Value()
    {
        Space();
        if (_at >= text.Length)
            return Fail<JsonValue>("unexpected end");
        var c = text[_at];
        return c switch
        {
            '{' => Object(),
            '[' => Array(),
            '"' => String().Map(static s => (JsonValue)new JsonValue.Text(s)),
            _ => Literal(),
        };
    }

    public Result<Unit, string> End()
    {
        Space();
        return _at == text.Length ? Result<Unit, string>.Ok(Unit.Value) : Fail<Unit>("text after the value");
    }

    private Result<JsonValue, string> Object()
    {
        if (++_depth > MaxDepth)
            return Fail<JsonValue>("too deep");
        _at++;
        var members = new List<KeyValuePair<string, JsonValue>>();
        Space();
        if (Peek('}'))
        {
            _at++;
            _depth--;
            return Result<JsonValue, string>.Ok(new JsonValue.Object(members));
        }

        while (true)
        {
            Space();
            if (!Peek('"'))
                return Fail<JsonValue>("expected a key");
            if (!String().TryGet(out var key))
                return Fail<JsonValue>("bad key");
            Space();
            if (!Peek(':'))
                return Fail<JsonValue>("expected ':'");
            _at++;
            if (!Value().TryGet(out var value))
                return Fail<JsonValue>($"bad value of '{key}'");
            members.Add(new KeyValuePair<string, JsonValue>(key, value));
            Space();
            if (Peek(','))
            {
                _at++;
                continue;
            }

            if (!Peek('}'))
                return Fail<JsonValue>("expected ',' or '}'");
            _at++;
            _depth--;
            return Result<JsonValue, string>.Ok(new JsonValue.Object(members));
        }
    }

    private Result<JsonValue, string> Array()
    {
        if (++_depth > MaxDepth)
            return Fail<JsonValue>("too deep");
        _at++;
        var items = new List<JsonValue>();
        Space();
        if (Peek(']'))
        {
            _at++;
            _depth--;
            return Result<JsonValue, string>.Ok(new JsonValue.Array(items));
        }

        while (true)
        {
            if (!Value().TryGet(out var item))
                return Fail<JsonValue>("bad array item");
            items.Add(item);
            Space();
            if (Peek(','))
            {
                _at++;
                continue;
            }

            if (!Peek(']'))
                return Fail<JsonValue>("expected ',' or ']'");
            _at++;
            _depth--;
            return Result<JsonValue, string>.Ok(new JsonValue.Array(items));
        }
    }

    private Result<string, string> String()
    {
        _at++;
        var value = new StringBuilder();
        while (_at < text.Length)
        {
            var c = text[_at++];
            if (c == '"')
                return Result<string, string>.Ok(value.ToString());
            if (c < ' ')
                return Fail<string>("control character in a string");
            if (c != '\\')
            {
                value.Append(c);
                continue;
            }

            if (_at >= text.Length)
                break;
            var escape = text[_at++];
            switch (escape)
            {
                case '"' or '\\' or '/':
                    value.Append(escape);
                    break;
                case 'b':
                    value.Append('\b');
                    break;
                case 'f':
                    value.Append('\f');
                    break;
                case 'n':
                    value.Append('\n');
                    break;
                case 'r':
                    value.Append('\r');
                    break;
                case 't':
                    value.Append('\t');
                    break;
                case 'u':
                    if (_at + 4 > text.Length || !int.TryParse(text.Substring(_at, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var code))
                        return Fail<string>("bad \\u escape");
                    value.Append((char)code);
                    _at += 4;
                    break;
                default:
                    return Fail<string>("bad escape");
            }
        }

        return Fail<string>("unterminated string");
    }

    private Result<JsonValue, string> Literal()
    {
        var start = _at;
        while (_at < text.Length && text[_at] is not (',' or '}' or ']' or ' ' or '\n' or '\r' or '\t'))
            _at++;
        var literal = text.Substring(start, _at - start);
        return literal is "true" or "false" or "null" || IsNumber(literal)
            ? Result<JsonValue, string>.Ok(new JsonValue.Literal(literal))
            : Fail<JsonValue>($"bad value '{literal}'");
    }

    private static bool IsNumber(string literal) =>
        literal.Length > 0 && (literal[0] == '-' || char.IsDigit(literal[0])) &&
        double.TryParse(literal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    private bool Peek(char c) => _at < text.Length && text[_at] == c;

    private void Space()
    {
        while (_at < text.Length && text[_at] is ' ' or '\n' or '\r' or '\t')
            _at++;
    }

    private Result<T, string> Fail<T>(string problem)
        where T : notnull =>
        Result<T, string>.Error($"{problem} at {_at}");
}
