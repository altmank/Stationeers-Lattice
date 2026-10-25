using System.Collections.Generic;
using System.Text;
using Lattice.Api;

namespace Lattice.Core.Written;

/// <summary>The written forms' names and schema version, and the rule their read problems come from.</summary>
internal static class WrittenForms
{
    /// <summary>The schema this version writes. A reader takes any version and keeps what it does not know.</summary>
    public const int Schema = 1;

    public const string PlanFile = "lattice-plan";
    public const string Live = "lattice-live";
    public const string Join = "lattice-join";
    public const string Template = "lattice-template";
    public const string Clipboard = "lattice-clipboard";

    /// <summary>The rule id of written-form problems.</summary>
    public static RuleId Rule => RuleId.Known("lattice.written");

    /// <summary>A problem as a reason: <c>invalid-value</c> with the problem and where it is.</summary>
    public static Reason Problem(string problem) =>
        Reason.Of(ReasonCodes.InvalidValue, Rule, ValueMap.Of("problem", problem));
}

/// <summary>A line of a plan file or live message: read, or kept as written because this version cannot read it.</summary>
internal abstract record FileLine
{
    private FileLine() { }

    public sealed record Read(ChangeLine Line) : FileLine;

    /// <summary>A line whose checksum holds but whose header this version cannot read: kept byte for byte.</summary>
    public sealed record Kept(string Text, string Problem) : FileLine;
}

/// <summary>Something a reader dropped or could not read, by line of the text (the header is line 1 of a file).</summary>
internal abstract record ReadProblem
{
    private ReadProblem() { }

    /// <summary>The last line has no line break (a crash while writing it): dropped.</summary>
    public sealed record TornLastLine(int At, string Text) : ReadProblem;

    /// <summary>The line's checksum does not match: reported and not trusted (left out).</summary>
    public sealed record BadChecksum(int At, string Text) : ReadProblem;

    /// <summary>The checksum holds but the line cannot be read: kept as written.</summary>
    public sealed record Unreadable(int At, string Problem) : ReadProblem;
}

/// <summary>Lines read from a plan file's body or a live message, and what was wrong.</summary>
internal sealed record LinesRead(ValueList<FileLine> Lines, ValueList<ReadProblem> Problems);

/// <summary>A plan file: a header with the schema, then change lines in the order they were appended.</summary>
internal sealed record PlanFile(int Schema, ValueList<FileLine> Lines)
{
    public ValueList<Field> Extra { get; init; } = ValueList<Field>.Empty;
}

/// <summary>A plan file as read, and what the reader dropped or could not read.</summary>
internal sealed record PlanFileRead(PlanFile File, ValueList<ReadProblem> Problems);

/// <summary>What a joining client gets: the board revision, the plans and the current ghosts.</summary>
internal sealed record JoinSnapshot(Revision Revision, ValueList<PlanRecord> Plans, ValueList<GhostRecord> Ghosts)
{
    public ValueList<Field> Extra { get; init; } = ValueList<Field>.Empty;

    /// <summary>Records of a kind this version does not know, kept and written after the ghosts.</summary>
    public ValueList<Entry> Others { get; init; } = ValueList<Entry>.Empty;
}

/// <summary>A template: its name and ghosts in local coordinates.</summary>
internal sealed record WrittenTemplate(TemplateName Name, ValueList<DraftRecord> Ghosts)
{
    public ValueList<Field> Extra { get; init; } = ValueList<Field>.Empty;
}

/// <summary>The clipboard: ghosts in local coordinates.</summary>
internal sealed record WrittenClipboard(ValueList<DraftRecord> Ghosts)
{
    public ValueList<Field> Extra { get; init; } = ValueList<Field>.Empty;
}

/// <summary>
/// Plan files and live messages. A plan file is <c>lattice-plan &lt;schema&gt;</c> then one change line per line,
/// appended, every line ending in a line break; a live message is the same lines without the header. JSON forms carry
/// the same records without checksums.
/// </summary>
internal static class PlanFileForm
{
    public static string Header(int schema, ValueList<Field> extra) => Headers.Text(WrittenForms.PlanFile, schema, extra) + "\n";

