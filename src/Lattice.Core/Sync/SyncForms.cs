using System.Collections.Generic;
using System.Text;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Written;

namespace Lattice.Core.Sync;

/// <summary>The rule sync's own problems come from, and how they read.</summary>
internal static class SyncRules
{
    public static RuleId Sync => RuleId.Known("lattice.sync");

    /// <summary><c>invalid-value</c> with the problem.</summary>
    public static Reason Problem(string problem) => Reason.Of(ReasonCodes.InvalidValue, Sync, ValueMap.Of("problem", problem));

    /// <summary><c>too-large</c>: a message of <paramref name="bytes"/> over the limit.</summary>
    public static Reason TooLarge(long bytes, long limit) =>
        Reason.Of(ReasonCodes.TooLarge, Sync, ValueMap.Of("bytes", WrittenValues.Number(bytes)).With("limit", WrittenValues.Number(limit)));
}

/// <summary>
/// Sync's sizes. <see cref="MaxMessageBytes"/> bounds every message in UTF-8 bytes, under LaunchPadBooster's own
/// 1 MiB message limit (<c>IModNetworking.MaxMessageSize</c>); a client message over it is dropped before it is read.
/// <see cref="JoinGhostsPerPart"/> is how many ghosts one join message carries.
/// </summary>
internal sealed record SyncLimits(int MaxMessageBytes, int JoinGhostsPerPart)
{
    /// <summary>1,000,000 bytes per message; 500 ghosts per join part (about 100 KB).</summary>
    public static SyncLimits Default { get; } = new(1_000_000, 500);

    public static int Bytes(string text) => Encoding.UTF8.GetByteCount(text);
}

/// <summary>What the host answers a client's command: applied as a change set, or refused with reasons.</summary>
internal abstract record Answer
{
    private Answer() { }

    /// <summary>Applied: the change set's id and revision; its lines come as a lines message.</summary>
    public sealed record Applied(ChangeSetId Set, Revision Revision) : Answer;

    /// <summary>Refused; nothing changed.</summary>
    public sealed record Refused(ValueList<Refusal> Why) : Answer;
}

/// <summary>
/// The answer form: <c>lattice-answer &lt;schema&gt; id=&lt;command id&gt;</c>, then <c>applied set= rev=</c>, or one
/// <c>refused</c> line per refusal (its reason in the reason form under <c>why</c>, then one <c>ghost=</c> per ghost).
/// </summary>
internal static class AnswerForm
{
    public const string Form = "lattice-answer";

    public static string Write(CommandId id, Answer answer)
    {
        var text = new StringBuilder(160);
        text.Append(Headers.Text(Form, WrittenForms.Schema, ValueList.Of(new Field("id", WrittenValues.Command(id))))).Append('\n');
        switch (answer)
        {
            case Answer.Applied applied:
                Line(text, new FieldList().Add("n", "applied").Add("set", applied.Set.Value).Add("rev", applied.Revision.Value));
                break;
            case Answer.Refused refused:
                foreach (var refusal in refused.Why)
                {
                    var fields = new FieldList().Add("n", "refused");
                    ReasonForm.Write(fields, "why", refusal.Why);
                    foreach (var ghost in refusal.Ghosts)
                        fields.Add("ghost", ghost.Value);
                    Line(text, fields);
                }

                break;
        }

        return text.ToString();
    }

