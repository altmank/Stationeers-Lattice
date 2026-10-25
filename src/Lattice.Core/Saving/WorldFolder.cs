using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Lattice.Api;
using Lattice.Core.Written;

namespace Lattice.Core.Saving;

/// <summary>A game save file: its full path and a fingerprint of what the game wrote into it.</summary>
internal readonly record struct SaveKey(string Path, string Fingerprint);

/// <summary>What a save remembers: the save, and the line each plan was at (its head). Plans not listed did not exist.</summary>
internal sealed record SavePoint(SaveKey Save, DateTimeOffset At, ValueMap<PlanId, long> Heads);

/// <summary>
/// A finished plan in the index: what the board lists (<see cref="FinishedPlan"/>), the line that finished it, and the
/// plan's record at that line (what the access policy judges; none in an index written before it kept them).
/// </summary>
internal sealed record FinishedEntry(FinishedPlan Plan, long Line)
{
    public Option<Plan> Record { get; init; }
}

/// <summary>
/// Lattice's own folder for one world, never inside a game save:
/// <c>plans/&lt;id&gt;.lplan</c> the plan files, <c>archive/</c> finished plans' files, <c>checkpoints/&lt;id&gt;/&lt;line&gt;.lcheck</c>
/// a plan's ghosts at a line, <c>saves/&lt;key&gt;.lsave</c> one record per game save file, <c>finished.index</c> the finished
/// plans. Lines are only appended and flushed at once; a torn tail is moved to <c>&lt;id&gt;.torn</c> before the next
/// append, so nothing is ever deleted. Files are read and written on the calling thread.
/// </summary>
internal sealed class WorldFolder
{
    public const string PlanExtension = ".lplan";
    private const string CheckpointForm = "lattice-checkpoint";
    private const string SaveForm = "lattice-save";
    private const string IndexForm = "lattice-finished";

    public WorldFolder(string root)
    {
        Root = root;
        Plans = Path.Combine(root, "plans");
        Archive = Path.Combine(root, "archive");
        Checkpoints = Path.Combine(root, "checkpoints");
        Saves = Path.Combine(root, "saves");
        IndexPath = Path.Combine(root, "finished.index");
    }

    public string Root { get; }

    public string Plans { get; }

    public string Archive { get; }

    public string Checkpoints { get; }

    public string Saves { get; }

    public string IndexPath { get; }

    public string PlanPath(PlanId plan) => Path.Combine(Plans, Name(plan) + PlanExtension);

    public string ArchivedPath(PlanId plan) => Path.Combine(Archive, Name(plan) + PlanExtension);

    /// <summary>Every plan with a file, active or archived.</summary>
    public List<(PlanId Plan, string Path, bool Archived)> PlanFiles()
    {
        var found = new List<(PlanId, string, bool)>();
        Collect(Plans, false, found);
        Collect(Archive, true, found);
        return found;
    }

    public string ReadText(string path) => File.ReadAllText(path, Encoding.UTF8);

    private readonly HashSet<PlanId> _tailChecked = new();

    /// <summary>A world was loaded: every plan's tail is checked again before its next append.</summary>
    public void NewSession() => _tailChecked.Clear();