    /// <summary>The text appended for one line.</summary>
    public static string Append(ChangeLine line) => ChangeLineForm.Write(line) + "\n";

    public static string Write(PlanFile file)
    {
        var text = new StringBuilder(Header(file.Schema, file.Extra), 64 + (file.Lines.Count * 160));
        LineBlock.Write(text, file.Lines);
        return text.ToString();
    }

    public static Result<PlanFileRead, Reason> Read(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length < 2)
            return Result<PlanFileRead, Reason>.Error(WrittenForms.Problem("no header"));
        return Headers.Read(lines[0], WrittenForms.PlanFile).Map(header =>
        {
            var body = LineBlock.Read(lines, 1);
            return new PlanFileRead(new PlanFile(header.Schema, body.Lines) { Extra = header.Extra }, body.Problems);
        });
    }

    public static string WriteJson(PlanFile file) =>
        JsonForm.Write(new Document(Headers.Entry(WrittenForms.PlanFile, file.Schema, file.Extra), LineBlock.Entries(file.Lines)));

    public static Result<PlanFile, Reason> ReadJson(string text) =>
        Headers.ReadJson(text, WrittenForms.PlanFile).Bind(static read =>
            LineBlock.FromEntries(read.Document.Items).Map(lines => new PlanFile(read.Header.Schema, lines) { Extra = read.Header.Extra }));
}

/// <summary>Live messages: the new change lines only, the same text the plan files append.</summary>
internal static class LiveMessageForm
{
    public static string Write(ValueList<ChangeLine> lines)
    {
        var text = new StringBuilder(lines.Count * 160);
        foreach (var line in lines)
            text.Append(PlanFileForm.Append(line));
        return text.ToString();
    }

    public static LinesRead Read(string text) => LineBlock.Read(text.Split('\n'), 0);

    public static string WriteJson(ValueList<ChangeLine> lines)
    {
        var entries = new List<Entry>(lines.Count);
        foreach (var line in lines)
            entries.Add(ChangeLineForm.ToEntry(line));
        return JsonForm.Write(new Document(Headers.Entry(WrittenForms.Live, WrittenForms.Schema, ValueList<Field>.Empty), ValueList.From(entries)));
    }

    public static Result<ValueList<FileLine>, Reason> ReadJson(string text) =>
        Headers.ReadJson(text, WrittenForms.Live).Bind(static read => LineBlock.FromEntries(read.Document.Items));
}

/// <summary>The join snapshot: <c>lattice-join &lt;schema&gt; rev=</c>, then one plan per line, then one ghost per line.</summary>
internal static class JoinForm
{
    public static Document ToDocument(JoinSnapshot join)
    {
        var header = new FieldList().Add("rev", join.Revision.Value).AddAll(join.Extra).ToList();
        var items = new List<Entry>(join.Plans.Count + join.Ghosts.Count + join.Others.Count);
        foreach (var plan in join.Plans)
            items.Add(Entry.Of(PlanForm.Write(plan, LineContext.None)));
        foreach (var ghost in join.Ghosts)
            items.Add(Entry.Of(GhostForm.Write(ghost, LineContext.None)));
        foreach (var other in join.Others)
            items.Add(other);
        return new Document(Headers.Entry(WrittenForms.Join, WrittenForms.Schema, header), ValueList.From(items));
    }

