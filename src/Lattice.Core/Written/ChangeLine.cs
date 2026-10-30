using System;
using System.Collections.Generic;
using System.Text;
using Lattice.Api;

namespace Lattice.Core.Written;

/// <summary>One thing a change line does to its plan's file.</summary>
internal abstract record ChangeOp
{
    private ChangeOp() { }

    /// <summary>A new ghost (<c>add</c>, ghost form).</summary>
    public sealed record AddGhost(GhostRecord Ghost) : ChangeOp;

    /// <summary>
    /// A ghost as it is after the change (<c>change</c>, ghost form). A ghost moved to another plan is listed in both
    /// plans' lines with its new plan; the old plan's file reads that as the ghost leaving.
    /// </summary>
    public sealed record ChangeGhost(GhostRecord Ghost) : ChangeOp;

    /// <summary>A ghost that left the board (<c>remove ghost plan cause</c>, with <c>by-ghost</c> when replaced, <c>built done-at</c> when done).</summary>
    public sealed record RemoveGhost(GhostId Ghost, PlanId Plan, RemovalCause Cause) : ChangeOp
    {
        public ValueList<Field> Extra { get; init; } = ValueList<Field>.Empty;
    }

    /// <summary>A plan created or reopened (<c>open</c>, plan form).</summary>
    public sealed record OpenPlan(PlanRecord Plan) : ChangeOp;

    /// <summary>A plan as it is after the change (<c>edit</c>, plan form).</summary>
    public sealed record EditPlan(PlanRecord Plan) : ChangeOp;

    /// <summary>A plan finished (<c>finish plan</c>).</summary>
    public sealed record FinishPlan(PlanId Plan) : ChangeOp
    {
        public ValueList<Field> Extra { get; init; } = ValueList<Field>.Empty;
    }

    /// <summary>An operation this version cannot read (a newer kind): kept and written back as it was.</summary>
    public sealed record Opaque(string Word, ValueList<Field> Fields, string Problem) : ChangeOp;
}

/// <summary>
/// A change line: one change set's part for one plan file. It carries its number, the number of the line it follows
/// (none for a file's first line; after an older save is loaded, the save's point, so the file branches), the command,
/// the actor, the time, the change set and board revision, the operations, and on writing a checksum.
/// </summary>
internal sealed record ChangeLine(
    LineNumber Number,
    Option<LineNumber> Follows,
    CommandId Command,
    Actor By,
    DateTimeOffset At,
    ChangeSetId Set,
    Revision Revision,
    ValueList<ChangeOp> Ops)
{
    /// <summary>Header fields of a newer schema, written again after <c>set</c> and <c>rev</c>.</summary>
    public ValueList<Field> Extra { get; init; } = ValueList<Field>.Empty;

    /// <summary>What the line's records leave out when they match it.</summary>
    public LineContext Context => new(Option.Some(By), Option.Some(At), Option.Some(Revision));

    /// <summary>The line for one plan's part of a change set.</summary>
    public static ChangeLine For(LineNumber number, Option<LineNumber> follows, ChangeSet set, ValueList<ChangeOp> ops) =>
        new(number, follows, set.Command, set.By, set.At, set.Id, set.Revision, ops);
}

/// <summary>One plan's part of a change set.</summary>
internal sealed record PlanPart(PlanId Plan, ValueList<ChangeOp> Ops);

/// <summary>Splits a change set into the parts each plan file gets, plans in ascending id.</summary>
internal static class ChangeLines
{
    public static ValueList<PlanPart> Of(ChangeSet set)
    {
        var split = new ChangeSplit(set);
        split.Step(int.MaxValue);
        return split.Parts;
    }
}

/// <summary>
/// A change set split into plan parts a few changes at a time: plans opened, plans changed, ghosts added, changed and
/// removed, plans finished, in that order, each change into its plan's part (a ghost moved between plans into both).
/// <see cref="Parts"/> once <see cref="Done"/> is <see cref="ChangeLines.Of"/>'s answer. Linear in the changes split.
/// </summary>
internal sealed class ChangeSplit
{
    private readonly ChangeSet _set;
    private readonly SortedDictionary<long, List<ChangeOp>> _parts = new();
    private readonly int _total;
    private int _next;

