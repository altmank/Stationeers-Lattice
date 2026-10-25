using System;
using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Written;

namespace Lattice.Core.Inspector;

/// <summary>
/// Ghost and plan stories read from the plan files, for the inspector and the
/// change-log export. A plan file is a tree: each line follows an earlier one, and a line
/// with an earlier sibling starts a branch. Stories hold the lines this version can read; the export holds every line
/// as written. Main thread; every call reads files: a plan's story and export are linear in its file, a ghost's story
/// in every plan file of the world.
/// </summary>
internal sealed class PlanInspector(IPlanFiles files) : IInspector
{
    /// <summary>Who gives the inspector's reasons.</summary>
    public static RuleId Rule => RuleId.Known("lattice.inspector");

    /// <inheritdoc />
    public Result<GhostStory, Reason> Story(GhostId ghost)
    {
        var found = new List<(ChangeLine Line, string Written, int Plan)>();
        var sets = new HashSet<ChangeSetId>();
        var plans = files.Plans;
        var filesTouching = 0;
        for (var plan = 0; plan < plans.Count; plan++)
        {
            if (!files.Read(plans[plan]).TryGet(out var read))
                continue;
            var before = found.Count;
            foreach (var line in read.File.Lines)
                if (line is FileLine.Read { Line: var change } && Touches(change, ghost) && sets.Add(change.Set))
                    found.Add((change, ChangeLineForm.Write(change), plan));
            if (found.Count > before)
                filesTouching++;
        }

        if (found.Count == 0)
            return Result<GhostStory, Reason>.Error(Reason.Of(ReasonCodes.NotFound, Rule, ValueMap.Of("ghost", Text(ghost.Value))));
        if (filesTouching > 1)
            found.Sort(static (a, b) => Order(a, b));

        var entries = new HistoryEntry[found.Count];
        for (var i = 0; i < found.Count; i++)
            entries[i] = Entry(found[i].Line, found[i].Written);
        return Result<GhostStory, Reason>.Ok(new GhostStory(ghost, ValueList.From(entries)));
    }

    /// <inheritdoc />
    public Result<PlanStory, Reason> Story(PlanId plan)
    {
        var file = Read(plan);
        if (!file.TryGet(out var read))
            return Result<PlanStory, Reason>.Error(file.TryGetError(out var why) ? why : Unknown(plan));

        var tree = FileTree.Of(read.File.Lines);
        var head = files.Head(plan).TryGet(out var loaded) && tree.Has(loaded.Value) ? loaded.Value : tree.Newest;
        var path = tree.PathTo(head);
        var entries = new List<HistoryEntry>(path.Count);
        foreach (var line in path)
            entries.Add(Entry(line, ChangeLineForm.Write(line)));
        return Result<PlanStory, Reason>.Ok(new PlanStory(plan, ValueList.From(entries), tree.Branches(files.Saves(plan))));
    }

    /// <inheritdoc />
    public Result<ValueList<string>, Reason> Export(PlanId plan)
    {
        var file = Read(plan);
        if (!file.TryGet(out var read))
            return Result<ValueList<string>, Reason>.Error(file.TryGetError(out var why) ? why : Unknown(plan));

        var lines = new List<string>(read.File.Lines.Count + 1)
        {
            PlanFileForm.Header(read.File.Schema, read.File.Extra).TrimEnd('\n'),
        };
        foreach (var line in read.File.Lines)
            lines.Add(line switch
            {
                FileLine.Read known => ChangeLineForm.Write(known.Line),
                FileLine.Kept kept => kept.Text,
                _ => "",
            });
        return Result<ValueList<string>, Reason>.Ok(ValueList.From(lines));
    }

    private Result<PlanFileRead, Reason> Read(PlanId plan)
    {
        var plans = files.Plans;
        for (var i = 0; i < plans.Count; i++)
            if (plans[i] == plan)
                return files.Read(plan);
        return Result<PlanFileRead, Reason>.Error(Unknown(plan));
    }

    private static bool Touches(ChangeLine line, GhostId ghost)
    {
        foreach (var op in line.Ops)
        {
            var id = op switch
            {
                ChangeOp.AddGhost add => IdOf(add.Ghost),
                ChangeOp.ChangeGhost change => IdOf(change.Ghost),
                ChangeOp.RemoveGhost remove => remove.Ghost,
                _ => default,
            };
            if (id == ghost)
                return true;
        }

        return false;
    }

    /// <summary>A ghost record's id; a record this version cannot read still names its ghost in the <c>ghost</c> field.</summary>
    private static GhostId IdOf(GhostRecord record)
    {
        if (record is GhostRecord.Known known)
            return known.Ghost.Id;
        if (record is GhostRecord.Opaque opaque)
            foreach (var field in opaque.Fields)
                if (field.Key == "ghost" && WrittenValues.Long(field.Value).TryGet(out var id))
                    return new GhostId(id);
        return default;
    }

    /// <summary>Lines from several files: by time, then board revision, then plan; one file keeps its own order.</summary>
    private static int Order((ChangeLine Line, string Written, int Plan) a, (ChangeLine Line, string Written, int Plan) b)
    {
        var byTime = a.Line.At.CompareTo(b.Line.At);
        if (byTime != 0)
            return byTime;
        if (a.Plan == b.Plan)
            return a.Line.Number.Value.CompareTo(b.Line.Number.Value);
        var byRevision = a.Line.Revision.Value.CompareTo(b.Line.Revision.Value);
        return byRevision != 0 ? byRevision : a.Plan.CompareTo(b.Plan);
    }

    private static HistoryEntry Entry(ChangeLine line, string written) =>
        new(line.Number, line.Follows, line.Command, line.By, line.At, written);

    private static Reason Unknown(PlanId plan) => Reason.Of(ReasonCodes.NotFound, Rule, ValueMap.Of("plan", Text(plan.Value)));

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
