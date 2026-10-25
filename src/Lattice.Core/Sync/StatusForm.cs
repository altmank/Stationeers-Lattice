using System;
using System.Collections.Generic;
using System.Text;
using Lattice.Api;
using Lattice.Core.Executors;
using Lattice.Core.Written;

namespace Lattice.Core.Sync;

/// <summary>A status message as read: whether it is a join's, and each ghost's claim as the host held it when it wrote it.</summary>
internal sealed record StatusUpdate(bool Join, ValueList<ClaimState> Entries);

/// <summary>
/// The status form: <c>lattice-status &lt;schema&gt; join=&lt;0|1&gt;</c>, then one line per ghost.
/// <c>claim ghost= by= name= left=</c> is a claim with the lease seconds left on the host (to the hundredth), followed
/// by its status if it has one: <c>now=working step= of=</c>, <c>now=paused</c>, or <c>now=waiting</c> with its reason
/// under <c>why</c> (the reason form). <c>gone ghost=</c> is no claim and no status. Each line is the ghost's whole
/// state, so a later line wins and a line read twice changes nothing. The reader is strict: a line or field it does
/// not know, or a bad value, refuses the whole message.
/// </summary>
internal static class StatusForm
{
    public const string Form = "lattice-status";

    /// <summary>The message for the entries, in order. One text; linear in the entries.</summary>
    public static string Write(bool join, IReadOnlyList<ClaimState> entries)
    {
        var text = new StringBuilder(32 + 72 * entries.Count);
        text.Append(Form).Append(' ').Append(WrittenValues.Number(WrittenForms.Schema)).Append(join ? " join=1\n" : " join=0\n");
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case ClaimState.Held held:
                    text.Append("claim");
                    Field(text, "ghost", WrittenValues.Number(held.Ghost.Value));
                    Field(text, "by", held.By.Id.Value);
                    Field(text, "name", held.By.Name.Value);
                    Field(text, "left", WrittenValues.Double(Math.Round(Math.Max(0, held.LeftSeconds), 2)));
                    if (held.Status.TryGet(out var status))
                        Status(text, status);
                    break;
                case ClaimState.Gone gone:
                    text.Append("gone");
                    Field(text, "ghost", WrittenValues.Number(gone.Ghost.Value));
                    break;
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The message as written; refused, whole, when any part of it is not exactly the form.</summary>
    public static Result<StatusUpdate, Reason> Read(string text)
    {
        var lines = text.Split('\n');
        var count = lines.Length;
        while (count > 0 && lines[count - 1].Length == 0)
            count--;
        if (count < 1 || !Headers.Read(lines[0], Form).TryGet(out var header) || header.Schema != WrittenForms.Schema)
            return Fail("not a status message");
        var extra = new FieldReader(header.Extra);
        var joinText = extra.Take("join").Reduce("");
        if (joinText is not ("0" or "1") || extra.Rest().Count > 0)
            return Fail("bad status header");

        var entries = new List<ClaimState>(count - 1);
        for (var i = 1; i < count; i++)
        {
            if (!TextForm.Read(lines[i], ["n"]).TryGet(out var line) || line.Groups.Count > 0)
                return Fail($"line {i + 1}: unreadable");
            var read = new FieldReader(line.Fields);
            var entry = read.Take("n").Reduce("") switch
            {
                "claim" => Held(read),
                "gone" => Ghost(read).Map(static ghost => (ClaimState)new ClaimState.Gone(ghost)),
                _ => Result<ClaimState, string>.Error("not a status line"),
            };
            if (!entry.TryGet(out var state))
                return Fail($"line {i + 1}: {(entry.TryGetError(out var problem) ? problem : "unreadable")}");
            if (read.Rest().Count > 0)
                return Fail($"line {i + 1}: a field this version does not know");
            entries.Add(state);
        }

        return Result<StatusUpdate, Reason>.Ok(new StatusUpdate(joinText == "1", ValueList.From(entries)));
    }

    private static Result<ClaimState, string> Held(FieldReader read)
    {
        var problem = "";
        if (!Common.Get(Ghost(read), out var ghost, ref problem) ||
            !Common.Get(read.Need("by").Bind(static t => WrittenValues.Checked(ExecutorId.Parse(t), "executor", t)), out var by, ref problem) ||
            !Common.Get(read.Need("name").Bind(static t => WrittenValues.Checked(DisplayName.Create(t), "name", t)), out var name, ref problem) ||
            !Common.Get(read.Need("left").Bind(WrittenValues.Double), out var left, ref problem))
            return Result<ClaimState, string>.Error(problem);
        if (left < 0 || double.IsNaN(left) || double.IsInfinity(left))
            return Result<ClaimState, string>.Error("bad left");
        var status = Option<ExecutorStatus>.None;
        if (read.Take("now").TryGet(out var now))
        {
            if (!StatusOf(now, read).TryGet(out var said))
                return Result<ClaimState, string>.Error("bad status");
            status = Option.Some(said);
        }

        return Result<ClaimState, string>.Ok(new ClaimState.Held(ghost, new ExecutorInfo(by, name), left, status));
    }

    private static Result<ExecutorStatus, string> StatusOf(string now, FieldReader read)
    {
        switch (now)
        {
            case "working":
                if (!read.Need("step").Bind(WrittenValues.Int).TryGet(out var step) || !read.Need("of").Bind(WrittenValues.Int).TryGet(out var of) ||
                    step < 1 || of < 1)
                    return Result<ExecutorStatus, string>.Error("bad step");
                return Result<ExecutorStatus, string>.Ok(new ExecutorStatus.Working(step, of));
            case "paused":
                return Result<ExecutorStatus, string>.Ok(new ExecutorStatus.Paused());
            case "waiting":
                return ReasonForm.Read(read, "why").Map(static why => (ExecutorStatus)new ExecutorStatus.Waiting(why));
            default:
                return Result<ExecutorStatus, string>.Error("unknown status");
        }
    }

    private static Result<GhostId, string> Ghost(FieldReader read) =>
        read.Need("ghost").Bind(WrittenValues.Long).Bind(static id =>
            id > 0 ? Result<GhostId, string>.Ok(new GhostId(id)) : Result<GhostId, string>.Error("bad ghost"));

    private static void Status(StringBuilder text, ExecutorStatus status)
    {
        switch (status)
        {
            case ExecutorStatus.Working working:
                Field(text, "now", "working");
                Field(text, "step", WrittenValues.Number(working.Step));
                Field(text, "of", WrittenValues.Number(working.Of));
                break;
            case ExecutorStatus.Paused:
                Field(text, "now", "paused");
                break;
            case ExecutorStatus.Waiting waiting:
                Field(text, "now", "waiting");
                var fields = new FieldList();
                ReasonForm.Write(fields, "why", waiting.Why);
                foreach (var field in fields.ToList())
                    Field(text, field.Key, field.Value);
                break;
        }
    }

    private static void Field(StringBuilder text, string key, string value) =>
        text.Append(' ').Append(TextForm.Escape(key, key: true)).Append('=').Append(TextForm.Escape(value, key: false));

    private static Result<StatusUpdate, Reason> Fail(string problem) => Result<StatusUpdate, Reason>.Error(SyncRules.Problem(problem));
}
