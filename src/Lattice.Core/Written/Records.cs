using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Written;

/// <summary>
/// What a change line says for every record in it, so its records leave those fields out when they match: the line's
/// actor (<c>by</c>), time (<c>at</c>) and board revision (<c>rev</c>). A join or template has none.
/// </summary>
internal sealed record LineContext(Option<Actor> By, Option<DateTimeOffset> At, Option<Revision> Revision)
{
    public static LineContext None { get; } = new(Option<Actor>.None, Option<DateTimeOffset>.None, Option<Revision>.None);
}

/// <summary>A written ghost: one Lattice reads (with the fields of a newer schema it keeps), or one it keeps whole.</summary>
internal abstract record GhostRecord
{
    private GhostRecord() { }

    public static GhostRecord Of(Ghost ghost) => new Known(ghost, ValueList<Field>.Empty);

    /// <param name="Ghost">The ghost.</param>
    /// <param name="Extra">Fields of a newer schema, written again after the known ones.</param>
    public sealed record Known(Ghost Ghost, ValueList<Field> Extra) : GhostRecord;

    /// <summary>A ghost this version cannot read (a newer intent kind, say): kept and written back field for field.</summary>
    public sealed record Opaque(ValueList<Field> Fields, string Problem) : GhostRecord;
}

/// <summary>A written plan: read, or kept whole.</summary>
internal abstract record PlanRecord
{
    private PlanRecord() { }

    public static PlanRecord Of(Plan plan) => new Known(plan, ValueList<Field>.Empty);

    public sealed record Known(Plan Plan, ValueList<Field> Extra) : PlanRecord;

    public sealed record Opaque(ValueList<Field> Fields, string Problem) : PlanRecord;
}

/// <summary>A written ghost draft (templates and the clipboard): read, or kept whole.</summary>
internal abstract record DraftRecord
{
    private DraftRecord() { }

    public static DraftRecord Of(GhostDraft draft) => new Known(draft, ValueList<Field>.Empty);

    public sealed record Known(GhostDraft Draft, ValueList<Field> Extra) : DraftRecord;

    public sealed record Opaque(ValueList<Field> Fields, string Problem) : DraftRecord;
}

/// <summary>
/// The ghost form. Fields in order: <c>ghost plan</c>, the intent (<c>build</c> | <c>remove</c> | <c>swap to</c> |
/// <c>finish</c>, then <c>paint name setting</c> of its finish), <c>rev</c>, <c>state</c> (left out when planned;
/// <c>part</c> when started; <c>why why-rule why-msg why.&lt;fact&gt;</c> when stale), <c>by at made vis</c>
/// (<c>vis</c> left out for everyone), <c>label tint</c>, one <c>group</c> per group and one
/// <c>data.&lt;key&gt;</c> per attachment (both sorted), then fields of a newer schema.
/// </summary>
internal static class GhostForm
{
    public static ValueList<Field> Write(GhostRecord record, LineContext context) => record switch
    {
        GhostRecord.Known known => Write(known.Ghost, known.Extra, context),
        GhostRecord.Opaque opaque => opaque.Fields,
        _ => ValueList<Field>.Empty,
    };

    public static ValueList<Field> Write(Ghost ghost, ValueList<Field> extra, LineContext context)
    {
        var fields = new FieldList()
            .Add("ghost", ghost.Id.Value)
            .Add("plan", ghost.Plan.Value);
        IntentForm.Write(fields, ghost.Intent);
        if (!context.Revision.TryGet(out var revision) || revision != ghost.Revision)
            fields.Add("rev", ghost.Revision.Value);
        StateForm.Write(fields, ghost.State);
        if (!context.By.TryGet(out var by) || by != ghost.PlacedBy)
            fields.Add("by", WrittenValues.Actor(ghost.PlacedBy));
        if (!context.At.TryGet(out var at) || at != ghost.PlacedAt)
            fields.Add("at", WrittenValues.Time(ghost.PlacedAt));
        fields.Add("made", ghost.MadeBy.Value);
        Common.WriteLooks(fields, ghost.Visibility, ghost.Label, ghost.Tint, ghost.Groups, ghost.Attached);
        return fields.AddAll(extra).ToList();
    }