    public ChangeSplit(ChangeSet set)
    {
        _set = set;
        _total = set.PlansOpened.Count + set.PlansChanged.Count + set.Added.Count + set.Changed.Count + set.Removed.Count + set.PlansFinished.Count;
    }

    public bool Done => _next == _total;

    /// <summary>Splits up to <paramref name="units"/> changes; returns how many it did.</summary>
    public int Step(int units)
    {
        var did = 0;
        for (; did < units && !Done; did++)
            Split(_next++);
        return did;
    }

    /// <summary>The parts in ascending plan id. Read once <see cref="Done"/>.</summary>
    public ValueList<PlanPart> Parts
    {
        get
        {
            var result = new List<PlanPart>(_parts.Count);
            foreach (var (plan, ops) in _parts)
                result.Add(new PlanPart(new PlanId(plan), ValueList.From(ops)));
            return ValueList.From(result);
        }
    }

    private void Split(int at)
    {
        var set = _set;
        if (at < set.PlansOpened.Count)
        {
            var plan = set.PlansOpened[at];
            Part(plan.Id).Add(new ChangeOp.OpenPlan(PlanRecord.Of(plan)));
            return;
        }

        at -= set.PlansOpened.Count;
        if (at < set.PlansChanged.Count)
        {
            var after = set.PlansChanged[at].After;
            Part(after.Id).Add(new ChangeOp.EditPlan(PlanRecord.Of(after)));
            return;
        }

        at -= set.PlansChanged.Count;
        if (at < set.Added.Count)
        {
            var ghost = set.Added[at];
            Part(ghost.Plan).Add(new ChangeOp.AddGhost(GhostRecord.Of(ghost)));
            return;
        }

        at -= set.Added.Count;
        if (at < set.Changed.Count)
        {
            var change = set.Changed[at];
            var op = new ChangeOp.ChangeGhost(GhostRecord.Of(change.After));
            if (change.Before.Plan != change.After.Plan)
                Part(change.Before.Plan).Add(op);
            Part(change.After.Plan).Add(op);
            return;
        }

        at -= set.Changed.Count;
        if (at < set.Removed.Count)
        {
            var removal = set.Removed[at];
            Part(removal.Ghost.Plan).Add(new ChangeOp.RemoveGhost(removal.Ghost.Id, removal.Ghost.Plan, removal.Cause));
            return;
        }

        at -= set.Removed.Count;
        var finished = set.PlansFinished[at];
        Part(finished).Add(new ChangeOp.FinishPlan(finished));
    }

    private List<ChangeOp> Part(PlanId plan)
    {
        if (!_parts.TryGetValue(plan.Value, out var ops))
            _parts.Add(plan.Value, ops = []);
        return ops;
    }
}

/// <summary>
/// The change-line form: <c>&lt;line&gt; &lt;follows&gt; &lt;command&gt; &lt;actor&gt; &lt;time&gt; set= rev=</c>,
/// newer header fields, then each operation as its word and fields, then <c> #&lt;crc-32&gt;</c> of everything before
/// it. <c>follows</c> is 0 for a file's first line.
/// </summary>
internal static class ChangeLineForm
{
    private const int LeadCount = 5;

    /// <summary>The keys of the values written bare at the start of a line, in order.</summary>
    public static IReadOnlyList<string> LeadKeys => ["line", "follows", "command", "by", "at"];

    public static Entry ToEntry(ChangeLine line)
    {
        var context = line.Context;
        var fields = new FieldList()
            .Add("line", line.Number.Value)
            .Add("follows", line.Follows.Map(static f => f.Value).Reduce(0))
            .Add("command", WrittenValues.Command(line.Command))
            .Add("by", WrittenValues.Actor(line.By))
            .Add("at", WrittenValues.Time(line.At))
            .Add("set", line.Set.Value)
            .Add("rev", line.Revision.Value)
            .AddAll(line.Extra);
        var groups = new List<Group>(line.Ops.Count);
        foreach (var op in line.Ops)
            groups.Add(ToGroup(op, context));
        return new Entry(fields.ToList(), ValueList.From(groups));
    }