    public static Result<JoinSnapshot, Reason> FromDocument(HeaderRead header, ValueList<Entry> items)
    {
        var read = new FieldReader(header.Extra);
        if (!read.Need("rev").Bind(WrittenValues.Long).TryGet(out var revision))
            return Result<JoinSnapshot, Reason>.Error(WrittenForms.Problem("missing rev"));
        var plans = new List<PlanRecord>();
        var ghosts = new List<GhostRecord>();
        var others = new List<Entry>();
        foreach (var item in items)
        {
            var first = item.Fields.Count > 0 && item.Groups.Count == 0 ? item.Fields[0].Key : "";
            if (first == "plan")
                plans.Add(PlanForm.Read(item.Fields, LineContext.None));
            else if (first == "ghost")
                ghosts.Add(GhostForm.Read(item.Fields, LineContext.None));
            else
                others.Add(item);
        }

        return Result<JoinSnapshot, Reason>.Ok(new JoinSnapshot(new Revision(revision), ValueList.From(plans), ValueList.From(ghosts))
        {
            Extra = read.Rest(),
            Others = ValueList.From(others),
        });
    }

    public static string Write(JoinSnapshot join) => Documents.Text(ToDocument(join));

    public static Result<(JoinSnapshot Join, ValueList<ReadProblem> Problems), Reason> Read(string text) =>
        Documents.ReadText(text, WrittenForms.Join).Bind(static read =>
            FromDocument(read.Header, read.Items).Map(join => (join, read.Problems)));

    public static string WriteJson(JoinSnapshot join) => JsonForm.Write(ToDocument(join));

    public static Result<JoinSnapshot, Reason> ReadJson(string text) =>
        Headers.ReadJson(text, WrittenForms.Join).Bind(static read => FromDocument(read.Header, read.Document.Items));
}

/// <summary>Templates (<c>lattice-template &lt;schema&gt; name=</c>) and the clipboard (<c>lattice-clipboard &lt;schema&gt;</c>): one draft per line.</summary>
internal static class TemplateForm
{
    public static Document ToDocument(WrittenTemplate template) =>
        new(
            Headers.Entry(WrittenForms.Template, WrittenForms.Schema, new FieldList().Add("name", template.Name.Value).AddAll(template.Extra).ToList()),
            Drafts(template.Ghosts));

    public static Document ToDocument(WrittenClipboard clipboard) =>
        new(Headers.Entry(WrittenForms.Clipboard, WrittenForms.Schema, clipboard.Extra), Drafts(clipboard.Ghosts));

    public static Result<WrittenTemplate, Reason> TemplateFrom(HeaderRead header, ValueList<Entry> items)
    {
        var read = new FieldReader(header.Extra);
        if (!read.Need("name").Bind(static t => WrittenValues.Checked(TemplateName.Create(t), "name", t)).TryGet(out var name))
            return Result<WrittenTemplate, Reason>.Error(WrittenForms.Problem("missing or bad name"));
        return Result<WrittenTemplate, Reason>.Ok(new WrittenTemplate(name, Drafts(items)) { Extra = read.Rest() });
    }

    public static WrittenClipboard ClipboardFrom(HeaderRead header, ValueList<Entry> items) =>
        new(Drafts(items)) { Extra = header.Extra };

    public static string Write(WrittenTemplate template) => Documents.Text(ToDocument(template));

    public static string Write(WrittenClipboard clipboard) => Documents.Text(ToDocument(clipboard));

    public static Result<(WrittenTemplate Template, ValueList<ReadProblem> Problems), Reason> ReadTemplate(string text) =>
        Documents.ReadText(text, WrittenForms.Template).Bind(static read =>
            TemplateFrom(read.Header, read.Items).Map(template => (template, read.Problems)));

    public static Result<(WrittenClipboard Clipboard, ValueList<ReadProblem> Problems), Reason> ReadClipboard(string text) =>
        Documents.ReadText(text, WrittenForms.Clipboard).Map(static read => (ClipboardFrom(read.Header, read.Items), read.Problems));

    public static string WriteJson(WrittenTemplate template) => JsonForm.Write(ToDocument(template));

    public static string WriteJson(WrittenClipboard clipboard) => JsonForm.Write(ToDocument(clipboard));

    public static Result<WrittenTemplate, Reason> ReadTemplateJson(string text) =>
        Headers.ReadJson(text, WrittenForms.Template).Bind(static read => TemplateFrom(read.Header, read.Document.Items));