    public static GhostRecord Read(ValueList<Field> fields, LineContext context)
    {
        var read = new FieldReader(fields);
        var problem = "";
        if (!Common.Get(read.Need("ghost").Bind(WrittenValues.Long), out var id, ref problem) ||
            !Common.Get(read.Need("plan").Bind(WrittenValues.Long), out var plan, ref problem) ||
            !Common.Get(IntentForm.Read(read), out var intent, ref problem) ||
            !Common.Get(Common.Contextual(read.Take("rev"), context.Revision, static t => WrittenValues.Long(t).Map(static v => new Revision(v)), "rev"), out var revision, ref problem) ||
            !Common.Get(StateForm.Read(read), out var state, ref problem) ||
            !Common.Get(Common.Contextual(read.Take("by"), context.By, WrittenValues.Actor, "by"), out var by, ref problem) ||
            !Common.Get(Common.Contextual(read.Take("at"), context.At, WrittenValues.Time, "at"), out var at, ref problem) ||
            !Common.Get(read.Need("made").Bind(static t => WrittenValues.Checked(Maker.Parse(t), "maker", t)), out var made, ref problem) ||
            !Common.Get(Common.ReadLooks(read), out var looks, ref problem))
            return new GhostRecord.Opaque(fields, problem);
        var ghost = new Ghost(
            new GhostId(id), new PlanId(plan), revision, intent, state, by, made, at,
            looks.Visibility, looks.Label, looks.Tint, looks.Groups, looks.Attached);
        return new GhostRecord.Known(ghost, read.Rest());
    }
}

/// <summary>The plan form: <c>plan rev name owner made vis</c>, one <c>data.&lt;key&gt;</c> per entry (sorted), then newer fields.</summary>
internal static class PlanForm
{
    public static ValueList<Field> Write(PlanRecord record, LineContext context) => record switch
    {
        PlanRecord.Known known => Write(known.Plan, known.Extra, context),
        PlanRecord.Opaque opaque => opaque.Fields,
        _ => ValueList<Field>.Empty,
    };

    public static ValueList<Field> Write(Plan plan, ValueList<Field> extra, LineContext context)
    {
        var fields = new FieldList().Add("plan", plan.Id.Value);
        if (!context.Revision.TryGet(out var revision) || revision != plan.Revision)
            fields.Add("rev", plan.Revision.Value);
        fields.Add("name", plan.Name.Value)
            .Add("owner", WrittenValues.Actor(plan.Owner))
            .Add("made", plan.MadeBy.Value);
        Common.WriteVisibility(fields, plan.Visibility);
        Common.WriteData(fields, plan.HigherLayerData);
        if (plan.Colour.TryGet(out var colour))
            fields.Add("colour", WrittenValues.Tint(colour));
        var builders = new List<string>(plan.BuildBy.Count);
        foreach (var builder in plan.BuildBy)
            builders.Add(builder.Value);
        builders.Sort(string.CompareOrdinal);
        foreach (var builder in builders)
            fields.Add("build-by", builder);
        return fields.AddAll(extra).ToList();
    }

    public static PlanRecord Read(ValueList<Field> fields, LineContext context)
    {
        var read = new FieldReader(fields);
        var problem = "";
        if (!Common.Get(read.Need("plan").Bind(WrittenValues.Long), out var id, ref problem) ||
            !Common.Get(Common.Contextual(read.Take("rev"), context.Revision, static t => WrittenValues.Long(t).Map(static v => new Revision(v)), "rev"), out var revision, ref problem) ||
            !Common.Get(read.Need("name").Bind(static t => WrittenValues.Checked(PlanName.Create(t), "name", t)), out var name, ref problem) ||
            !Common.Get(read.Need("owner").Bind(WrittenValues.Actor), out var owner, ref problem) ||
            !Common.Get(read.Need("made").Bind(static t => WrittenValues.Checked(Maker.Parse(t), "maker", t)), out var made, ref problem) ||
            !Common.Get(Common.ReadVisibility(read), out var visibility, ref problem) ||
            !Common.Get(Common.ReadData(read), out var data, ref problem) ||
            !Common.Get(Common.Optional(read.Take("colour"), WrittenValues.Tint), out var colour, ref problem))
            return new PlanRecord.Opaque(fields, problem);
        var buildBy = new List<ExecutorId>();
        foreach (var text in read.TakeAll("build-by"))
        {
            if (!ExecutorId.Parse(text).TryGet(out var builder))
                return new PlanRecord.Opaque(fields, $"bad builder '{text}'");
            buildBy.Add(builder);
        }

        return new PlanRecord.Known(new Plan(new PlanId(id), revision, name, owner, made, visibility, data) { Colour = colour, BuildBy = ValueSet.From(buildBy) }, read.Rest());
    }
}

