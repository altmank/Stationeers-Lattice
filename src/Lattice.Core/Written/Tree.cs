using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Written;

/// <summary>One named value of a written record; <see cref="Value"/> is the text before escaping.</summary>
internal readonly record struct Field(string Key, string Value);

/// <summary>A named part of a record: one operation of a change line.</summary>
internal sealed record Group(string Word, ValueList<Field> Fields);

/// <summary>
/// The shared shape every written form reduces to: fields in order, then groups in order. The text and JSON encodings
/// write this tree, so one record reads the same in a plan file, a live message, a join and an agent's JSON.
/// </summary>
internal sealed record Entry(ValueList<Field> Fields, ValueList<Group> Groups)
{
    public static Entry Of(ValueList<Field> fields) => new(fields, ValueList<Group>.Empty);
}

/// <summary>A document: a header record, then its items in order.</summary>
internal sealed record Document(Entry Header, ValueList<Entry> Items);

/// <summary>Builds a field list in the order fields are added.</summary>
internal sealed class FieldList
{
    private readonly List<Field> _fields = new(16);

    public FieldList Add(string key, string value)
    {
        _fields.Add(new Field(key, value));
        return this;
    }

    public FieldList Add(string key, long value) => Add(key, WrittenValues.Number(value));

    public FieldList AddAll(ValueList<Field> fields)
    {
        foreach (var field in fields)
            _fields.Add(field);
        return this;
    }

    public ValueList<Field> ToList() => ValueList.From(_fields);
}

/// <summary>
/// Reads a field list: each known field is taken once, and what is left (fields of a newer schema) comes back in its
/// order from <see cref="Rest"/>, to be written again unchanged.
/// </summary>
internal sealed class FieldReader
{
    private readonly ValueList<Field> _fields;
    private readonly bool[] _taken;

    public FieldReader(ValueList<Field> fields)
    {
        _fields = fields;
        _taken = new bool[fields.Count];
    }

    /// <summary>Whether a field with the key is there and not taken yet.</summary>
    public bool Has(string key)
    {
        for (var i = 0; i < _fields.Count; i++)
            if (!_taken[i] && _fields[i].Key == key)
                return true;
        return false;
    }

    /// <summary>The first untaken field with the key, taken.</summary>
    public Option<string> Take(string key)
    {
        for (var i = 0; i < _fields.Count; i++)
            if (!_taken[i] && _fields[i].Key == key)
            {
                _taken[i] = true;
                return Option.Some(_fields[i].Value);
            }

        return Option<string>.None;
    }

    /// <summary>The field with the key, taken; a problem when it is missing.</summary>
    public Result<string, string> Need(string key) =>
        Take(key).Match(static v => Result<string, string>.Ok(v), () => Result<string, string>.Error($"missing {key}"));

    /// <summary>Every untaken field with the key, in order, taken.</summary>
    public List<string> TakeAll(string key)
    {
        var values = new List<string>();
        for (var i = 0; i < _fields.Count; i++)
            if (!_taken[i] && _fields[i].Key == key)
            {
                _taken[i] = true;
                values.Add(_fields[i].Value);
            }

        return values;
    }

    /// <summary>Every untaken field whose key starts with the prefix, as (key after the prefix, value), taken.</summary>
    public List<(string Name, string Value)> TakePrefixed(string prefix)
    {
        var values = new List<(string, string)>();
        for (var i = 0; i < _fields.Count; i++)
            if (!_taken[i] && _fields[i].Key.Length > prefix.Length && _fields[i].Key.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                _taken[i] = true;
                values.Add((_fields[i].Key.Substring(prefix.Length), _fields[i].Value));
            }

        return values;
    }

    /// <summary>The fields nothing took, in their order.</summary>
    public ValueList<Field> Rest()
    {
        var rest = new List<Field>();
        for (var i = 0; i < _fields.Count; i++)
            if (!_taken[i])
                rest.Add(_fields[i]);
        return ValueList.From(rest);
    }
}
