using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Support;

namespace Lattice.Core.Validation;

/// <summary>
/// The main-thread facts handed to the worker (game answers, rooms, networks), each with the cell it is about, so a
/// world change drops the ones near it and the reconciler can ask them all again. Main thread.
/// <para>
/// Recording an answer runs in a checking step, once per ask of a large plan, so it allocates nothing once room is
/// reserved: the keys of a fine bucket are a chain of nodes in two arrays shared by every bucket (a key and the next
/// node), not a list per bucket.
/// </para>
/// </summary>
internal sealed class AnswerBook
{
    private const int End = -1;

    private readonly Dictionary<ReadKey, (Cell At, object Answer, int Post)> _answers = [];
    private readonly Dictionary<Cell, int> _byBucket = [];
    private ReadKey[] _keys = new ReadKey[64];
    private int[] _next = new int[64];
    private int _used;
    private int _free = End;
    private int _answersRoom;
    private int _bucketsRoom;

    public int Count => _answers.Count;

    /// <summary>Every key held, for a sweep.</summary>
    public List<ReadKey> Keys() => new(_answers.Keys);

    /// <summary>Whether an answer is held for the key.</summary>
    public bool Holds(ReadKey key) => _answers.ContainsKey(key);

    /// <summary>Whether the key's answer was given for a post later than <paramref name="settled"/>: still on its way to the worker.</summary>
    public bool OnItsWay(ReadKey key, int settled) => _answers.TryGetValue(key, out var held) && held.Post > settled;

    public bool TryGet(ReadKey key, out object answer)
    {
        if (_answers.TryGetValue(key, out var held))
        {
            answer = held.Answer;
            return true;
        }

        answer = null!;
        return false;
    }

    /// <summary>
    /// Makes room for <paramref name="answers"/> more answers, so recording them grows nothing; room at least doubles
    /// when it grows, so reserving a little more each time copies the book a few times, not each time.
    /// </summary>
    public void Reserve(int answers)
    {
        var wanted = _answers.Count + answers;
        if (_answersRoom < wanted)
            _answersRoom = _answers.EnsureCapacity(Math.Max(wanted, 2 * _answersRoom));
        if (_bucketsRoom < _byBucket.Count + answers)
            _bucketsRoom = _byBucket.EnsureCapacity(Math.Max(_byBucket.Count + answers, 2 * _bucketsRoom));
        if (_keys.Length < wanted)
        {
            var room = Math.Max(wanted, 2 * _keys.Length);
            Array.Resize(ref _keys, room);
            Array.Resize(ref _next, room);
        }
    }

    /// <summary>Records an answer about a cell, given to the worker with post <paramref name="post"/>.</summary>
    public void Record(ReadKey key, Cell at, object answer, int post)
    {
        if (!_answers.ContainsKey(key))
        {
            var bucket = ReadKey.FineBucketOf(at);
            _byBucket[bucket] = Node(key, _byBucket.TryGetValue(bucket, out var first) ? first : End);
        }

        _answers[key] = (at, answer, post);
    }

    /// <summary>How many times answers were dropped around changed buckets.</summary>
    public int DropPasses { get; private set; }

    /// <summary>
    /// Drops the answers about cells in the given fine buckets and the buckets around them (one large cell on every
    /// axis, a heuristic for "the game may answer differently now"); adds the keys dropped to <paramref name="dropped"/>.
    /// Linear in the buckets and the answers dropped; allocates nothing. A bucket dropped leaves the book, so one
    /// in the neighbourhood of several of them is dropped once.
    /// </summary>
    public void DropAround(HashSet<Cell> buckets, List<ReadKey> dropped)
    {
        DropPasses++;
        foreach (var centre in buckets)
        {
            for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                    for (var z = -1; z <= 1; z++)
                    {
                        var bucket = new Cell(centre.X + x, centre.Y + y, centre.Z + z);
                        if (!_byBucket.TryGetValue(bucket, out var first))
                            continue;
                        for (var node = first; node != End;)
                        {
                            if (_answers.Remove(_keys[node]))
                                dropped.Add(_keys[node]);
                            var next = _next[node];
                            Free(node);
                            node = next;
                        }

                        _byBucket.Remove(bucket);
                    }
        }
    }

    /// <summary>
    /// Forgets the answer held for a fact no ghost reads any longer, unless a later answer for it was given after
    /// <paramref name="settled"/> (still on its way to the worker). Linear in the keys of its fine bucket.
    /// </summary>
    public void Forget(ReadKey key, int settled)
    {
        if (!_answers.TryGetValue(key, out var held) || held.Post > settled)
            return;
        _answers.Remove(key);
        var bucket = ReadKey.FineBucketOf(held.At);
        if (!_byBucket.TryGetValue(bucket, out var first))
            return;
        var before = End;
        var node = first;
        while (node != End && _keys[node] != key)
            (before, node) = (node, _next[node]);
        if (node == End)
            return;
        var next = _next[node];
        Free(node);
        if (before != End)
            _next[before] = next;
        else if (next == End)
            _byBucket.Remove(bucket);
        else
            _byBucket[bucket] = next;
    }

    public void Clear()
    {
        _answers.Clear();
        _byBucket.Clear();
        Array.Clear(_keys, 0, _used);
        _used = 0;
        _free = End;
    }

    private int Node(ReadKey key, int next)
    {
        int node;
        if (_free != End)
        {
            node = _free;
            _free = _next[node];
        }
        else
        {
            if (_used == _keys.Length)
            {
                Array.Resize(ref _keys, 2 * _keys.Length);
                Array.Resize(ref _next, 2 * _next.Length);
            }

            node = _used++;
        }

        _keys[node] = key;
        _next[node] = next;
        return node;
    }

    private void Free(int node)
    {
        _keys[node] = default;
        _next[node] = _free;
        _free = node;
    }
}