    /// <summary>
    /// Appends lines to a plan's file, creating it with its header; a file in the archive moves back first. A tail with
    /// no line break (a torn line) is moved to the plan's <c>.torn</c> file the first time a plan is appended to.
    /// Flushed to the operating system before it returns, so a killed game loses nothing written.
    /// </summary>
    public void Append(PlanId plan, string lines)
    {
        Directory.CreateDirectory(Plans);
        var path = PlanPath(plan);
        var archived = ArchivedPath(plan);
        if (!File.Exists(path) && File.Exists(archived))
            File.Move(archived, path);
        if (!File.Exists(path))
            File.WriteAllText(path, PlanFileForm.Header(WrittenForms.Schema, ValueList<Field>.Empty), Utf8);
        else if (_tailChecked.Add(plan))
            MoveTornTail(plan, path);
        _tailChecked.Add(plan);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = Utf8.GetBytes(lines);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    /// <summary>Moves a finished plan's file to the archive.</summary>
    public void ToArchive(PlanId plan)
    {
        var path = PlanPath(plan);
        if (!File.Exists(path))
            return;
        Directory.CreateDirectory(Archive);
        File.Move(path, ArchivedPath(plan));
    }

    /// <summary>Moves a reopened plan's file back from the archive; nothing when it is not archived or already back.</summary>
    public void FromArchive(PlanId plan)
    {
        var archived = ArchivedPath(plan);
        var path = PlanPath(plan);
        if (!File.Exists(archived) || File.Exists(path))
            return;
        Directory.CreateDirectory(Plans);
        File.Move(archived, path);
    }

    /// <summary>Writes a plan's state at a line: its plan record, its ghosts, whether it was finished (the join form).</summary>
    public void WriteCheckpoint(PlanReplay state)
    {
        var write = BeginCheckpoint(state);
        while (!write.Step(int.MaxValue))
        {
        }
    }

    /// <summary>
    /// Starts writing a plan's state at a line in parts (<see cref="CheckpointWrite"/>); the file is what
    /// <see cref="WriteCheckpoint"/> writes, and it takes its name only when the last part is in.
    /// </summary>
    public CheckpointWrite BeginCheckpoint(PlanReplay state)
    {
        var plans = state.Record.TryGet(out var plan) ? ValueList.Of(PlanRecord.Of(plan)) : ValueList<PlanRecord>.Empty;
        var extra = new FieldList()
            .Add("checkpoint", CheckpointForm)
            .Add("plan", state.Plan.Value)
            .Add("line", state.At)
            .Add("finished", state.Finished ? "yes" : "no")
            .Add("built", state.Built)
            .AddAll(Box(state.Bounds))
            .Add("done", state.Done)
            .ToList();
        var ghosts = new List<Ghost>(state.Ghosts);
        return new CheckpointWrite(Path.Combine(Checkpoints, Name(state.Plan), Number(state.At) + ".lcheck"), new JoinSnapshot(default, plans, ValueList<GhostRecord>.Empty) { Extra = extra }, ghosts);
    }

    /// <summary>
    /// A checkpoint written a few ghosts a step: each step formats up to its units of ghosts (the header and plan
    /// record with the first) and appends them to the file's <c>.new</c> twin; the last step moves the twin over the
    /// file. A world that stops in between leaves only the twin, which no load reads. Linear in the ghosts formatted.
    /// </summary>
    internal sealed class CheckpointWrite(string path, JoinSnapshot head, List<Ghost> ghosts)
    {
        private readonly StringBuilder _text = new(1024);
        private int _next = -1;

        /// <summary>Formats and appends up to <paramref name="units"/> ghosts; whether the checkpoint is complete.</summary>
        public bool Step(int units)
        {
            var temp = path + ".new";
            var mode = FileMode.Append;
            if (_next < 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var start = JoinForm.ToDocument(head);
                TextForm.Write(_text, start.Header, 2);
                _text.Append('\n');
                foreach (var item in start.Items)
                {
                    TextForm.Write(_text, item, 0);
                    _text.Append('\n');
                }

                mode = FileMode.Create;
                _next = 0;
            }

            for (var did = 0; did < units && _next < ghosts.Count; did++)
            {
                TextForm.Write(_text, Entry.Of(GhostForm.Write(GhostRecord.Of(ghosts[_next++]), LineContext.None)), 0);
                _text.Append('\n');
            }

            using (var stream = new FileStream(temp, mode, FileAccess.Write, FileShare.Read))
            {
                var bytes = Utf8.GetBytes(_text.ToString());
                stream.Write(bytes, 0, bytes.Length);
            }

            _text.Clear();
            if (_next < ghosts.Count)
                return false;
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
            return true;
        }
    }

    /// <summary>The lines of a plan that have a checkpoint.</summary>
    public HashSet<long> CheckpointLines(PlanId plan)
    {
        var lines = new HashSet<long>();
        var dir = Path.Combine(Checkpoints, Name(plan));
        if (!Directory.Exists(dir))
            return lines;
        foreach (var file in Directory.GetFiles(dir, "*.lcheck"))
            if (long.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
                lines.Add(line);
        return lines;
    }

    /// <summary>A plan's state at a checkpoint line, or none when it cannot be read.</summary>
    public Option<PlanReplay> ReadCheckpoint(PlanId plan, long line)
    {
        var path = Path.Combine(Checkpoints, Name(plan), Number(line) + ".lcheck");
        if (!File.Exists(path) || !JoinForm.Read(ReadText(path)).TryGet(out var read))
            return Option<PlanReplay>.None;
        var extra = new FieldReader(read.Join.Extra);
        var finished = extra.Take("finished").Reduce("no") == "yes";
        var built = extra.Take("built").Bind(static b => WrittenValues.Int(b).Match(Option.Some, static _ => Option<int>.None)).Reduce(0);
        var bounds = extra.Take("min").Bind(static c => WrittenValues.Cell(c).Match(Option.Some, static _ => Option<Cell>.None)).TryGet(out var min)
                     && extra.Take("max").Bind(static c => WrittenValues.Cell(c).Match(Option.Some, static _ => Option<Cell>.None)).TryGet(out var max)
            ? Option.Some((min, max))
            : Option<(Cell, Cell)>.None;
        var done = extra.Take("done").Bind(static d => WrittenValues.Int(d).Match(Option.Some, static _ => Option<int>.None));
        var record = Option<Plan>.None;
        foreach (var planRecord in read.Join.Plans)
            if (planRecord is PlanRecord.Known known)
                record = Option.Some(known.Plan);
        var ghosts = new List<Ghost>(read.Join.Ghosts.Count);
        foreach (var ghost in read.Join.Ghosts)
            if (ghost is GhostRecord.Known known)
                ghosts.Add(known.Ghost);
        return Option.Some(PlanReplay.From(plan, line, record, finished, ghosts, built, bounds, done.TryGet(out var count) ? count : null));
    }

    /// <summary>Writes the record of a save, replacing any earlier record of the same save file.</summary>
    public void WriteRecord(SavePoint record)
    {
        Directory.CreateDirectory(Saves);
        var header = new FieldList()
            .Add("save", record.Save.Path)
            .Add("fingerprint", record.Save.Fingerprint)
            .Add("at", WrittenValues.Time(record.At))
            .ToList();
        var items = new List<Entry>(record.Heads.Count);
        foreach (var (plan, head) in record.Heads)
            items.Add(Entry.Of(new FieldList().Add("plan", plan.Value).Add("head", head).ToList()));
        var document = new Document(Headers.Entry(SaveForm, 1, header), ValueList.From(items));
        WriteWhole(Path.Combine(Saves, PathKey(record.Save.Path) + ".lsave"), Documents.Text(document));
    }

    /// <summary>Every save record in the folder that can be read.</summary>
    public List<SavePoint> Records()
    {
        var records = new List<SavePoint>();
        if (!Directory.Exists(Saves))
            return records;
        foreach (var file in Directory.GetFiles(Saves, "*.lsave"))
            if (Documents.ReadText(ReadText(file), SaveForm).TryGet(out var read) && Record(read).TryGet(out var record))
                records.Add(record);
        return records;
    }

    /// <summary>Adds a finished plan to the index.</summary>
    public void AddFinished(FinishedEntry entry)
    {
        var index = Finished();
        index.RemoveAll(p => p.Plan.Id == entry.Plan.Id);
        index.Add(entry);
        WriteIndex(index);
    }

    /// <summary>Writes the finished plans index anew with these entries.</summary>
    public void ReplaceFinished(List<FinishedEntry> index) => WriteIndex(index);

    /// <summary>Drops a plan's entry from the finished plans index (it is active again).</summary>
    public void RemoveFinished(PlanId plan)
    {
        var index = Finished();
        if (index.RemoveAll(p => p.Plan.Id == plan) > 0)
            WriteIndex(index);
    }

    private void WriteIndex(List<FinishedEntry> index)
    {
        var items = new List<Entry>(index.Count);
        foreach (var each in index)
        {
            var plan = each.Plan;
            items.Add(new Entry(new FieldList()
                .Add("plan", plan.Id.Value)
                .Add("name", plan.Name.Value)
                .Add("owner", WrittenValues.Actor(plan.Owner))
                .Add("finished-at", WrittenValues.Time(plan.FinishedAt))
                .Add("pieces", plan.Pieces)
                .Add("min", WrittenValues.Cell(plan.Min))
                .Add("max", WrittenValues.Cell(plan.Max))
                .Add("line", each.Line)
                .ToList(),
                each.Record.TryGet(out var record)
                    ? ValueList.Of(new Group("record", PlanForm.Write(record, ValueList<Field>.Empty, LineContext.None)))
                    : ValueList<Group>.Empty));
        }

        WriteWhole(IndexPath, Documents.Text(new Document(Headers.Entry(IndexForm, 1, ValueList<Field>.Empty), ValueList.From(items))));
    }

    /// <summary>The finished plans index; empty when there is none.</summary>
    public List<FinishedEntry> Finished()
    {
        var index = new List<FinishedEntry>();
        if (!File.Exists(IndexPath) || !Documents.ReadText(ReadText(IndexPath), IndexForm).TryGet(out var read))
            return index;
        foreach (var item in read.Items)
        {
            var f = new FieldReader(item.Fields);
            if (f.Need("plan").Bind(WrittenValues.Long).TryGet(out var plan)
                && f.Need("name").Bind(static n => PlanName.Create(n).MapError(static r => r.Code.Value)).TryGet(out var name)
                && f.Need("owner").Bind(WrittenValues.Actor).TryGet(out var owner)
                && f.Need("finished-at").Bind(WrittenValues.Time).TryGet(out var at)
                && f.Need("pieces").Bind(WrittenValues.Int).TryGet(out var pieces)
                && f.Need("min").Bind(WrittenValues.Cell).TryGet(out var min)
                && f.Need("max").Bind(WrittenValues.Cell).TryGet(out var max)
                && f.Need("line").Bind(WrittenValues.Long).TryGet(out var line))
                index.Add(new FinishedEntry(new FinishedPlan(new PlanId(plan), name, owner, at, pieces, min, max), line) { Record = RecordOf(item) });
        }

        return index;
    }

    // The plan record an index item keeps under its "record" group; none when it has none or it does not read.
    private static Option<Plan> RecordOf(Entry item)
    {
        foreach (var group in item.Groups)
            if (group.Word == "record" && PlanForm.Read(group.Fields, LineContext.None) is PlanRecord.Known known)
                return Option.Some(known.Plan);
        return Option<Plan>.None;
    }

    /// <summary>The file name of a save's record: a hash of its full path, case folded.</summary>
    internal static string PathKey(string path)
    {
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Utf8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        var text = new StringBuilder(32);
        for (var i = 0; i < 12; i++)
            text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return text.ToString();
    }

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static string Name(PlanId plan) => Number(plan.Value);

    private static ValueList<Field> Box(Option<(Cell Min, Cell Max)> bounds) =>
        bounds.TryGet(out var box)
            ? new FieldList().Add("min", WrittenValues.Cell(box.Min)).Add("max", WrittenValues.Cell(box.Max)).ToList()
            : ValueList<Field>.Empty;

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Collect(string dir, bool archived, List<(PlanId, string, bool)> found)
    {
        if (!Directory.Exists(dir))
            return;
        foreach (var file in Directory.GetFiles(dir, "*" + PlanExtension))
            if (long.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                found.Add((new PlanId(id), file, archived));
    }

    // A whole small file: written beside, then moved over the old one, so a crash leaves the old or the new.
    private static void WriteWhole(string path, string text)
    {
        var temp = path + ".new";
        File.WriteAllText(temp, text, Utf8);
        if (File.Exists(path))
            File.Delete(path);
        File.Move(temp, path);
    }

    private void MoveTornTail(PlanId plan, string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (stream.Length == 0)
            return;
        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() == '\n')
            return;
        var bytes = new byte[stream.Length];
        stream.Seek(0, SeekOrigin.Begin);
        var read = 0;
        while (read < bytes.Length)
            read += stream.Read(bytes, read, bytes.Length - read);
        var cut = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        var torn = new byte[bytes.Length - cut];
        Array.Copy(bytes, cut, torn, 0, torn.Length);
        using (var tornFile = new FileStream(Path.Combine(Plans, Name(plan) + ".torn"), FileMode.Append, FileAccess.Write))
        {
            tornFile.Write(torn, 0, torn.Length);
            tornFile.WriteByte((byte)'\n');
        }

        stream.SetLength(cut);
        stream.Flush();
    }

    private static Option<SavePoint> Record(Documents.TextRead read)
    {
        var header = new FieldReader(read.Header.Extra);
        if (!header.Take("save").TryGet(out var path) || !header.Take("fingerprint").TryGet(out var fingerprint))
            return Option<SavePoint>.None;
        var at = header.Take("at").Match(t => WrittenValues.Time(t).Match(static v => v, static _ => default), static () => default(DateTimeOffset));
        var heads = new Dictionary<PlanId, long>(read.Items.Count);
        foreach (var item in read.Items)
        {
            var fields = new FieldReader(item.Fields);
            if (fields.Need("plan").Bind(WrittenValues.Long).TryGet(out var plan) && fields.Need("head").Bind(WrittenValues.Long).TryGet(out var head))
                heads[new PlanId(plan)] = head;
        }

        return Option.Some(new SavePoint(new SaveKey(path, fingerprint), at, ValueMap<PlanId, long>.From(heads)));
    }
}