/// <summary>The draft form (a ghost in local coordinates): the intent, <c>plan</c> if given, <c>made vis label tint group data.&lt;key&gt;</c>, then newer fields.</summary>
internal static class DraftForm
{
    public static ValueList<Field> Write(DraftRecord record) => record switch
    {
        DraftRecord.Known known => Write(known.Draft, known.Extra),
        DraftRecord.Opaque opaque => opaque.Fields,
        _ => ValueList<Field>.Empty,
    };

    public static ValueList<Field> Write(GhostDraft draft, ValueList<Field> extra)
    {
        var fields = new FieldList();
        IntentForm.Write(fields, draft.Intent);
        if (draft.Plan.TryGet(out var plan))
            fields.Add("plan", plan.Value);
        fields.Add("made", draft.MadeBy.Value);
        Common.WriteLooks(fields, draft.Visibility, draft.Label, draft.Tint, draft.Groups, draft.Attached);
        return fields.AddAll(extra).ToList();
    }

    public static DraftRecord Read(ValueList<Field> fields)
    {
        var read = new FieldReader(fields);
        var problem = "";
        if (!Common.Get(IntentForm.Read(read), out var intent, ref problem) ||
            !Common.Get(Common.Optional(read.Take("plan"), static t => WrittenValues.Long(t).Map(static v => new PlanId(v))), out var plan, ref problem) ||
            !Common.Get(read.Need("made").Bind(static t => WrittenValues.Checked(Maker.Parse(t), "maker", t)), out var made, ref problem) ||
            !Common.Get(Common.ReadLooks(read), out var looks, ref problem))
            return new DraftRecord.Opaque(fields, problem);
        return new DraftRecord.Known(
            new GhostDraft(intent, plan, made, looks.Visibility, looks.Label, looks.Tint, looks.Groups, looks.Attached),
            read.Rest());
    }
}

/// <summary>Intents: <c>build=&lt;placement&gt;</c>, <c>remove=&lt;structure&gt;</c>, <c>swap=&lt;structure&gt; to=&lt;placement&gt;</c>, <c>finish=&lt;structure&gt;</c>; a finish adds <c>paint name setting</c>.</summary>
internal static class IntentForm
{
    public static void Write(FieldList fields, Intent intent)
    {
        switch (intent)
        {
            case Intent.Build build:
                fields.Add("build", WrittenValues.Placement(build.Target));
                WriteFinish(fields, build.Finish);
                break;
            case Intent.Remove remove:
                fields.Add("remove", WrittenValues.Structure(remove.Target));
                break;
            case Intent.Swap swap:
                fields.Add("swap", WrittenValues.Structure(swap.Old)).Add("to", WrittenValues.Placement(swap.New));
                WriteFinish(fields, swap.Finish);
                break;
            case Intent.FinishOnly finish:
                fields.Add("finish", WrittenValues.Structure(finish.Target));
                WriteFinish(fields, Option.Some(finish.Finish));
                break;
            default:
                fields.Add("intent", intent.Kind.ToString());
                break;
        }
    }

    public static Result<Intent, string> Read(FieldReader read)
    {
        if (read.Take("build").TryGet(out var build))
            return WrittenValues.Placement(build).Bind(target => ReadFinish(read).Map(finish => (Intent)new Intent.Build(target, finish)));
        if (read.Take("remove").TryGet(out var remove))
            return WrittenValues.Structure(remove).Map(static target => (Intent)new Intent.Remove(target));
        if (read.Take("swap").TryGet(out var swap))
            return WrittenValues.Structure(swap)
                .Bind(old => read.Need("to").Bind(WrittenValues.Placement)
                    .Bind(to => ReadFinish(read).Map(finish => (Intent)new Intent.Swap(old, to, finish))));
        if (read.Take("finish").TryGet(out var finishOnly))
            return WrittenValues.Structure(finishOnly)
                .Bind(target => ReadFinish(read).Bind(finish => finish.TryGet(out var f)
                    ? Result<Intent, string>.Ok(new Intent.FinishOnly(target, f))
                    : Result<Intent, string>.Error("a finish ghost names a finish")));
        return Result<Intent, string>.Error("unknown intent");
    }