    public static Result<ChangeLine, string> FromEntry(Entry entry)
    {
        var read = new FieldReader(entry.Fields);
        var problem = "";
        if (!Common.Get(read.Need("line").Bind(WrittenValues.Long), out var number, ref problem) ||
            !Common.Get(read.Need("follows").Bind(WrittenValues.Long), out var follows, ref problem) ||
            !Common.Get(read.Need("command").Bind(WrittenValues.Command), out var command, ref problem) ||
            !Common.Get(read.Need("by").Bind(WrittenValues.Actor), out var by, ref problem) ||
            !Common.Get(read.Need("at").Bind(WrittenValues.Time), out var at, ref problem) ||
            !Common.Get(read.Need("set").Bind(WrittenValues.Long), out var set, ref problem) ||
            !Common.Get(read.Need("rev").Bind(WrittenValues.Long), out var revision, ref problem))
            return Result<ChangeLine, string>.Error(problem);
        if (number < 1 || follows < 0 || follows >= number)
            return Result<ChangeLine, string>.Error($"line {number} cannot follow line {follows}");
        var line = new ChangeLine(
            new LineNumber(number),
            follows == 0 ? Option<LineNumber>.None : Option.Some(new LineNumber(follows)),
            command, by, at, new ChangeSetId(set), new Revision(revision), ValueList<ChangeOp>.Empty)
        {
            Extra = read.Rest(),
        };
        var ops = new List<ChangeOp>(entry.Groups.Count);
        foreach (var group in entry.Groups)
            ops.Add(FromGroup(group, line.Context));
        return Result<ChangeLine, string>.Ok(line with { Ops = ValueList.From(ops) });
    }

    /// <summary>The line as written, checksum included, without the line break.</summary>
    public static string Write(ChangeLine line)
    {
        var text = new StringBuilder(160);
        TextForm.Write(text, ToEntry(line), LeadCount);
        var content = text.ToString();
        return content + " #" + LineChecksum.Of(content);
    }

    /// <summary>
    /// The line's header as <see cref="Write"/> starts it: everything before its first operation, no checksum. With
    /// <see cref="WriteOp"/> for each operation in order and the checksum of the whole, it gives <see cref="Write"/>'s text.
    /// </summary>
    public static void WriteHeader(StringBuilder text, ChangeLine line) =>
        TextForm.Write(text, ToEntry(line with { Ops = ValueList<ChangeOp>.Empty }), LeadCount);

    /// <summary>One operation of <paramref name="line"/> as <see cref="Write"/> writes it after the header and the operations before it.</summary>
    public static void WriteOp(StringBuilder text, ChangeLine line, ChangeOp op) =>
        TextForm.WriteGroup(text.Append(' '), ToGroup(op, line.Context));

    /// <summary>Checks the checksum, then reads the line.</summary>
    public static Result<ChangeLine, LineProblem> Read(string text)
    {
        var mark = text.LastIndexOf(" #", StringComparison.Ordinal);
        if (mark < 0)
            return Result<ChangeLine, LineProblem>.Error(new LineProblem.BadChecksum());
        var content = text.Substring(0, mark);
        if (text.Substring(mark + 2) != LineChecksum.Of(content))
            return Result<ChangeLine, LineProblem>.Error(new LineProblem.BadChecksum());
        return TextForm.Read(content, LeadKeys).Bind(FromEntry).MapError(static p => (LineProblem)new LineProblem.Unreadable(p));
    }

    private static Group ToGroup(ChangeOp op, LineContext context) => op switch
    {
        ChangeOp.AddGhost add => new Group("add", GhostForm.Write(add.Ghost, context)),
        ChangeOp.ChangeGhost change => new Group("change", GhostForm.Write(change.Ghost, context)),
        ChangeOp.RemoveGhost remove => new Group("remove", RemoveFields(remove, context)),
        ChangeOp.OpenPlan open => new Group("open", PlanForm.Write(open.Plan, context)),
        ChangeOp.EditPlan edit => new Group("edit", PlanForm.Write(edit.Plan, context)),
        ChangeOp.FinishPlan finish => new Group("finish", new FieldList().Add("plan", finish.Plan.Value).AddAll(finish.Extra).ToList()),
        ChangeOp.Opaque opaque => new Group(opaque.Word, opaque.Fields),
        _ => new Group("unknown", ValueList<Field>.Empty),
    };