    public static Result<WrittenClipboard, Reason> ReadClipboardJson(string text) =>
        Headers.ReadJson(text, WrittenForms.Clipboard).Map(static read => ClipboardFrom(read.Header, read.Document.Items));

    /// <summary>The template as the contract's type; refused while any draft is one this version cannot read.</summary>
    public static Result<Template, Reason> ToTemplate(WrittenTemplate template)
    {
        var drafts = new List<GhostDraft>(template.Ghosts.Count);
        foreach (var record in template.Ghosts)
        {
            if (record is not DraftRecord.Known known)
                return Result<Template, Reason>.Error(Reason.Of(ReasonCodes.UnknownKind, WrittenForms.Rule, ValueMap.Of("template", template.Name.Value)));
            drafts.Add(known.Draft);
        }

        return Result<Template, Reason>.Ok(new Template(template.Name, ValueList.From(drafts)));
    }

    private static ValueList<Entry> Drafts(ValueList<DraftRecord> drafts)
    {
        var items = new List<Entry>(drafts.Count);
        foreach (var draft in drafts)
            items.Add(Entry.Of(DraftForm.Write(draft)));
        return ValueList.From(items);
    }

    private static ValueList<DraftRecord> Drafts(ValueList<Entry> items)
    {
        var drafts = new List<DraftRecord>(items.Count);
        foreach (var item in items)
            drafts.Add(DraftForm.Read(item.Fields));
        return ValueList.From(drafts);
    }
}

/// <summary>A document header as read: the schema and the fields after it.</summary>
internal sealed record HeaderRead(int Schema, ValueList<Field> Extra);

/// <summary>Document headers: <c>&lt;form&gt; &lt;schema&gt;</c> then the document's own fields.</summary>
internal static class Headers
{
    /// <summary>A JSON document as read, with its header.</summary>
    internal sealed record JsonRead(HeaderRead Header, Document Document);

    public static Entry Entry(string form, int schema, ValueList<Field> fields) =>
        Written.Entry.Of(new FieldList().Add("form", form).Add("schema", schema).AddAll(fields).ToList());

    public static string Text(string form, int schema, ValueList<Field> fields) => TextForm.Write(Entry(form, schema, fields), 2);

    public static Result<HeaderRead, Reason> Read(string line, string form) =>
        TextForm.Read(line, ["form", "schema"])
            .MapError(static p => WrittenForms.Problem(p))
            .Bind(entry => FromEntry(entry, form));

    public static Result<JsonRead, Reason> ReadJson(string text, string form) =>
        JsonForm.ReadDocument(text)
            .MapError(static p => WrittenForms.Problem(p))
            .Bind(document => FromEntry(document.Header, form).Map(header => new JsonRead(header, document)));

    private static Result<HeaderRead, Reason> FromEntry(Entry entry, string form)
    {
        var read = new FieldReader(entry.Fields);
        if (!read.Take("form").TryGet(out var written) || written != form)
            return Result<HeaderRead, Reason>.Error(WrittenForms.Problem($"not a {form} document"));
        if (!read.Need("schema").Bind(WrittenValues.Int).TryGet(out var schema) || schema < 1)
            return Result<HeaderRead, Reason>.Error(WrittenForms.Problem("bad schema"));
        if (entry.Groups.Count > 0)
            return Result<HeaderRead, Reason>.Error(WrittenForms.Problem("bad header"));
        return Result<HeaderRead, Reason>.Ok(new HeaderRead(schema, read.Rest()));
    }
}

/// <summary>Text documents of plain records: a header line, then one record per line.</summary>
internal static class Documents
{
    /// <summary>A text document as read.</summary>
    internal sealed record TextRead(HeaderRead Header, ValueList<Entry> Items, ValueList<ReadProblem> Problems);

    public static string Text(Document document)
    {
        var text = new StringBuilder(64 + (document.Items.Count * 120));
        TextForm.Write(text, document.Header, 2);
        text.Append('\n');
        foreach (var item in document.Items)
        {
            TextForm.Write(text, item, 0);
            text.Append('\n');
        }

        return text.ToString();
    }