    private static void WriteFinish(FieldList fields, Option<Finish> finish)
    {
        if (!finish.TryGet(out var f))
            return;
        if (f.Colour.TryGet(out var colour))
            fields.Add("paint", colour.Index);
        if (f.Name.TryGet(out var name))
            fields.Add("name", name.Value);
        if (f.Setting.TryGet(out var setting))
            fields.Add("setting", WrittenValues.Double(setting.Value));
    }

    private static Result<Option<Finish>, string> ReadFinish(FieldReader read)
    {
        var problem = "";
        if (!Common.Get(Common.Optional(read.Take("paint"), static t => WrittenValues.Int(t).Bind(i => WrittenValues.Checked(PaintColour.Create(i), "paint", t))), out var colour, ref problem) ||
            !Common.Get(Common.Optional(read.Take("name"), static t => WrittenValues.Checked(ThingName.Create(t), "name", t)), out var name, ref problem) ||
            !Common.Get(Common.Optional(read.Take("setting"), static t => WrittenValues.Double(t).Bind(d => WrittenValues.Checked(Setting.Create(d), "setting", t))), out var setting, ref problem))
            return Result<Option<Finish>, string>.Error(problem);
        if (colour.IsNone && name.IsNone && setting.IsNone)
            return Result<Option<Finish>, string>.Ok(Option<Finish>.None);
        return WrittenValues.Checked(Finish.Create(colour, name, setting), "finish", "").Map(static f => Option.Some(f));
    }
}

/// <summary>Ghost states: nothing for planned; <c>state=started part=&lt;structure&gt;</c>; <c>state=stale</c> with its reason.</summary>
internal static class StateForm
{
    public static void Write(FieldList fields, GhostState state)
    {
        switch (state)
        {
            case GhostState.Planned:
                break;
            case GhostState.Started started:
                fields.Add("state", "started").Add("part", WrittenValues.Structure(started.PartBuilt));
                break;
            case GhostState.Stale stale:
                fields.Add("state", "stale");
                ReasonForm.Write(fields, "why", stale.Why);
                break;
            default:
                fields.Add("state", state.GetType().Name);
                break;
        }
    }

    public static Result<GhostState, string> Read(FieldReader read)
    {
        if (!read.Take("state").TryGet(out var state))
            return Result<GhostState, string>.Ok(new GhostState.Planned());
        return state switch
        {
            "started" => read.Need("part").Bind(WrittenValues.Structure).Map(static s => (GhostState)new GhostState.Started(s)),
            "stale" => ReasonForm.Read(read, "why").Map(static why => (GhostState)new GhostState.Stale(why)),
            _ => Result<GhostState, string>.Error($"unknown state '{state}'"),
        };
    }
}

/// <summary>A reason under a prefix: <c>&lt;p&gt;=code &lt;p&gt;-rule=rule &lt;p&gt;-msg=game message &lt;p&gt;.&lt;fact&gt;=value</c> (facts sorted).</summary>
internal static class ReasonForm
{
    public static void Write(FieldList fields, string prefix, Reason reason)
    {
        fields.Add(prefix, reason.Code.Value).Add(prefix + "-rule", reason.From.Value);
        if (reason.GameMessage.TryGet(out var message))
            fields.Add(prefix + "-msg", message);
        foreach (var key in Common.Sorted(reason.Facts.Keys))
            fields.Add(prefix + "." + key, reason.Facts[key]);
    }

    public static Result<Reason, string> Read(FieldReader read, string prefix)
    {
        var problem = "";
        if (!Common.Get(read.Need(prefix).Bind(static t => WrittenValues.Checked(ReasonCode.Parse(t), "code", t)), out var code, ref problem) ||
            !Common.Get(read.Need(prefix + "-rule").Bind(static t => WrittenValues.Checked(RuleId.Parse(t), "rule", t)), out var rule, ref problem))
            return Result<Reason, string>.Error(problem);
        var message = read.Take(prefix + "-msg");
        var facts = new Dictionary<string, string>();
        foreach (var (name, value) in read.TakePrefixed(prefix + "."))
            facts[name] = value;
        return Result<Reason, string>.Ok(new Reason(code, ValueMap.From<string, string>(facts), message, rule));
    }
}

/// <summary>What ghosts and drafts share in writing, and the read helpers.</summary>
internal static class Common
{
    public static bool Get<T>(Result<T, string> result, out T value, ref string problem)
        where T : notnull
    {
        if (result.TryGet(out value))
            return true;
        result.TryGetError(out problem);
        return false;
    }