    private static ChangeOp FromGroup(Group group, LineContext context)
    {
        switch (group.Word)
        {
            case "add":
                return new ChangeOp.AddGhost(GhostForm.Read(group.Fields, context));
            case "change":
                return new ChangeOp.ChangeGhost(GhostForm.Read(group.Fields, context));
            case "open":
                return new ChangeOp.OpenPlan(PlanForm.Read(group.Fields, context));
            case "edit":
                return new ChangeOp.EditPlan(PlanForm.Read(group.Fields, context));
            case "remove":
                return ReadRemove(group, context);
            case "finish":
            {
                var read = new FieldReader(group.Fields);
                return read.Need("plan").Bind(WrittenValues.Long).Match(
                    plan => (ChangeOp)new ChangeOp.FinishPlan(new PlanId(plan)) { Extra = read.Rest() },
                    problem => new ChangeOp.Opaque(group.Word, group.Fields, problem));
            }

            default:
                return new ChangeOp.Opaque(group.Word, group.Fields, $"unknown operation '{group.Word}'");
        }
    }

    private static ValueList<Field> RemoveFields(ChangeOp.RemoveGhost remove, LineContext context)
    {
        var fields = new FieldList().Add("ghost", remove.Ghost.Value).Add("plan", remove.Plan.Value);
        switch (remove.Cause)
        {
            case RemovalCause.Removed:
                fields.Add("cause", "removed");
                break;
            case RemovalCause.Replaced replaced:
                fields.Add("cause", "replaced").Add("by-ghost", replaced.By.Value);
                break;
            case RemovalCause.Done done:
                fields.Add("cause", "done");
                if (done.How.Built.TryGet(out var built))
                    fields.Add("built", WrittenValues.Structure(built));
                if (!context.At.TryGet(out var at) || at != done.How.At)
                    fields.Add("done-at", WrittenValues.Time(done.How.At));
                if (done.How.By.TryGet(out var by))
                    fields.Add("by", by.Value);
                break;
            case RemovalCause.TargetGone:
                fields.Add("cause", "target-gone");
                break;
            default:
                fields.Add("cause", remove.Cause.GetType().Name);
                break;
        }

        return fields.AddAll(remove.Extra).ToList();
    }

    private static ChangeOp ReadRemove(Group group, LineContext context)
    {
        var read = new FieldReader(group.Fields);
        var problem = "";
        if (!Common.Get(read.Need("ghost").Bind(WrittenValues.Long), out var ghost, ref problem) ||
            !Common.Get(read.Need("plan").Bind(WrittenValues.Long), out var plan, ref problem) ||
            !Common.Get(read.Need("cause"), out var causeText, ref problem) ||
            !Common.Get(ReadCause(causeText, read, context), out var cause, ref problem))
            return new ChangeOp.Opaque(group.Word, group.Fields, problem);
        return new ChangeOp.RemoveGhost(new GhostId(ghost), new PlanId(plan), cause) { Extra = read.Rest() };
    }

    private static Result<RemovalCause, string> ReadCause(string cause, FieldReader read, LineContext context) => cause switch
    {
        "removed" => Result<RemovalCause, string>.Ok(new RemovalCause.Removed()),
        "replaced" => read.Need("by-ghost").Bind(WrittenValues.Long).Map(static by => (RemovalCause)new RemovalCause.Replaced(new GhostId(by))),
        "done" => Common.Optional(read.Take("built"), WrittenValues.Structure)
            .Bind(built => Common.Contextual(read.Take("done-at"), context.At, WrittenValues.Time, "done-at")
                .Bind(at => Common.Optional(read.Take("by"), static t => WrittenValues.Checked(ExecutorId.Parse(t), "executor", t))
                    .Map(by => (RemovalCause)new RemovalCause.Done(new Completion(built, at) { By = by })))),
        "target-gone" => Result<RemovalCause, string>.Ok(new RemovalCause.TargetGone()),
        _ => Result<RemovalCause, string>.Error($"unknown cause '{cause}'"),
    };
}

/// <summary>Why a written line was not taken.</summary>
internal abstract record LineProblem
{
    private LineProblem() { }

    /// <summary>The checksum is missing or does not match: the line is not trusted.</summary>
    public sealed record BadChecksum : LineProblem;

    /// <summary>The checksum matches but the line's header cannot be read.</summary>
    public sealed record Unreadable(string Problem) : LineProblem;
}