    public static Result<(CommandId Id, Answer Answer), Reason> Read(string text)
    {
        var lines = text.Split('\n');
        var count = lines.Length;
        while (count > 0 && lines[count - 1].Length == 0)
            count--;
        if (count < 2 || !Headers.Read(lines[0], Form).TryGet(out var header) ||
            !new FieldReader(header.Extra).Need("id").Bind(WrittenValues.Command).TryGet(out var id))
            return Fail("not an answer");

        var refusals = new List<Refusal>(count - 1);
        for (var i = 1; i < count; i++)
        {
            if (!TextForm.Read(lines[i], ["n"]).TryGet(out var entry))
                return Fail($"line {i + 1}: unreadable");
            var read = new FieldReader(entry.Fields);
            var kind = read.Take("n").Reduce("");
            if (kind == "applied" && count == 2)
            {
                if (!read.Need("set").Bind(WrittenValues.Long).TryGet(out var set) || !read.Need("rev").Bind(WrittenValues.Long).TryGet(out var revision))
                    return Fail("bad applied line");
                return Result<(CommandId, Answer), Reason>.Ok((id, new Answer.Applied(new ChangeSetId(set), new Revision(revision))));
            }

            if (kind != "refused" || !ReasonForm.Read(read, "why").TryGet(out var why))
                return Fail($"line {i + 1}: not a refusal");
            var ghosts = new List<GhostId>();
            foreach (var ghost in read.TakeAll("ghost"))
            {
                if (!WrittenValues.Long(ghost).TryGet(out var ghostId))
                    return Fail($"line {i + 1}: bad ghost");
                ghosts.Add(new GhostId(ghostId));
            }

            refusals.Add(new Refusal(why, ValueList.From(ghosts)));
        }

        return Result<(CommandId, Answer), Reason>.Ok((id, new Answer.Refused(ValueList.From(refusals))));
    }

    private static void Line(StringBuilder text, FieldList fields) => text.Append(TextForm.Write(Entry.Of(fields.ToList()), 1)).Append('\n');

    private static Result<(CommandId, Answer), Reason> Fail(string problem) =>
        Result<(CommandId, Answer), Reason>.Error(SyncRules.Problem(problem));
}

/// <summary>
/// One part of a join as read: the host's revision, which part of how many, its plans with each plan's done count (a
/// plan not listed has none), and its ghosts.
/// </summary>
internal sealed record JoinPart(Revision Revision, int Part, int Parts, ValueList<Plan> Plans, ValueMap<PlanId, int> Done, ValueList<Ghost> Ghosts);

/// <summary>
/// A join in parts: each part is a join document (the join form) whose header adds <c>part= parts=</c>. The first
/// part carries every plan, each plan line with <c>done=</c> when the plan has ghosts done (absent: none); the ghosts are cut into parts of <see cref="SyncLimits.JoinGhostsPerPart"/>, so a large
/// board is sent over several frames. A board with no ghosts is one part.
/// </summary>
internal static class JoinParts
{
    private const string DoneField = "done";

    public static int Count(int ghosts, int perPart) => ghosts == 0 ? 1 : (ghosts + perPart - 1) / perPart;

    public static string Write(Revision revision, IReadOnlyList<Plan> plans, IReadOnlyDictionary<PlanId, int> done, IReadOnlyList<Ghost> ghosts, int part, int perPart)
    {
        var parts = Count(ghosts.Count, perPart);
        var planRecords = new List<PlanRecord>(part == 0 ? plans.Count : 0);
        if (part == 0)
            foreach (var plan in plans)
                planRecords.Add(new PlanRecord.Known(plan, done.TryGetValue(plan.Id, out var count) && count > 0
                    ? ValueList.Of(new Field(DoneField, WrittenValues.Number(count)))
                    : ValueList<Field>.Empty));
        var from = part * perPart;
        var to = from + perPart < ghosts.Count ? from + perPart : ghosts.Count;
        var ghostRecords = new List<GhostRecord>(to > from ? to - from : 0);
        for (var i = from; i < to; i++)
            ghostRecords.Add(GhostRecord.Of(ghosts[i]));
        return JoinForm.Write(new JoinSnapshot(revision, ValueList.From(planRecords), ValueList.From(ghostRecords))
        {
            Extra = ValueList.From(new[] { new Field("part", WrittenValues.Number(part)), new Field("parts", WrittenValues.Number(parts)) }),
        });
    }

