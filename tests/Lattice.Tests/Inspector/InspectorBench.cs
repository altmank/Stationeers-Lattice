using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using Lattice.Core.Inspector;
using Lattice.Core.Ports;
using Lattice.Core.Written;
using Lattice.Tests.Board;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;

namespace Lattice.Tests.Inspector;

/// <summary>
/// Plan files kept in memory the way Lattice keeps them on disk: each change set becomes one line per plan it
/// touches (<see cref="ChangeLines"/>), following the plan's current line, so moving a plan back to an older line makes
/// the next line branch. <see cref="Read"/> goes through the plan file's text form.
/// </summary>
internal sealed class FakePlanFiles : IPlanFiles
{
    private readonly SortedDictionary<long, List<FileLine>> _files = new();
    private readonly Dictionary<PlanId, long> _heads = new();
    private readonly List<(SaveName Save, Dictionary<PlanId, long> Heads)> _saves = new();

    public ValueList<PlanId> Plans
    {
        get
        {
            var plans = new List<PlanId>(_files.Count);
            foreach (var plan in _files.Keys)
                plans.Add(new PlanId(plan));
            return ValueList.From(plans);
        }
    }

    public void Record(ChangeSet set)
    {
        foreach (var part in ChangeLines.Of(set))
        {
            var lines = File(part.Plan);
            var number = lines.Count + 1;
            var follows = _heads.TryGetValue(part.Plan, out var head) ? Option.Some(new LineNumber(head)) : Option<LineNumber>.None;
            lines.Add(new FileLine.Read(ChangeLine.For(new LineNumber(number), follows, set, part.Ops)));
            _heads[part.Plan] = number;
        }
    }

    /// <summary>A plan with a file but no lines yet.</summary>
    public void Open(PlanId plan) => File(plan);

    /// <summary>The plan is loaded at an earlier line, as after loading an older save: its next line branches.</summary>
    public void Rewind(PlanId plan, long line) => _heads[plan] = line;

    /// <summary>The plan is not open (hidden by an older save, or finished).</summary>
    public void Close(PlanId plan) => _heads.Remove(plan);

    /// <summary>A game save remembering every open plan's line.</summary>
    public void Save(string name) => _saves.Add((TestKitIds.Must(SaveName.Create(name)), new Dictionary<PlanId, long>(_heads)));

    /// <summary>A line whose checksum holds but which this version cannot read; it takes the next line number.</summary>
    public string Keep(PlanId plan)
    {
        var lines = File(plan);
        var content = $"{lines.Count + 1} {lines.Count} ? from-a-newer-version";
        var text = content + " #" + LineChecksum.Of(content);
        lines.Add(new FileLine.Kept(text, "newer"));
        return text;
    }

    public string Text(PlanId plan) => PlanFileForm.Write(new PlanFile(WrittenForms.Schema, ValueList.From(File(plan))));

    public Result<PlanFileRead, Reason> Read(PlanId plan) =>
        _files.ContainsKey(plan.Value)
            ? PlanFileForm.Read(Text(plan))
            : Result<PlanFileRead, Reason>.Error(Reason.Of(ReasonCodes.NotFound, TestKitIds.Catalogue));

    public Option<LineNumber> Head(PlanId plan) =>
        _heads.TryGetValue(plan, out var head) && head > 0 ? Option.Some(new LineNumber(head)) : Option<LineNumber>.None;

    public ValueList<SaveAt> Saves(PlanId plan)
    {
        var saves = new List<SaveAt>();
        foreach (var (save, heads) in _saves)
            if (heads.TryGetValue(plan, out var line))
                saves.Add(new SaveAt(save, new LineNumber(line)));
        return ValueList.From(saves);
    }

    private List<FileLine> File(PlanId plan)
    {
        if (!_files.TryGetValue(plan.Value, out var lines))
            _files.Add(plan.Value, lines = new List<FileLine>());
        return lines;
    }
}

/// <summary>A board whose every applied command is recorded in <see cref="FakePlanFiles"/>, and the inspector over them.</summary>
internal sealed class InspectorBench
{
    private int _nextCell;

    public InspectorBench(params NamedPiece[] extraPieces)
    {
        T = new TestBoard(default, extraPieces);
        Files.Open(Loose);
        Inspector = new PlanInspector(Files);
    }

    public TestBoard T { get; }

    public FakePlanFiles Files { get; } = new();

    public PlanInspector Inspector { get; }

    public static PlanId Loose => new(1);

    public ChangeSet Apply(Command command, Actor? actor = null)
    {
        var set = TestBoard.Applied(T.Apply(command, actor));
        Files.Record(set);
        return set;
    }

    public GhostId Place(string piece = "StructureFrame", Actor? actor = null, Option<PlanId> plan = default) =>
        Apply(new Command.Place(ValueList.Of(T.Draft(piece, new Cell(10 + 2 * _nextCell++, 10, 10), plan: plan))), actor).Added[0].Id;

    public ChangeSet Relabel(GhostId ghost, string label, Actor? actor = null) =>
        Apply(new Command.Change(TestBoard.Ids(ghost), new GhostEdit.SetLabel(Option.Some(TestKitIds.Must(Label.Create(label)))), Option<Revision>.None), actor);

    public PlanId CreatePlan(string name) =>
        Apply(new Command.CreatePlan(new PlanDraft(TestKitIds.Must(PlanName.Create(name)), TestBoard.TestMaker, new Visibility.Everyone(),
            ValueMap<AttachmentKey, Attachment>.Empty))).PlansOpened[0].Id;

    public static NamedPiece[] Pieces(string text) =>
        [.. TestKitIds.MustRead(CatalogueFile.Read(new StringReader("lattice-catalogue 3\n" + text), "inline.lcat")).Pieces];
}
