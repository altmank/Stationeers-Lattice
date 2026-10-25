using System;
using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;
using Lattice.Core.Written;

namespace Lattice.Core.Saving;

/// <summary>One line of a plan file as the tree knows it: its number, the line it follows (0 for none), its text.</summary>
internal readonly record struct TreeLine(long Number, long Follows, string Text);

/// <summary>Why part of a plan file was not taken.</summary>
internal abstract record FileProblem
{
    private FileProblem() { }

    /// <summary>The last line was cut off mid-write (a crash): dropped, kept aside, reported.</summary>
    public sealed record TornLine(PlanId Plan, string Text) : FileProblem;

    /// <summary>A line whose number cannot be read, or whose checksum fails when it is replayed: skipped, reported.</summary>
    public sealed record BadLine(PlanId Plan, long Number, string Problem) : FileProblem;

    /// <summary>The file's header is not a plan file header: the plan is left out, reported.</summary>
    public sealed record BadFile(PlanId Plan, string Problem) : FileProblem;
}

/// <summary>
/// A plan file as a tree: every line has a number and the number of the line it
/// follows; lines are only appended, so after an older save is loaded the next line follows that save's point and the
/// file branches. Built from each line's two leading fields only, so a long file is indexed without reading its
/// records; a line is read whole only when it is replayed. Linear in the file's length to build; the walks are linear
/// in the path length.
/// </summary>
internal sealed class PlanTree
{
    private readonly Dictionary<long, TreeLine> _lines = new();
    private readonly Dictionary<long, List<long>> _children = new();

    private PlanTree(PlanId plan) => Plan = plan;

    public PlanId Plan { get; }

    /// <summary>How many lines the tree holds.</summary>
    public int Count => _lines.Count;

    /// <summary>The highest line number in the file: the newest line written.</summary>
    public long Newest { get; private set; }

    /// <summary>The number the next appended line takes.</summary>
    public long NextNumber => Newest + 1;

    /// <summary>An empty tree for a plan that has no file yet.</summary>
    public static PlanTree Empty(PlanId plan) => new(plan);

    /// <summary>Indexes a plan file's text. A last line with no line break is torn: dropped and reported.</summary>
    public static (PlanTree Tree, List<FileProblem> Problems) Read(PlanId plan, string text)
    {
        var tree = new PlanTree(plan);
        var problems = new List<FileProblem>();
        var start = text.IndexOf('\n');
        if (start < 0 || !Headers.Read(text.Substring(0, start), WrittenForms.PlanFile).IsOk)
        {
            problems.Add(new FileProblem.BadFile(plan, start < 0 ? "no header" : "not a plan file"));
            return (tree, problems);
        }

        var at = start + 1;
        while (at < text.Length)
        {
            var end = text.IndexOf('\n', at);
            if (end < 0)
            {
                problems.Add(new FileProblem.TornLine(plan, text.Substring(at)));
                break;
            }

            if (end > at)
            {
                var line = text.Substring(at, end - at);
                if (Lead(line, out var number, out var follows))
                    tree.Add(new TreeLine(number, follows, line));
                else
                    problems.Add(new FileProblem.BadLine(plan, 0, "no line number"));
            }

            at = end + 1;
        }

        return (tree, problems);
    }

    /// <summary>Adds a line written in this session.</summary>
    public void Add(TreeLine line)
    {
        if (!_lines.TryAdd(line.Number, line))
            return;
        if (line.Number > Newest)
            Newest = line.Number;
        if (!_children.TryGetValue(line.Follows, out var children))
            _children.Add(line.Follows, children = new List<long>(1));
        children.Add(line.Number);
    }

    public bool Has(long number) => _lines.ContainsKey(number);

    public TreeLine Line(long number) => _lines[number];

    /// <summary>The lines from the file's first line to <paramref name="head"/>, in order; empty for head 0.</summary>
    public List<TreeLine> PathTo(long head)
    {
        var path = new List<TreeLine>();
        var guard = _lines.Count;
        for (var at = head; at != 0 && _lines.TryGetValue(at, out var line) && guard-- >= 0; at = line.Follows)
            path.Add(line);
        path.Reverse();
        return path;
    }

    /// <summary>
    /// The lines after <paramref name="head"/> on its newest branch: from the head, always the newest line that follows
    /// the one before. These are the plan's changes made after a save at <paramref name="head"/>.
    /// </summary>
    public List<long> NewestAfter(long head)
    {
        var after = new List<long>();
        for (var at = head; _children.TryGetValue(at, out var children) && children.Count > 0;)
        {
            var newest = children[0];
            foreach (var child in children)
                if (child > newest)
                    newest = child;
            after.Add(newest);
            at = newest;
        }

        return after;
    }

    /// <summary>Reads a line's number and the number it follows from its first two fields.</summary>
    internal static bool Lead(string line, out long number, out long follows)
    {
        number = 0;
        follows = 0;
        var first = line.IndexOf(' ');
        if (first <= 0)
            return false;
        var second = line.IndexOf(' ', first + 1);
        if (second < 0)
            second = line.Length;
        return long.TryParse(line.Substring(0, first), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
               && long.TryParse(line.Substring(first + 1, second - first - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out follows)
               && number > 0;
    }
}