    public static Result<JoinPart, Reason> Read(string text)
    {
        if (!JoinForm.Read(text).TryGet(out var read))
            return Fail("not a join");
        if (read.Problems.Count > 0 || read.Join.Others.Count > 0)
            return Fail("unreadable join line");
        var extra = new FieldReader(read.Join.Extra);
        if (!extra.Need("part").Bind(WrittenValues.Int).TryGet(out var part) || !extra.Need("parts").Bind(WrittenValues.Int).TryGet(out var parts) ||
            part < 0 || parts < 1 || part >= parts)
            return Fail("bad part");
        var plans = new List<Plan>(read.Join.Plans.Count);
        var done = new Dictionary<PlanId, int>();
        foreach (var plan in read.Join.Plans)
        {
            if (plan is not PlanRecord.Known known || !DoneOf(known.Extra).TryGet(out var count))
                return Fail("unreadable plan");
            plans.Add(known.Plan);
            if (count > 0)
                done[known.Plan.Id] = count;
        }

        var ghosts = new List<Ghost>(read.Join.Ghosts.Count);
        foreach (var ghost in read.Join.Ghosts)
        {
            if (ghost is not GhostRecord.Known { Extra.Count: 0 } known)
                return Fail("unreadable ghost");
            ghosts.Add(known.Ghost);
        }

        return Result<JoinPart, Reason>.Ok(new JoinPart(read.Join.Revision, part, parts, ValueList.From(plans), ValueMap<PlanId, int>.From(done), ValueList.From(ghosts)));
    }

    /// <summary>A plan line's done count: none when the line has no <c>done=</c>; nothing when it has another field or a count below one.</summary>
    private static Option<int> DoneOf(ValueList<Field> extra)
    {
        var read = new FieldReader(extra);
        var count = 0;
        if (read.Take(DoneField).TryGet(out var text) && (!WrittenValues.Int(text).TryGet(out count) || count < 1))
            return Option<int>.None;
        return read.Rest().Count == 0 ? Option.Some(count) : Option<int>.None;
    }

    private static Result<JoinPart, Reason> Fail(string problem) => Result<JoinPart, Reason>.Error(SyncRules.Problem(problem));
}

/// <summary>
/// The lines of one change set: the live message form, one line per plan the set touches (plans ascending), each
/// numbered with the set's revision (only the host keeps plan files, so the file numbering stays on the host); a set
/// that touches no plan is one line with no operations. The done counts the set gives plans outright ride in every
/// line's header as <c>done=&lt;plan&gt;:&lt;count&gt;,...</c> (plans ascending), never in a plan file.
/// A client turns them back into the whole change set with its own board's ghosts and plans as the before states.
/// </summary>
internal static class SetLines
{
    private const string DoneField = "done";

    public static string Write(ChangeSet set)
    {
        var parts = ChangeLines.Of(set);
        var extra = set.DoneSet.Count == 0
            ? ValueList<Field>.Empty
            : ValueList.Of(new Field(DoneField, DoneText(set.DoneSet)));
        var lines = new List<ChangeLine>(parts.Count > 0 ? parts.Count : 1);
        foreach (var part in parts)
            lines.Add(ChangeLine.For(new LineNumber(set.Revision.Value), Option<LineNumber>.None, set, part.Ops) with { Extra = extra });
        if (lines.Count == 0)
            lines.Add(ChangeLine.For(new LineNumber(set.Revision.Value), Option<LineNumber>.None, set, ValueList<ChangeOp>.Empty) with { Extra = extra });
        return LiveMessageForm.Write(ValueList.From(lines));
    }

    private static string DoneText(ValueMap<PlanId, int> done)
    {
        var plans = new List<PlanId>(done.Count);
        foreach (var pair in done)
            plans.Add(pair.Key);
        plans.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        var text = new StringBuilder(plans.Count * 8);
        foreach (var plan in plans)
            text.Append(text.Length > 0 ? "," : "").Append(WrittenValues.Number(plan.Value)).Append(':').Append(WrittenValues.Number(done[plan]));
        return text.ToString();
    }

    /// <summary>The done counts a line's header gives; empty with none; nothing when the field does not read.</summary>
    private static Option<ValueMap<PlanId, int>> DoneOf(ValueList<Field> extra)
    {
        var read = new FieldReader(extra);
        if (!read.Take(DoneField).TryGet(out var text))
            return Option.Some(ValueMap<PlanId, int>.Empty);
        var done = new Dictionary<PlanId, int>();
        foreach (var item in text.Split(','))
        {
            var colon = item.IndexOf(':');
            if (colon < 1 || !WrittenValues.Long(item.Substring(0, colon)).TryGet(out var plan) ||
                !WrittenValues.Int(item.Substring(colon + 1)).TryGet(out var count) || count < 0 || !done.TryAdd(new PlanId(plan), count))
                return Option<ValueMap<PlanId, int>>.None;
        }

        return Option.Some(ValueMap<PlanId, int>.From(done));
    }