    public static Result<TextRead, Reason> ReadText(string text, string form)
    {
        var lines = text.Split('\n');
        if (lines.Length < 2)
            return Result<TextRead, Reason>.Error(WrittenForms.Problem("no header"));
        return Headers.Read(lines[0], form).Bind(header =>
        {
            var items = new List<Entry>(lines.Length);
            var problems = new List<ReadProblem>();
            for (var i = 1; i < lines.Length; i++)
            {
                var last = i == lines.Length - 1;
                if (last && lines[i].Length == 0)
                    break;
                if (last)
                {
                    problems.Add(new ReadProblem.TornLastLine(i + 1, lines[i]));
                    break;
                }

                if (!TextForm.Read(lines[i], []).TryGet(out var entry))
                    return Result<TextRead, Reason>.Error(WrittenForms.Problem($"bad line {i + 1}"));
                items.Add(entry);
            }

            return Result<TextRead, Reason>.Ok(new TextRead(header, ValueList.From(items), ValueList.From(problems)));
        });
    }
}

/// <summary>Blocks of change lines: a plan file's body and a live message.</summary>
internal static class LineBlock
{
    private const string KeptKey = "kept";

    public static void Write(StringBuilder text, ValueList<FileLine> lines)
    {
        foreach (var line in lines)
        {
            switch (line)
            {
                case FileLine.Read read:
                    text.Append(ChangeLineForm.Write(read.Line));
                    break;
                case FileLine.Kept kept:
                    text.Append(kept.Text);
                    break;
            }

            text.Append('\n');
        }
    }

    /// <summary>Reads lines from <paramref name="start"/>; text lines are numbered from 1.</summary>
    public static LinesRead Read(string[] lines, int start)
    {
        var read = new List<FileLine>(lines.Length);
        var problems = new List<ReadProblem>();
        for (var i = start; i < lines.Length; i++)
        {
            var text = lines[i];
            var last = i == lines.Length - 1;
            if (last && text.Length == 0)
                break;
            if (last)
            {
                problems.Add(new ReadProblem.TornLastLine(i + 1, text));
                break;
            }

            var result = ChangeLineForm.Read(text);
            if (result.TryGet(out var line))
            {
                read.Add(new FileLine.Read(line));
                continue;
            }

            result.TryGetError(out var problem);
            switch (problem)
            {
                case LineProblem.Unreadable unreadable:
                    read.Add(new FileLine.Kept(text, unreadable.Problem));
                    problems.Add(new ReadProblem.Unreadable(i + 1, unreadable.Problem));
                    break;
                default:
                    problems.Add(new ReadProblem.BadChecksum(i + 1, text));
                    break;
            }
        }

        return new LinesRead(ValueList.From(read), ValueList.From(problems));
    }

    public static ValueList<Entry> Entries(ValueList<FileLine> lines)
    {
        var entries = new List<Entry>(lines.Count);
        foreach (var line in lines)
            entries.Add(line switch
            {
                FileLine.Read read => ChangeLineForm.ToEntry(read.Line),
                FileLine.Kept kept => Entry.Of(ValueList.Of(new Field(KeptKey, kept.Text))),
                _ => Entry.Of(ValueList<Field>.Empty),
            });
        return ValueList.From(entries);
    }

    public static Result<ValueList<FileLine>, Reason> FromEntries(ValueList<Entry> entries)
    {
        var lines = new List<FileLine>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Fields.Count == 1 && entry.Groups.Count == 0 && entry.Fields[0].Key == KeptKey)
            {
                lines.Add(new FileLine.Kept(entry.Fields[0].Value, "kept"));
                continue;
            }

            if (!ChangeLineForm.FromEntry(entry).TryGet(out var line))
                return Result<ValueList<FileLine>, Reason>.Error(WrittenForms.Problem($"bad line {lines.Count + 1}"));
            lines.Add(new FileLine.Read(line));
        }

        return Result<ValueList<FileLine>, Reason>.Ok(ValueList.From(lines));
    }
}
