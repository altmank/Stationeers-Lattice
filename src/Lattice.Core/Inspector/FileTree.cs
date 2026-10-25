using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Written;

namespace Lattice.Core.Inspector;

/// <summary>
/// The readable lines of one plan file as a tree: each line's parent is the line it follows (0 for the file's first).
/// A branch starts at the first line after 0 and at every line whose parent already had a child earlier in the file;
/// it runs through first children to its head. A line whose parent this version could not read starts a branch of its
/// own, and a path stops there.
/// </summary>
internal sealed class FileTree
{
    private readonly Dictionary<long, ChangeLine> _lines;
    private readonly Dictionary<long, List<long>> _children;
    private readonly List<long> _starts;

    private FileTree(Dictionary<long, ChangeLine> lines, Dictionary<long, List<long>> children, List<long> starts, long newest)
    {
        _lines = lines;
        _children = children;
        _starts = starts;
        Newest = newest;
    }

    /// <summary>The line appended last; 0 when there are none.</summary>
    public long Newest { get; }

    public static FileTree Of(ValueList<FileLine> file)
    {
        var lines = new Dictionary<long, ChangeLine>(file.Count);
        var children = new Dictionary<long, List<long>>(file.Count);
        var starts = new List<long>(4);
        var newest = 0L;
        foreach (var fileLine in file)
        {
            if (fileLine is not FileLine.Read { Line: var line } || !lines.TryAdd(line.Number.Value, line))
                continue;
            newest = line.Number.Value;
            var parent = line.Follows.TryGet(out var follows) ? follows.Value : 0;
            if (!children.TryGetValue(parent, out var siblings))
                children.Add(parent, siblings = new List<long>(1));
            if (siblings.Count > 0 || parent == 0 || !lines.ContainsKey(parent))
                starts.Add(line.Number.Value);
            siblings.Add(line.Number.Value);
        }

        return new FileTree(lines, children, starts, newest);
    }

    public bool Has(long line) => _lines.ContainsKey(line);

    /// <summary>The lines from the root to the line, in order; empty for 0.</summary>
    public List<ChangeLine> PathTo(long head)
    {
        var path = new List<ChangeLine>();
        var at = head;
        while (_lines.TryGetValue(at, out var line))
        {
            path.Add(line);
            at = line.Follows.TryGet(out var follows) ? follows.Value : 0;
        }

        path.Reverse();
        return path;
    }

    /// <summary>Every branch in the order it started, each with the saves whose line lies on it.</summary>
    public ValueList<Branch> Branches(ValueList<SaveAt> saves)
    {
        var branches = new List<Branch>(_starts.Count);
        var on = new List<SaveName>(saves.Count);
        foreach (var start in _starts)
        {
            var members = new HashSet<long> { start };
            var head = start;
            while (_children.TryGetValue(head, out var next))
            {
                head = next[0];
                members.Add(head);
            }

            on.Clear();
            foreach (var save in saves)
                if (members.Contains(save.Line.Value))
                    on.Add(save.Save);
            var follows = _lines[start].Follows.TryGet(out var parent) ? parent : new LineNumber(0);
            branches.Add(new Branch(follows, new LineNumber(head), ValueList.From(on)));
        }

        return ValueList.From(branches);
    }
}