    /// <summary>
    /// The change set the lines describe, against the board's current state; refused if they do not fit it. An op
    /// written into two plan parts (a ghost moved between plans) is taken once per kind, so a set that removes a ghost
    /// and adds it again under the same id keeps both.
    /// </summary>
    public static Result<ChangeSet, Reason> Read(string text, IBoardStates board)
    {
        var read = LiveMessageForm.Read(text);
        if (read.Problems.Count > 0 || read.Lines.Count == 0)
            return Fail("unreadable lines");
        var first = default(ChangeLine);
        var added = new List<Ghost>();
        var changed = new List<GhostChange>();
        var removed = new List<GhostRemoval>();
        var opened = new List<Plan>();
        var planChanges = new List<PlanChange>();
        var finished = new List<PlanId>();
        var seenAdded = new HashSet<GhostId>();
        var seenChanged = new HashSet<GhostId>();
        var seenRemoved = new HashSet<GhostId>();
        foreach (var fileLine in read.Lines)
        {
            if (fileLine is not FileLine.Read { Line: var line })
                return Fail("unreadable line");
            first ??= line;
            if (line.Set != first.Set || line.Revision != first.Revision || line.Command != first.Command || line.By != first.By || line.At != first.At ||
                !line.Extra.Equals(first.Extra))
                return Fail("lines of two change sets");
            foreach (var op in line.Ops)
            {
                switch (op)
                {
                    case ChangeOp.AddGhost { Ghost: GhostRecord.Known add }:
                        if (seenAdded.Add(add.Ghost.Id))
                            added.Add(add.Ghost);
                        break;
                    case ChangeOp.ChangeGhost { Ghost: GhostRecord.Known change }:
                        if (!seenChanged.Add(change.Ghost.Id))
                            break;
                        if (!board.Ghost(change.Ghost.Id).TryGet(out var before))
                            return Fail($"ghost {change.Ghost.Id.Value} is not on the board");
                        changed.Add(new GhostChange(before, change.Ghost));
                        break;
                    case ChangeOp.RemoveGhost remove:
                        if (!seenRemoved.Add(remove.Ghost))
                            break;
                        if (!board.Ghost(remove.Ghost).TryGet(out var gone))
                            return Fail($"ghost {remove.Ghost.Value} is not on the board");
                        removed.Add(new GhostRemoval(gone, remove.Cause));
                        break;
                    case ChangeOp.OpenPlan { Plan: PlanRecord.Known open }:
                        opened.Add(open.Plan);
                        break;
                    case ChangeOp.EditPlan { Plan: PlanRecord.Known edit }:
                        if (!board.Plan(edit.Plan.Id).TryGet(out var was))
                            return Fail($"plan {edit.Plan.Id.Value} is not on the board");
                        planChanges.Add(new PlanChange(was, edit.Plan));
                        break;
                    case ChangeOp.FinishPlan finish:
                        finished.Add(finish.Plan);
                        break;
                    default:
                        return Fail("an operation this version cannot read");
                }
            }
        }

        if (!DoneOf(first!.Extra).TryGet(out var doneSet))
            return Fail("unreadable done counts");
        return Result<ChangeSet, Reason>.Ok(new ChangeSet(
            first.Set,
            first.Command,
            first.By,
            first.Revision,
            first.At,
            ValueList.From(added),
            ValueList.From(changed),
            ValueList.From(removed),
            ValueList.From(opened),
            ValueList.From(planChanges),
            ValueList.From(finished))
        {
            DoneSet = doneSet,
        });
    }

    private static Result<ChangeSet, Reason> Fail(string problem) => Result<ChangeSet, Reason>.Error(SyncRules.Problem(problem));
}

