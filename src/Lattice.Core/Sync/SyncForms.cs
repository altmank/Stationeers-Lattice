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

/// <summary>One part of a join as read: the host's revision, which part of how many, and its plans and ghosts.</summary>
internal sealed record JoinPart(Revision Revision, int Part, int Parts, ValueList<Plan> Plans, ValueList<Ghost> Ghosts);

/// <summary>
/// A join in parts: each part is a join document (the join form) whose header adds <c>part= parts=</c>. The first
/// part carries every plan; the ghosts are cut into parts of <see cref="SyncLimits.JoinGhostsPerPart"/>, so a large
/// board is sent over several frames. A board with no ghosts is one part.
/// </summary>
internal static class JoinParts
{
    public static int Count(int ghosts, int perPart) => ghosts == 0 ? 1 : (ghosts + perPart - 1) / perPart;

    public static string Write(Revision revision, IReadOnlyList<Plan> plans, IReadOnlyList<Ghost> ghosts, int part, int perPart)
    {
        var parts = Count(ghosts.Count, perPart);
        var planRecords = new List<PlanRecord>(part == 0 ? plans.Count : 0);
        if (part == 0)
            foreach (var plan in plans)
                planRecords.Add(PlanRecord.Of(plan));
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
        foreach (var plan in read.Join.Plans)
        {
            if (plan is not PlanRecord.Known { Extra.Count: 0 } known)
                return Fail("unreadable plan");
            plans.Add(known.Plan);
        }

        var ghosts = new List<Ghost>(read.Join.Ghosts.Count);
        foreach (var ghost in read.Join.Ghosts)
        {
            if (ghost is not GhostRecord.Known { Extra.Count: 0 } known)
                return Fail("unreadable ghost");
            ghosts.Add(known.Ghost);
        }

        return Result<JoinPart, Reason>.Ok(new JoinPart(read.Join.Revision, part, parts, ValueList.From(plans), ValueList.From(ghosts)));
    }

    private static Result<JoinPart, Reason> Fail(string problem) => Result<JoinPart, Reason>.Error(SyncRules.Problem(problem));
}

/// <summary>
/// The lines of one change set: the live message form, one line per plan the set touches (plans ascending), each
/// numbered with the set's revision (only the host keeps plan files, so the file numbering stays on the host).
/// A client turns them back into the whole change set with its own board's ghosts and plans as the before states.
/// </summary>
internal static class SetLines
{
    public static string Write(ChangeSet set)
    {
        var parts = ChangeLines.Of(set);
        var lines = new List<ChangeLine>(parts.Count);
        foreach (var part in parts)
            lines.Add(ChangeLine.For(new LineNumber(set.Revision.Value), Option<LineNumber>.None, set, part.Ops));
        return LiveMessageForm.Write(ValueList.From(lines));
    }

    /// <summary>The change set the lines describe, against the board's current state; refused if they do not fit it.</summary>
    public static Result<ChangeSet, Reason> Read(string text, IReplicaBoard board)
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
        var seen = new HashSet<GhostId>();
        foreach (var fileLine in read.Lines)
        {
            if (fileLine is not FileLine.Read { Line: var line })
                return Fail("unreadable line");
            first ??= line;
            if (line.Set != first.Set || line.Revision != first.Revision || line.Command != first.Command || line.By != first.By || line.At != first.At)
                return Fail("lines of two change sets");
            foreach (var op in line.Ops)
            {
                switch (op)
                {
                    case ChangeOp.AddGhost { Ghost: GhostRecord.Known add }:
                        if (seen.Add(add.Ghost.Id))
                            added.Add(add.Ghost);
                        break;
                    case ChangeOp.ChangeGhost { Ghost: GhostRecord.Known change }:
                        if (!seen.Add(change.Ghost.Id))
                            break;
                        if (!board.Ghost(change.Ghost.Id).TryGet(out var before))
                            return Fail($"ghost {change.Ghost.Id.Value} is not on the board");
                        changed.Add(new GhostChange(before, change.Ghost));
                        break;
                    case ChangeOp.RemoveGhost remove:
                        if (!seen.Add(remove.Ghost))
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

        return Result<ChangeSet, Reason>.Ok(new ChangeSet(
            first!.Set,
            first.Command,
            first.By,
            first.Revision,
            first.At,
            ValueList.From(added),
            ValueList.From(changed),
            ValueList.From(removed),
            ValueList.From(opened),
            ValueList.From(planChanges),
            ValueList.From(finished)));
    }

    private static Result<ChangeSet, Reason> Fail(string problem) => Result<ChangeSet, Reason>.Error(SyncRules.Problem(problem));
}

/// <summary>A join's parts gathered until the last arrives, then the seed a client's board resets to.</summary>
internal sealed class JoinGather
{
    private readonly List<Plan> _plans = new();
    private readonly List<Ghost> _ghosts = new();
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
        return new BoardSeed(_revision, new IdCounters(ghost + 1, plan + 1, 1), ValueList.From(_plans), ValueList.From(_ghosts));
    }
}