    /// <summary>The written value, else the line's, else a problem.</summary>
    public static Result<T, string> Contextual<T>(Option<string> written, Option<T> fromLine, Func<string, Result<T, string>> parse, string key)
        where T : notnull
    {
        if (written.TryGet(out var text))
            return parse(text);
        return fromLine.TryGet(out var value) ? Result<T, string>.Ok(value) : Result<T, string>.Error($"missing {key}");
    }

    public static Result<Option<T>, string> Optional<T>(Option<string> written, Func<string, Result<T, string>> parse)
        where T : notnull =>
        written.TryGet(out var text) ? parse(text).Map(static v => Option.Some(v)) : Result<Option<T>, string>.Ok(Option<T>.None);

    public static List<string> Sorted(IEnumerable<string> keys)
    {
        var sorted = new List<string>(keys);
        sorted.Sort(string.CompareOrdinal);
        return sorted;
    }

    public static void WriteVisibility(FieldList fields, Visibility visibility)
    {
        if (visibility is not Visibility.Everyone)
            fields.Add("vis", WrittenValues.Visibility(visibility));
    }

    public static Result<Visibility, string> ReadVisibility(FieldReader read) =>
        read.Take("vis").TryGet(out var text) ? WrittenValues.Visibility(text) : Result<Visibility, string>.Ok(new Visibility.Everyone());

    public static void WriteData(FieldList fields, ValueMap<AttachmentKey, Attachment> data)
    {
        var keys = new List<AttachmentKey>(data.Count);
        foreach (var entry in data)
            keys.Add(entry.Key);
        keys.Sort(static (a, b) => string.CompareOrdinal(a.Value, b.Value));
        foreach (var key in keys)
            fields.Add("data." + key.Value, data[key].Value);
    }

    public static Result<ValueMap<AttachmentKey, Attachment>, string> ReadData(FieldReader read)
    {
        var data = new Dictionary<AttachmentKey, Attachment>();
        foreach (var (name, value) in read.TakePrefixed("data."))
        {
            if (!AttachmentKey.Parse(name).TryGet(out var key))
                return Result<ValueMap<AttachmentKey, Attachment>, string>.Error($"bad data key '{name}'");
            if (!Attachment.Create(value).TryGet(out var attachment))
                return Result<ValueMap<AttachmentKey, Attachment>, string>.Error($"bad data under '{name}'");
            data[key] = attachment;
        }

        return Result<ValueMap<AttachmentKey, Attachment>, string>.Ok(ValueMap.From<AttachmentKey, Attachment>(data));
    }

    public static void WriteLooks(FieldList fields, Visibility visibility, Option<Label> label, Option<Tint> tint, ValueSet<GroupName> groups, ValueMap<AttachmentKey, Attachment> data)
    {
        WriteVisibility(fields, visibility);
        if (label.TryGet(out var l))
            fields.Add("label", l.Value);
        if (tint.TryGet(out var t))
            fields.Add("tint", WrittenValues.Tint(t));
        var names = new List<string>(groups.Count);
        foreach (var group in groups)
            names.Add(group.Value);
        names.Sort(string.CompareOrdinal);
        foreach (var name in names)
            fields.Add("group", name);
        WriteData(fields, data);
    }

    public static Result<Looks, string> ReadLooks(FieldReader read)
    {
        var problem = "";
        if (!Get(ReadVisibility(read), out var visibility, ref problem) ||
            !Get(Optional(read.Take("label"), static t => WrittenValues.Checked(Label.Create(t), "label", t)), out var label, ref problem) ||
            !Get(Optional(read.Take("tint"), WrittenValues.Tint), out var tint, ref problem))
            return Result<Looks, string>.Error(problem);
        var groups = new List<GroupName>();
        foreach (var text in read.TakeAll("group"))
        {
            if (!GroupName.Create(text).TryGet(out var group))
                return Result<Looks, string>.Error($"bad group '{text}'");
            groups.Add(group);
        }

        return ReadData(read).Map(data => new Looks(visibility, label, tint, ValueSet.From(groups), data));
    }

    /// <summary>The look and data fields ghosts and drafts share.</summary>
    internal sealed record Looks(
        Visibility Visibility,
        Option<Label> Label,
        Option<Tint> Tint,
        ValueSet<GroupName> Groups,
        ValueMap<AttachmentKey, Attachment> Attached);
}