/// <summary>
/// The rejoin form: <c>lattice-rejoin &lt;schema&gt;</c>, then one <c>pending id=</c> line per command the client
/// still waits on. A client that dropped a change set sends it to ask the host for the board again and for the
/// outcomes of those commands.
/// </summary>
internal static class RejoinForm
{
    public const string Form = "lattice-rejoin";

    public static string Write(IEnumerable<CommandId> pending)
    {
        var text = new StringBuilder(64);
        text.Append(Headers.Text(Form, WrittenForms.Schema, ValueList<Field>.Empty)).Append('\n');
        foreach (var id in pending)
            text.Append(TextForm.Write(Entry.Of(new FieldList().Add("n", "pending").Add("id", WrittenValues.Command(id)).ToList()), 1)).Append('\n');
        return text.ToString();
    }

    /// <summary>The pending commands the client names; refused, whole, when any line is not the form.</summary>
    public static Result<ValueList<CommandId>, Reason> Read(string text)
    {
        var lines = text.Split('\n');
        var count = lines.Length;
        while (count > 0 && lines[count - 1].Length == 0)
            count--;
        if (count < 1 || !Headers.Read(lines[0], Form).TryGet(out var header) || header.Schema != WrittenForms.Schema || header.Extra.Count > 0)
            return Fail("not a rejoin message");
        var pending = new List<CommandId>(count - 1);
        for (var i = 1; i < count; i++)
        {
            if (!TextForm.Read(lines[i], ["n"]).TryGet(out var entry))
                return Fail($"line {i + 1}: unreadable");
            var read = new FieldReader(entry.Fields);
            if (read.Take("n").Reduce("") != "pending" || !read.Need("id").Bind(WrittenValues.Command).TryGet(out var id))
                return Fail($"line {i + 1}: not a pending command");
            pending.Add(id);
        }

        return Result<ValueList<CommandId>, Reason>.Ok(ValueList.From(pending));
    }

    private static Result<ValueList<CommandId>, Reason> Fail(string problem) =>
        Result<ValueList<CommandId>, Reason>.Error(SyncRules.Problem(problem));
}

/// <summary>
/// A command's applied outcome whole, for a client whose board has moved past its change set (a resync join):
/// <c>lattice-applied &lt;schema&gt; id= plans= ghosts=</c>, then the plans and ghosts as they were before the set (one
/// per line, the plan and ghost forms), then the set's lines. The client reads the lines against those before states,
/// so the change set is the one it would have mirrored.
/// </summary>
internal static class AppliedForm
{
    public const string Form = "lattice-applied";

    public static bool Is(string text) => text.StartsWith(Form + " ", System.StringComparison.Ordinal);

    public static string Write(CommandId id, ChangeSet set)
    {
        var plans = new List<Plan>(set.PlansChanged.Count);
        foreach (var change in set.PlansChanged)
            plans.Add(change.Before);
        var ghosts = new Dictionary<GhostId, Ghost>(set.Changed.Count + set.Removed.Count);
        foreach (var change in set.Changed)
            ghosts[change.Before.Id] = change.Before;
        foreach (var removal in set.Removed)
            ghosts[removal.Ghost.Id] = removal.Ghost;

        var text = new StringBuilder(256);
        var header = new FieldList().Add("id", WrittenValues.Command(id)).Add("plans", plans.Count).Add("ghosts", ghosts.Count).ToList();
        text.Append(Headers.Text(Form, WrittenForms.Schema, header)).Append('\n');
        foreach (var plan in plans)
            text.Append(TextForm.Write(Entry.Of(PlanForm.Write(plan, ValueList<Field>.Empty, LineContext.None)), 0)).Append('\n');
        foreach (var ghost in ghosts.Values)
            text.Append(TextForm.Write(Entry.Of(GhostForm.Write(ghost, ValueList<Field>.Empty, LineContext.None)), 0)).Append('\n');
        return text.Append(SetLines.Write(set)).ToString();
    }

    public static Result<(CommandId Id, ChangeSet Set), Reason> Read(string text)
    {
        var lines = text.Split('\n');
        if (!Headers.Read(lines[0], Form).TryGet(out var header) || header.Schema != WrittenForms.Schema)
            return Fail("not an applied outcome");
        var fields = new FieldReader(header.Extra);
        if (!fields.Need("id").Bind(WrittenValues.Command).TryGet(out var id)
            || !fields.Need("plans").Bind(WrittenValues.Long).TryGet(out var planCount)
            || !fields.Need("ghosts").Bind(WrittenValues.Long).TryGet(out var ghostCount)
            || planCount < 0 || ghostCount < 0 || 1 + planCount + ghostCount > lines.Length)
            return Fail("bad applied header");

        var before = new BeforeStates();
        var at = 1;
        for (var i = 0; i < planCount; i++, at++)
        {
            if (!TextForm.Read(lines[at], []).TryGet(out var entry) || PlanForm.Read(entry.Fields, LineContext.None) is not PlanRecord.Known plan)
                return Fail($"line {at + 1}: not a plan");
            before.Plans[plan.Plan.Id] = plan.Plan;
        }

        for (var i = 0; i < ghostCount; i++, at++)
        {
            if (!TextForm.Read(lines[at], []).TryGet(out var entry) || GhostForm.Read(entry.Fields, LineContext.None) is not GhostRecord.Known ghost)
                return Fail($"line {at + 1}: not a ghost");
            before.Ghosts[ghost.Ghost.Id] = ghost.Ghost;
        }

        return SetLines.Read(string.Join("\n", lines, at, lines.Length - at), before).Map(set => (id, set));
    }

    private static Result<(CommandId, ChangeSet), Reason> Fail(string problem) =>
        Result<(CommandId, ChangeSet), Reason>.Error(SyncRules.Problem(problem));

    private sealed class BeforeStates : IBoardStates
    {
        public Dictionary<GhostId, Ghost> Ghosts { get; } = new();

        public Dictionary<PlanId, Plan> Plans { get; } = new();

        public Option<Ghost> Ghost(GhostId id) => Ghosts.TryGetValue(id, out var ghost) ? Option.Some(ghost) : Option<Ghost>.None;

        public Option<Plan> Plan(PlanId id) => Plans.TryGetValue(id, out var plan) ? Option.Some(plan) : Option<Plan>.None;
    }
}

/// <summary>A join's parts gathered until the last arrives, then the seed a client's board resets to.</summary>
internal sealed class JoinGather
{
    private readonly List<Plan> _plans = new();
    private readonly List<Ghost> _ghosts = new();
    private ValueMap<PlanId, int> _done = ValueMap<PlanId, int>.Empty;
    private Revision _revision;
    private int _next;
    private int _parts;

    /// <summary>Takes the next part; the seed once the last is in, none before. A part out of order restarts the gather.</summary>
    public Result<Option<BoardSeed>, Reason> Take(JoinPart part)
    {
        if (part.Part == 0)
        {
            _plans.Clear();
            _ghosts.Clear();
            _done = part.Done;
            _revision = part.Revision;
            _parts = part.Parts;
            _next = 0;
        }
        else if (part.Part != _next || part.Parts != _parts || part.Revision != _revision)
        {
            _next = 0;
            return Result<Option<BoardSeed>, Reason>.Error(SyncRules.Problem("join part out of order"));
        }

        _plans.AddRange(part.Plans);
        _ghosts.AddRange(part.Ghosts);
        _next = part.Part + 1;
        if (_next < _parts)
            return Result<Option<BoardSeed>, Reason>.Ok(Option<BoardSeed>.None);

        _next = 0;
        return Result<Option<BoardSeed>, Reason>.Ok(Option.Some(Seed()));
    }

    private BoardSeed Seed()
    {
        long ghost = 0, plan = GhostBoard.DefaultPlan.Value;
        foreach (var each in _ghosts)
            ghost = each.Id.Value > ghost ? each.Id.Value : ghost;
        foreach (var each in _plans)
            plan = each.Id.Value > plan ? each.Id.Value : plan;
        return new BoardSeed(_revision, new IdCounters(ghost + 1, plan + 1, 1), ValueList.From(_plans), ValueList.From(_ghosts)) { Done = _done };
    }
}
