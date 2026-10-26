using System.Collections.Generic;
using System.Text;
using Lattice.Api;
using Lattice.Core.Written;

namespace Lattice.Core.Sync;

/// <summary>
/// The command form: what a client sends the host. A header <c>lattice-command &lt;schema&gt; id=&lt;command id&gt;</c>,
/// then one node per line, each a kind word and its fields in the written forms' text encoding. A node names a node
/// written before it as <c>#&lt;n&gt;</c> (n counts the node lines from 0), each at most once; the last node is the
/// command. So nesting costs no escaping, and reading builds every node from earlier ones without recursion.
/// Every <see cref="Command"/> kind of this version has a node; reading refuses an unknown kind, a field it does not
/// know, a reference forward or used twice, and a node no one uses.
/// </summary>
internal static class CommandForm
{
    public const string Form = "lattice-command";

    public const int Schema = 1;

    /// <summary>One moved ghost and its new placement, as a <c>m.to</c> node reads.</summary>
    private sealed record MoveTarget(GhostId Ghost, Placement At);

    /// <summary>The command's text; a kind this version does not know is refused <see cref="ReasonCodes.UnknownKind"/>.</summary>
    public static Result<string, Reason> Write(CommandId id, Command command)
    {
        var writer = new Writer();
        if (writer.Command(command) < 0)
            return Result<string, Reason>.Error(writer.Unknown ?? WrittenForms.Problem("unknown kind"));
        var text = new StringBuilder(256);
        text.Append(Headers.Text(Form, Schema, ValueList.Of(new Field("id", WrittenValues.Command(id))))).Append('\n');
        foreach (var line in writer.Lines)
            text.Append(line).Append('\n');
        return Result<string, Reason>.Ok(text.ToString());
    }

    /// <summary>The command id and command; a problem is a reason (<see cref="WrittenForms.Problem"/>) naming the line.</summary>
    public static Result<(CommandId Id, Command Command), Reason> Read(string text)
    {
        var lines = text.Split('\n');
        var count = lines.Length;
        while (count > 0 && lines[count - 1].Length == 0)
            count--;
        if (count < 2)
            return Fail("no command");
        if (!Headers.Read(lines[0], Form).TryGet(out var header))
            return Fail("not a command document");
        var headerFields = new FieldReader(header.Extra);
        if (!headerFields.Need("id").Bind(WrittenValues.Command).TryGet(out var id))
            return Fail("bad command id");
        if (headerFields.Rest().Count > 0)
            return Fail("unknown header field");

        var reader = new Reader(count - 1);
        for (var i = 1; i < count; i++)
        {
            if (!TextForm.Read(lines[i], ["n"]).TryGet(out var entry))
                return Fail($"line {i + 1}: unreadable");
            if (reader.Node(entry) is { } problem)
                return Fail($"line {i + 1}: {problem}");
        }

        return reader.Root().Match(
            command => Result<(CommandId, Command), Reason>.Ok((id, command)),
            static problem => Fail(problem));
    }

    private static Result<(CommandId, Command), Reason> Fail(string problem) =>
        Result<(CommandId, Command), Reason>.Error(WrittenForms.Problem(problem));

    private static string Ref(int node) => "#" + WrittenValues.Number(node);

    private static string Offset(CellOffset offset) => WrittenValues.Cell(new Cell(offset.X, offset.Y, offset.Z));

    private static string Flag(bool on) => on ? "1" : "0";

    /// <summary>Writes nodes children first; each method returns its node's number, or -1 for a kind it does not know.</summary>
    private sealed class Writer
    {
        public List<string> Lines { get; } = new(8);

        public Reason? Unknown { get; private set; }

        private int Node(string kind, FieldList fields)
        {
            var all = new FieldList().Add("n", kind).AddAll(fields.ToList()).ToList();
            Lines.Add(TextForm.Write(Entry.Of(all), 1));
            return Lines.Count - 1;
        }

        private int Refuse(object value)
        {
            Unknown ??= Reason.Of(ReasonCodes.UnknownKind, WrittenForms.Rule, ValueMap.Of("kind", value.GetType().Name));
            return -1;
        }

        public int Command(Command command)
        {
            switch (command)
            {
                case Api.Command.Place place:
                    return Drafts("place", new FieldList(), place.Ghosts);
                case Api.Command.Remove remove:
                    return WithSelector("remove", remove.Which, new FieldList());
                case Api.Command.Change change:
                {
                    var which = Selector(change.Which);
                    var edit = Edit(change.Edit);
                    if (which < 0 || edit < 0)
                        return -1;
                    var fields = new FieldList().Add("which", Ref(which)).Add("edit", Ref(edit));
                    if (change.UnchangedSince.TryGet(out var since))
                        fields.Add("since", since.Value);
                    return Node("change", fields);
                }
                case Api.Command.Move move:
                {
                    var nodes = new int[move.To.Count];
                    var at = 0;
                    foreach (var pair in move.To)
                        nodes[at++] = Node("m.to", new FieldList().Add("ghost", pair.Key.Value).Add("at", WrittenValues.Placement(pair.Value)));
                    var fields = new FieldList();
                    foreach (var node in nodes)
                        fields.Add("to", Ref(node));
                    if (move.UnchangedSince.TryGet(out var since))
                        fields.Add("since", since.Value);
                    return Node("move", fields);
                }
                case Api.Command.CreatePlan create:
                    return Drafts("create-plan", PlanDraftFields(create.Draft), create.Ghosts);
                case Api.Command.EditPlan edit:
                {
                    var planEdit = PlanEdit(edit.Edit);
                    if (planEdit < 0)
                        return -1;
                    var fields = new FieldList().Add("plan", edit.Plan.Value).Add("edit", Ref(planEdit));
                    if (edit.UnchangedSince.TryGet(out var since))
                        fields.Add("since", since.Value);
                    return Node("edit-plan", fields);
                }
                case Api.Command.MoveToPlan move:
                    return WithSelector("move-to-plan", move.Which, new FieldList().Add("to", move.To.Value));
                case Api.Command.Revert revert:
                    return Node("revert", new FieldList().Add("set", revert.ChangeSet.Value));
                case Api.Command.Batch batch:
                {
                    var steps = new int[batch.Steps.Count];
                    for (var i = 0; i < steps.Length; i++)
                        if ((steps[i] = Command(batch.Steps[i])) < 0)
                            return -1;
                    var fields = new FieldList();
                    foreach (var step in steps)
                        fields.Add("step", Ref(step));
                    return Node("batch", fields);
                }
                case Api.Command.FinishPlan finish:
                    return Node("finish-plan", new FieldList().Add("plan", finish.Plan.Value));
                case Api.Command.CatchUp catchUp:
                    return Node("catch-up", new FieldList().Add("plan", catchUp.Plan.Value));
                case Api.Command.MarkRemoval mark:
                    return WithStructures("mark-removal", mark.Which, Into(new FieldList(), mark.Into).Add("made", mark.MadeBy.Value));
                case Api.Command.PlanSwap swap:
                {
                    var fields = new FieldList();
                    switch (swap.To)
                    {
                        case SwapTo.Exactly exactly:
                            fields.Add("to-piece", exactly.Piece.Hash);
                            break;
                        case SwapTo.SameShapeFrom shape:
                            fields.Add("to-kit", shape.Kit.Hash);
                            break;
                        default:
                            return Refuse(swap.To);
                    }

                    return WithStructures("plan-swap", swap.Which, Into(fields, swap.Into).Add("made", swap.MadeBy.Value));
                }
                case Api.Command.PlanFinish finish:
                    return WithStructures("plan-finish", finish.Which, Into(FinishFields(new FieldList(), finish.Finish), finish.Into).Add("made", finish.MadeBy.Value));
                case Api.Command.PlanRun run:
                {
                    var request = run.Request;
                    var fields = new FieldList().Add("kit", request.Kit.Hash).Add("from", WrittenValues.Cell(request.From));
                    foreach (var via in request.Via)
                        fields.Add("via", WrittenValues.Cell(via));
                    fields.Add("to", WrittenValues.Cell(request.To)).Add("made", request.MadeBy.Value);
                    return Node("plan-run", Into(LayFields(PortFields(fields, request.FromPort, request.ToPort), request.Lay), request.Into));
                }
                case Api.Command.PlanRoute route:
                {
                    var request = route.Request;
                    var toggles = request.Toggles;
                    var fields = new FieldList().Add("kit", request.Kit.Hash).Add("from", WrittenValues.Cell(request.From))
                        .Add("to", WrittenValues.Cell(request.To)).Add("made", request.MadeBy.Value)
                        .Add("frames", Flag(toggles.OnFrames)).Add("bends", Flag(toggles.FewestBends))
                        .Add("doors", Flag(toggles.KeepDoorsAndWindowsClear)).Add("ports", Flag(toggles.KeepPortsFree))
                        .Add("no-bridge", Flag(toggles.NoBridging));
                    if (request.SearchBound.TryGet(out var bound))
                        fields.Add("bound", bound);
                    if (toggles.First.TryGet(out var first))
                        fields.Add("first", first == FirstLeg.Vertical ? "vertical" : "level");
                    if (toggles.HugFramesAndWalls)
                        fields.Add("hug", Flag(true));
                    PortFields(fields, request.FromPort, request.ToPort);
                    foreach (var via in request.Via)
                        fields.Add("via", WrittenValues.Cell(via));
                    foreach (var branch in request.Branches)
                        fields.Add("branch", branch.Port.TryGet(out var port)
                            ? WrittenValues.Cell(branch.From) + "@" + PortText(port)
                            : WrittenValues.Cell(branch.From));
                    return Node("plan-route", Into(LayFields(fields, request.Lay), request.Into));
                }
                case Api.Command.Transform transform:
                {
                    var fields = new FieldList();
                    switch (transform.How)
                    {
                        case Transformation.Move move:
                            fields.Add("how", "move").Add("by", Offset(move.By));
                            break;
                        case Transformation.Rotate rotate:
                            fields.Add("how", "rotate").Add("pivot", WrittenValues.Cell(rotate.Pivot)).Add("turn", rotate.Turn.Index);
                            break;
                        case Transformation.Mirror mirror:
                            fields.Add("how", "mirror").Add("normal", (int)mirror.Plane.Normal).Add("at", mirror.Plane.At);
                            break;
                        default:
                            return Refuse(transform.How);
                    }

                    switch (transform.Mode)
                    {
                        case TransformMode.Move:
                            fields.Add("mode", "move");
                            break;
                        case TransformMode.Copy copy:
                            fields.Add("mode", "copy").Add("made", copy.MadeBy.Value);
                            break;
                        default:
                            return Refuse(transform.Mode);
                    }

                    return WithSelector("transform", transform.Which, fields);
                }
                case Api.Command.PlaceTemplate template:
                {
                    var plan = Node("plan-draft", PlanDraftFields(template.As));
                    var fields = new FieldList().Add("name", template.Template.Name.Value)
                        .Add("origin", WrittenValues.Cell(template.Origin)).Add("turn", template.Turn.Index).Add("as", Ref(plan));
                    return Drafts("place-template", fields, template.Template.Ghosts);
                }
                case Api.Command.PlaceTemplateIn template:
                {
                    var fields = new FieldList().Add("name", template.Template.Name.Value)
                        .Add("origin", WrittenValues.Cell(template.Origin)).Add("turn", template.Turn.Index);
                    return Drafts("place-template-in", Into(fields, template.Into), template.Template.Ghosts);
                }
                case Api.Command.Repair repair:
                    return Node("repair", new FieldList().Add("plan", repair.Plan.Value).Add("made", repair.MadeBy.Value));
                case Api.Command.TakeDown takeDown:
                    return Node("take-down", new FieldList().Add("plan", takeDown.Plan.Value).Add("made", takeDown.MadeBy.Value));
                case Api.Command.Reopen reopen:
                    return Node("reopen", new FieldList().Add("plan", reopen.Plan.Value));
                case Api.Command.PlanSplit split:
                {
                    var fields = Into(new FieldList(), split.Into).Add("made", split.MadeBy.Value);
                    return split.Which switch
                    {
                        SplitTargets.Ghosts ghosts => WithSelector("plan-split", ghosts.Which, fields.Add("of", "ghosts")),
                        SplitTargets.Structures structures => WithStructures("plan-split", structures.Which, fields.Add("of", "structures")),
                        _ => Refuse(split.Which),
                    };
                }
                default:
                    return Refuse(command);
            }
        }

        private int Drafts(string kind, FieldList fields, ValueList<GhostDraft> drafts)
        {
            var nodes = new int[drafts.Count];
            for (var i = 0; i < nodes.Length; i++)
                nodes[i] = Node("draft", new FieldList().AddAll(DraftForm.Write(drafts[i], ValueList<Field>.Empty)));
            foreach (var node in nodes)
                fields.Add("ghost", Ref(node));
            return Node(kind, fields);
        }

        private int WithSelector(string kind, Selector which, FieldList fields)
        {
            var node = Selector(which);
            return node < 0 ? -1 : Node(kind, fields.Add("which", Ref(node)));
        }

        private int WithStructures(string kind, StructureSelector which, FieldList fields)
        {
            var node = Structures(which);
            return node < 0 ? -1 : Node(kind, fields.Add("which", Ref(node)));
        }

        private static FieldList Into(FieldList fields, Option<PlanId> into) =>
            into.TryGet(out var plan) ? fields.Add("into", plan.Value) : fields;

        /// <summary>A run's or route's port ends (<see cref="PortText"/>); nothing for a request without them.</summary>
        private static FieldList PortFields(FieldList fields, Option<PortEnd> from, Option<PortEnd> to)
        {
            if (from.TryGet(out var start))
                fields.Add("from-port", PortText(start));
            if (to.TryGet(out var end))
                fields.Add("to-port", PortText(end));
            return fields;
        }

        /// <summary>How a run's or route's pieces are chosen: a flag for each choice that is on; nothing for the default.</summary>
        private static FieldList LayFields(FieldList fields, LayOptions lay)
        {
            if (lay.SinglePiecesOnly)
                fields.Add("singles", Flag(true));
            if (lay.MatchGrade)
                fields.Add("grade", Flag(true));
            return fields;
        }

        /// <summary>
        /// A port end: <c>&lt;ghost id&gt;:&lt;port index&gt;</c> on a ghost; <c>s&lt;structure&gt;:&lt;port index&gt;</c> on a
        /// built structure, which a reader that knows only ghost ends refuses rather than reads as a ghost.
        /// </summary>
        private static string PortText(PortEnd end) => end switch
        {
            PortEnd.OnGhost onGhost => WrittenValues.Number(onGhost.Ghost.Value) + ":" + WrittenValues.Number(onGhost.Port.Value),
            PortEnd.OnStructure onStructure => "s" + WrittenValues.Structure(onStructure.Structure) + ":" + WrittenValues.Number(onStructure.Port.Value),
            _ => "",
        };

        private static FieldList PlanDraftFields(PlanDraft draft)
        {
            var fields = new FieldList().Add("name", draft.Name.Value).Add("made", draft.MadeBy.Value);
            Common.WriteVisibility(fields, draft.Visibility);
            Common.WriteData(fields, draft.HigherLayerData);
            return fields;
        }

        private static FieldList FinishFields(FieldList fields, Finish finish)
        {
            if (finish.Colour.TryGet(out var colour))
                fields.Add("paint", colour.Index);
            if (finish.Name.TryGet(out var name))
                fields.Add("name", name.Value);
            if (finish.Setting.TryGet(out var setting))
                fields.Add("setting", WrittenValues.Double(setting.Value));
            return fields;
        }

        private int Selector(Selector selector)
        {
            switch (selector)
            {
                case Api.Selector.Everything:
                    return Node("s.everything", new FieldList());
                case Api.Selector.Ids ids:
                {
                    var fields = new FieldList();
                    foreach (var id in ids.Values)
                        fields.Add("id", id.Value);
                    return Node("s.ids", fields);
                }
                case Api.Selector.Box box:
                    return Node("s.box", new FieldList().Add("corner", WrittenValues.Cell(box.Corner)).Add("opposite", WrittenValues.Cell(box.Opposite)));
                case Api.Selector.RunThrough run:
                    return Node("s.run", new FieldList().Add("cell", WrittenValues.Cell(run.Cell)));
                case Api.Selector.Network network:
                    return Node("s.network", new FieldList().Add("of", network.Of.Value));
                case Api.Selector.Room room:
                    return Node("s.room", new FieldList().Add("of", room.Of.Value));
                case Api.Selector.Group group:
                    return Node("s.group", new FieldList().Add("name", group.Name.Value));
                case Api.Selector.Owner owner:
                    return Node("s.owner", new FieldList().Add("actor", WrittenValues.Actor(owner.Actor)));
                case Api.Selector.MadeBy made:
                    return Node("s.made", new FieldList().Add("maker", made.Maker.Value));
                case Api.Selector.InPlan plan:
                    return Node("s.plan", new FieldList().Add("plan", plan.Plan.Value));
                case Api.Selector.Tagged tagged:
                {
                    var fields = new FieldList().Add("key", tagged.Key.Value);
                    if (tagged.Value.TryGet(out var value))
                        fields.Add("value", value.Value);
                    return Node("s.tagged", fields);
                }
                case Api.Selector.OfPiece piece:
                    return Node("s.piece", new FieldList().Add("piece", piece.Piece.Hash));
                case Api.Selector.All all:
                    return Selectors("s.all", all.Of);
                case Api.Selector.Any any:
                    return Selectors("s.any", any.Of);
                default:
                    return Refuse(selector);
            }
        }

        private int Selectors(string kind, ValueList<Selector> of)
        {
            var nodes = new int[of.Count];
            for (var i = 0; i < nodes.Length; i++)
                if ((nodes[i] = Selector(of[i])) < 0)
                    return -1;
            var fields = new FieldList();
            foreach (var node in nodes)
                fields.Add("of", Ref(node));
            return Node(kind, fields);
        }

        private int Structures(StructureSelector selector)
        {
            switch (selector)
            {
                case StructureSelector.Refs refs:
                {
                    var fields = new FieldList();
                    foreach (var structure in refs.Values)
                        fields.Add("ref", WrittenValues.Structure(structure));
                    return Node("t.refs", fields);
                }
                case StructureSelector.Box box:
                    return Node("t.box", new FieldList().Add("corner", WrittenValues.Cell(box.Corner)).Add("opposite", WrittenValues.Cell(box.Opposite)));
                case StructureSelector.RunThrough run:
                    return Node("t.run", new FieldList().Add("cell", WrittenValues.Cell(run.Cell)));
                case StructureSelector.Network network:
                    return Node("t.network", new FieldList().Add("of", network.Of.Value));
                case StructureSelector.Room room:
                    return Node("t.room", new FieldList().Add("of", room.Of.Value));
                case StructureSelector.OfPiece piece:
                    return Node("t.piece", new FieldList().Add("piece", piece.Piece.Hash));
                case StructureSelector.All all:
                    return StructureList("t.all", all.Of);
                case StructureSelector.Any any:
                    return StructureList("t.any", any.Of);
                default:
                    return Refuse(selector);
            }
        }

        private int StructureList(string kind, ValueList<StructureSelector> of)
        {
            var nodes = new int[of.Count];
            for (var i = 0; i < nodes.Length; i++)
                if ((nodes[i] = Structures(of[i])) < 0)
                    return -1;
            var fields = new FieldList();
            foreach (var node in nodes)
                fields.Add("of", Ref(node));
            return Node(kind, fields);
        }

        private int Edit(GhostEdit edit)
        {
            switch (edit)
            {
                case GhostEdit.SetLabel label:
                    return Node("e.label", label.To.TryGet(out var to) ? new FieldList().Add("to", to.Value) : new FieldList());
                case GhostEdit.SetTint tint:
                    return Node("e.tint", tint.To.TryGet(out var colour) ? new FieldList().Add("to", WrittenValues.Tint(colour)) : new FieldList());
                case GhostEdit.JoinGroup join:
                    return Node("e.join", new FieldList().Add("group", join.Group.Value));
                case GhostEdit.LeaveGroup leave:
                    return Node("e.leave", new FieldList().Add("group", leave.Group.Value));
                case GhostEdit.SetFinish finish:
                    return Node("e.finish", finish.To.TryGet(out var f) ? FinishFields(new FieldList(), f) : new FieldList());
                case GhostEdit.SetVisibility visibility:
                    return Node("e.vis", new FieldList().Add("to", WrittenValues.Visibility(visibility.To)));
                case GhostEdit.Attach attach:
                    return Node("e.attach", new FieldList().Add("key", attach.Key.Value).Add("value", attach.Value.Value));
                case GhostEdit.Detach detach:
                    return Node("e.detach", new FieldList().Add("key", detach.Key.Value));
                default:
                    return Refuse(edit);
            }
        }

        private int PlanEdit(PlanEdit edit) => edit switch
        {
            Api.PlanEdit.Rename rename => Node("p.rename", new FieldList().Add("to", rename.To.Value)),
            Api.PlanEdit.ChangeOwner owner => Node("p.owner", new FieldList().Add("to", WrittenValues.Actor(owner.To))),
            Api.PlanEdit.ChangeVisibility visibility => Node("p.vis", new FieldList().Add("to", WrittenValues.Visibility(visibility.To))),
            Api.PlanEdit.SetData set => Node("p.set-data", new FieldList().Add("key", set.Key.Value).Add("value", set.Value.Value)),
            Api.PlanEdit.ClearData clear => Node("p.clear-data", new FieldList().Add("key", clear.Key.Value)),
            _ => Refuse(edit),
        };
    }

    /// <summary>Builds nodes in order from earlier ones; every problem is a short text naming what is wrong.</summary>
    private sealed class Reader(int capacity)
    {
        private readonly object?[] _built = new object?[capacity];
        private readonly bool[] _used = new bool[capacity];
        private int _count;

        public string? Node(Entry entry)
        {
            if (entry.Groups.Count > 0)
                return "unexpected group";
            var read = new FieldReader(entry.Fields);
            if (!read.Take("n").TryGet(out var kind))
                return "no kind";
            var built = Build(kind, read);
            if (!built.TryGet(out var value))
                return built.TryGetError(out var problem) ? problem : "unreadable";
            if (read.Rest().Count > 0)
                return $"unknown field '{read.Rest()[0].Key}' on {kind}";
            _built[_count++] = value;
            return null;
        }

        public Result<Command, string> Root()
        {
            if (_count == 0 || _built[_count - 1] is not Command command)
                return Result<Command, string>.Error("the last node is not a command");
            for (var i = 0; i < _count - 1; i++)
                if (!_used[i])
                    return Result<Command, string>.Error($"node {i} is not used");
            return Result<Command, string>.Ok(command);
        }

        private Result<object, string> Build(string kind, FieldReader read) => kind switch
        {
            "draft" => Draft(read),
            "plan-draft" => PlanDraft(read).Map(static d => (object)d),
            "place" => Ghosts(read).Map(static g => (object)new Command.Place(g)),
            "remove" => Use<Selector>(read, "which").Map(static s => (object)new Command.Remove(s)),
            "change" => Use<Selector>(read, "which").Bind(which => Use<GhostEdit>(read, "edit")
                .Bind(edit => Optional(read, "since", static t => WrittenValues.Long(t).Map(static v => new Revision(v)))
                    .Map(since => (object)new Command.Change(which, edit, since)))),
            "move" => UseAll<MoveTarget>(read, "to").Bind(to => Optional(read, "since", static t => WrittenValues.Long(t).Map(static v => new Revision(v)))
                .Bind(since => Moves(to).Map(map => (object)new Command.Move(map, since)))),
            "m.to" => Need(read, "ghost", static t => WrittenValues.Long(t).Map(static v => new GhostId(v)))
                .Bind(ghost => Need(read, "at", WrittenValues.Placement).Map(at => (object)new MoveTarget(ghost, at))),
            "create-plan" => PlanDraft(read).Bind(d => Ghosts(read).Map(g => (object)new Command.CreatePlan(d) { Ghosts = g })),
            "edit-plan" => Need(read, "plan", PlanOf).Bind(plan => Use<PlanEdit>(read, "edit")
                .Bind(edit => Optional(read, "since", static t => WrittenValues.Long(t).Map(static v => new Revision(v)))
                    .Map(since => (object)new Command.EditPlan(plan, edit, since)))),
            "move-to-plan" => Use<Selector>(read, "which").Bind(which => Need(read, "to", PlanOf).Map(to => (object)new Command.MoveToPlan(which, to))),
            "revert" => Need(read, "set", static t => WrittenValues.Long(t).Map(static v => new ChangeSetId(v))).Map(static s => (object)new Command.Revert(s)),
            "batch" => UseAll<Command>(read, "step").Map(static steps => (object)new Command.Batch(steps)),
            "finish-plan" => Need(read, "plan", PlanOf).Map(static p => (object)new Command.FinishPlan(p)),
            "catch-up" => Need(read, "plan", PlanOf).Map(static p => (object)new Command.CatchUp(p)),
            "mark-removal" => Use<StructureSelector>(read, "which").Bind(which => Into(read).Bind(into => Made(read)
                .Map(made => (object)new Command.MarkRemoval(which, into, made)))),
            "plan-swap" => Use<StructureSelector>(read, "which").Bind(which => SwapTo(read)
                .Bind(to => Into(read).Bind(into => Made(read).Map(made => (object)new Command.PlanSwap(which, to, into, made))))),
            "plan-finish" => Use<StructureSelector>(read, "which").Bind(which => Finish(read)
                .Bind(finish => finish.TryGet(out var f)
                    ? Into(read).Bind(into => Made(read).Map(made => (object)new Command.PlanFinish(which, f, into, made)))
                    : Result<object, string>.Error("plan-finish names a finish"))),
            "plan-run" => Run(read),
            "plan-route" => Route(read),
            "transform" => Use<Selector>(read, "which").Bind(which => Transformation(read)
                .Bind(how => Mode(read).Map(mode => (object)new Command.Transform(which, how, mode)))),
            "place-template" => PlaceTemplate(read),
            "place-template-in" => PlaceTemplateIn(read),
            "repair" => Need(read, "plan", PlanOf).Bind(p => Made(read).Map(made => (object)new Command.Repair(p, made))),
            "take-down" => Need(read, "plan", PlanOf).Bind(p => Made(read).Map(made => (object)new Command.TakeDown(p, made))),
            "reopen" => Need(read, "plan", PlanOf).Map(static p => (object)new Command.Reopen(p)),
            "plan-split" => Split(read),
            "s.everything" => Result<object, string>.Ok(new Selector.Everything()),
            "s.ids" => Ids(read),
            "s.box" => Need(read, "corner", WrittenValues.Cell).Bind(c => Need(read, "opposite", WrittenValues.Cell).Map(o => (object)new Selector.Box(c, o))),
            "s.run" => Need(read, "cell", WrittenValues.Cell).Map(static c => (object)new Selector.RunThrough(c)),
            "s.network" => Need(read, "of", WrittenValues.Long).Map(static v => (object)new Selector.Network(new NetworkRef(v))),
            "s.room" => Need(read, "of", WrittenValues.Long).Map(static v => (object)new Selector.Room(new RoomRef(v))),
            "s.group" => Need(read, "name", GroupOf).Map(static g => (object)new Selector.Group(g)),
            "s.owner" => Need(read, "actor", WrittenValues.Actor).Map(static a => (object)new Selector.Owner(a)),
            "s.made" => Need(read, "maker", MakerOf).Map(static m => (object)new Selector.MadeBy(m)),
            "s.plan" => Need(read, "plan", PlanOf).Map(static p => (object)new Selector.InPlan(p)),
            "s.tagged" => Need(read, "key", KeyOf).Bind(key => Optional(read, "value", AttachmentOf).Map(value => (object)new Selector.Tagged(key, value))),
            "s.piece" => Need(read, "piece", PieceOf).Map(static p => (object)new Selector.OfPiece(p)),
            "s.all" => UseAll<Selector>(read, "of").Map(static of => (object)new Selector.All(of)),
            "s.any" => UseAll<Selector>(read, "of").Map(static of => (object)new Selector.Any(of)),
            "t.refs" => StructureRefs(read),
            "t.box" => Need(read, "corner", WrittenValues.Cell).Bind(c => Need(read, "opposite", WrittenValues.Cell).Map(o => (object)new StructureSelector.Box(c, o))),
            "t.run" => Need(read, "cell", WrittenValues.Cell).Map(static c => (object)new StructureSelector.RunThrough(c)),
            "t.network" => Need(read, "of", WrittenValues.Long).Map(static v => (object)new StructureSelector.Network(new NetworkRef(v))),
            "t.room" => Need(read, "of", WrittenValues.Long).Map(static v => (object)new StructureSelector.Room(new RoomRef(v))),
            "t.piece" => Need(read, "piece", PieceOf).Map(static p => (object)new StructureSelector.OfPiece(p)),
            "t.all" => UseAll<StructureSelector>(read, "of").Map(static of => (object)new StructureSelector.All(of)),
            "t.any" => UseAll<StructureSelector>(read, "of").Map(static of => (object)new StructureSelector.Any(of)),
            "e.label" => Optional(read, "to", static t => WrittenValues.Checked(Label.Create(t), "label", t)).Map(static l => (object)new GhostEdit.SetLabel(l)),
            "e.tint" => Optional(read, "to", WrittenValues.Tint).Map(static t => (object)new GhostEdit.SetTint(t)),
            "e.join" => Need(read, "group", GroupOf).Map(static g => (object)new GhostEdit.JoinGroup(g)),
            "e.leave" => Need(read, "group", GroupOf).Map(static g => (object)new GhostEdit.LeaveGroup(g)),
            "e.finish" => Finish(read).Map(static f => (object)new GhostEdit.SetFinish(f)),
            "e.vis" => Need(read, "to", WrittenValues.Visibility).Map(static v => (object)new GhostEdit.SetVisibility(v)),
            "e.attach" => Need(read, "key", KeyOf).Bind(key => Need(read, "value", AttachmentOf).Map(value => (object)new GhostEdit.Attach(key, value))),
            "e.detach" => Need(read, "key", KeyOf).Map(static k => (object)new GhostEdit.Detach(k)),
            "p.rename" => Need(read, "to", static t => WrittenValues.Checked(PlanName.Create(t), "plan name", t)).Map(static n => (object)new PlanEdit.Rename(n)),
            "p.owner" => Need(read, "to", WrittenValues.Actor).Map(static a => (object)new PlanEdit.ChangeOwner(a)),
            "p.vis" => Need(read, "to", WrittenValues.Visibility).Map(static v => (object)new PlanEdit.ChangeVisibility(v)),
            "p.set-data" => Need(read, "key", KeyOf).Bind(key => Need(read, "value", AttachmentOf).Map(value => (object)new PlanEdit.SetData(key, value))),
            "p.clear-data" => Need(read, "key", KeyOf).Map(static k => (object)new PlanEdit.ClearData(k)),
            _ => Result<object, string>.Error($"unknown kind '{kind}'"),
        };

        private static Result<object, string> Draft(FieldReader read) =>
            DraftForm.Read(read.Rest()) is DraftRecord.Known { Extra.Count: 0 } known
                ? TakeAll(read, Result<object, string>.Ok(known.Draft))
                : Result<object, string>.Error("unreadable draft");

        // A draft node's fields all belong to the draft form, which reads them from a copy.
        private static Result<object, string> TakeAll(FieldReader read, Result<object, string> result)
        {
            foreach (var field in read.Rest())
                read.Take(field.Key);
            return result;
        }

        private Result<ValueList<GhostDraft>, string> Ghosts(FieldReader read) => UseAll<GhostDraft>(read, "ghost");

        private static Result<PlanDraft, string> PlanDraft(FieldReader read)
        {
            var problem = "";
            if (!Common.Get(read.Need("name").Bind(static t => WrittenValues.Checked(PlanName.Create(t), "plan name", t)), out var name, ref problem) ||
                !Common.Get(read.Need("made").Bind(MakerOf), out var made, ref problem) ||
                !Common.Get(Common.ReadVisibility(read), out var visibility, ref problem) ||
                !Common.Get(Common.ReadData(read), out var data, ref problem))
                return Result<PlanDraft, string>.Error(problem);
            return Result<PlanDraft, string>.Ok(new PlanDraft(name, made, visibility, data));
        }

        private Result<object, string> Run(FieldReader read)
        {
            var problem = "";
            if (!Common.Get(read.Need("kit").Bind(KitOf), out var kit, ref problem) ||
                !Common.Get(read.Need("from").Bind(WrittenValues.Cell), out var from, ref problem))
                return Result<object, string>.Error(problem);
            var via = new List<Cell>();
            foreach (var text in read.TakeAll("via"))
            {
                if (!WrittenValues.Cell(text).TryGet(out var cell))
                    return Result<object, string>.Error($"bad via '{text}'");
                via.Add(cell);
            }

            if (!Common.Get(read.Need("to").Bind(WrittenValues.Cell), out var to, ref problem) ||
                !Common.Get(read.Need("made").Bind(MakerOf), out var made, ref problem) ||
                !Common.Get(Into(read), out var into, ref problem) ||
                !Common.Get(Optional(read, "from-port", PortEndOf), out var fromPort, ref problem) ||
                !Common.Get(Optional(read, "to-port", PortEndOf), out var toPort, ref problem) ||
                !Common.Get(Lay(read), out var lay, ref problem))
                return Result<object, string>.Error(problem);
            return Result<object, string>.Ok(new Command.PlanRun(new RunRequest(kit, from, ValueList.From(via), to, into, made)
            {
                FromPort = fromPort,
                ToPort = toPort,
                Lay = lay,
            }));
        }

        private static Result<LayOptions, string> Lay(FieldReader read)
        {
            var problem = "";
            if (!Common.Get(Optional(read, "singles", FlagOf), out var singles, ref problem) ||
                !Common.Get(Optional(read, "grade", FlagOf), out var grade, ref problem))
                return Result<LayOptions, string>.Error(problem);
            var lay = new LayOptions(singles.Reduce(false), grade.Reduce(false));
            return Result<LayOptions, string>.Ok(lay == LayOptions.Default ? LayOptions.Default : lay);
        }

        private Result<object, string> Split(FieldReader read)
        {
            var problem = "";
            if (!Common.Get(read.Need("of"), out var of, ref problem) ||
                !Common.Get(Into(read), out var into, ref problem) ||
                !Common.Get(read.Need("made").Bind(MakerOf), out var made, ref problem))
                return Result<object, string>.Error(problem);
            return of switch
            {
                "ghosts" => Use<Selector>(read, "which").Map(which => (object)new Command.PlanSplit(new SplitTargets.Ghosts(which), into, made)),
                "structures" => Use<StructureSelector>(read, "which").Map(which => (object)new Command.PlanSplit(new SplitTargets.Structures(which), into, made)),
                _ => Result<object, string>.Error($"bad split targets '{of}'"),
            };
        }

        /// <summary>A route branch as the writer writes it: its start cell, then <c>@</c> and its port end when it has one.</summary>
        private static Result<RouteBranch, string> BranchOf(string text)
        {
            var at = text.IndexOf('@');
            if (at < 0)
                return WrittenValues.Cell(text).Map(static cell => new RouteBranch(cell, Option<PortEnd>.None));
            return WrittenValues.Cell(text.Substring(0, at))
                .Bind(cell => PortEndOf(text.Substring(at + 1)).Map(port => new RouteBranch(cell, Option.Some(port))));
        }

        private static Result<FirstLeg, string> FirstOf(string text) => text switch
        {
            "vertical" => Result<FirstLeg, string>.Ok(FirstLeg.Vertical),
            "level" => Result<FirstLeg, string>.Ok(FirstLeg.Level),
            _ => Result<FirstLeg, string>.Error($"bad first leg '{text}'"),
        };

        private Result<object, string> Route(FieldReader read)
        {
            var problem = "";
            if (!Common.Get(read.Need("kit").Bind(KitOf), out var kit, ref problem) ||
                !Common.Get(read.Need("from").Bind(WrittenValues.Cell), out var from, ref problem) ||
                !Common.Get(read.Need("to").Bind(WrittenValues.Cell), out var to, ref problem) ||
                !Common.Get(read.Need("made").Bind(MakerOf), out var made, ref problem) ||
                !Common.Get(read.Need("frames").Bind(FlagOf), out var frames, ref problem) ||
                !Common.Get(read.Need("bends").Bind(FlagOf), out var bends, ref problem) ||
                !Common.Get(read.Need("doors").Bind(FlagOf), out var doors, ref problem) ||
                !Common.Get(read.Need("ports").Bind(FlagOf), out var ports, ref problem) ||
                !Common.Get(read.Need("no-bridge").Bind(FlagOf), out var noBridge, ref problem) ||
                !Common.Get(Optional(read, "bound", WrittenValues.Int), out var bound, ref problem) ||
                !Common.Get(Into(read), out var into, ref problem) ||
                !Common.Get(Optional(read, "from-port", PortEndOf), out var fromPort, ref problem) ||
                !Common.Get(Optional(read, "to-port", PortEndOf), out var toPort, ref problem) ||
                !Common.Get(Optional(read, "first", FirstOf), out var first, ref problem) ||
                !Common.Get(Optional(read, "hug", FlagOf), out var hug, ref problem) ||
                !Common.Get(Lay(read), out var lay, ref problem))
                return Result<object, string>.Error(problem);
            var pins = new List<Cell>();
            foreach (var text in read.TakeAll("via"))
            {
                if (!WrittenValues.Cell(text).TryGet(out var cell))
                    return Result<object, string>.Error($"bad via '{text}'");
                pins.Add(cell);
            }

            var branches = new List<RouteBranch>();
            foreach (var text in read.TakeAll("branch"))
            {
                if (!BranchOf(text).TryGet(out var branch))
                    return Result<object, string>.Error($"bad branch '{text}'");
                branches.Add(branch);
            }

            return Result<object, string>.Ok(new Command.PlanRoute(
                new RouteRequest(kit, from, to, new RouteToggles(frames, bends, doors, ports, noBridge) { First = first, HugFramesAndWalls = hug.Reduce(false) }, into, made)
                {
                    SearchBound = bound,
                    FromPort = fromPort,
                    ToPort = toPort,
                    Lay = lay,
                    Branches = branches.Count == 0 ? ValueList<RouteBranch>.Empty : ValueList.From(branches),
                    Via = pins.Count == 0 ? ValueList<Cell>.Empty : ValueList.From(pins),
                }));
        }

        /// <summary>A port end as the writer writes it: <c>&lt;ghost id&gt;:&lt;port index&gt;</c> or <c>s&lt;structure&gt;:&lt;port index&gt;</c>.</summary>
        private static Result<PortEnd, string> PortEndOf(string text)
        {
            var colon = text.LastIndexOf(':');
            if (colon <= 0)
                return Result<PortEnd, string>.Error($"bad port end '{text}'");
            var problem = "";
            if (!Common.Get(WrittenValues.Int(text.Substring(colon + 1)), out var index, ref problem))
                return Result<PortEnd, string>.Error(problem);
            if (!PortIndex.Create(index).TryGet(out var port))
                return Result<PortEnd, string>.Error($"bad port index '{text}'");
            if (text[0] == 's')
                return WrittenValues.Structure(text.Substring(1, colon - 1))
                    .Map(structure => (PortEnd)new PortEnd.OnStructure(structure, port))
                    .MapError(_ => $"bad port end '{text}'");
            if (!Common.Get(WrittenValues.Long(text.Substring(0, colon)), out var ghost, ref problem))
                return Result<PortEnd, string>.Error(problem);
            return Result<PortEnd, string>.Ok(new PortEnd.OnGhost(new GhostId(ghost), port));
        }

        private Result<object, string> PlaceTemplate(FieldReader read)
        {
            var problem = "";
            if (!Common.Get(read.Need("name").Bind(static t => WrittenValues.Checked(TemplateName.Create(t), "template name", t)), out var name, ref problem) ||
                !Common.Get(read.Need("origin").Bind(WrittenValues.Cell), out var origin, ref problem) ||
                !Common.Get(read.Need("turn").Bind(TurnOf), out var turn, ref problem) ||
                !Common.Get(Use<PlanDraft>(read, "as"), out var plan, ref problem) ||
                !Common.Get(Ghosts(read), out var ghosts, ref problem))
                return Result<object, string>.Error(problem);
            return Result<object, string>.Ok(new Command.PlaceTemplate(new Template(name, ghosts), origin, turn, plan));
        }

        private Result<object, string> PlaceTemplateIn(FieldReader read)
        {
            var problem = "";
            if (!Common.Get(read.Need("name").Bind(static t => WrittenValues.Checked(TemplateName.Create(t), "template name", t)), out var name, ref problem) ||
                !Common.Get(read.Need("origin").Bind(WrittenValues.Cell), out var origin, ref problem) ||
                !Common.Get(read.Need("turn").Bind(TurnOf), out var turn, ref problem) ||
                !Common.Get(Into(read), out var into, ref problem) ||
                !Common.Get(Ghosts(read), out var ghosts, ref problem))
                return Result<object, string>.Error(problem);
            return Result<object, string>.Ok(new Command.PlaceTemplateIn(new Template(name, ghosts), origin, turn, into));
        }

        private static Result<object, string> Ids(FieldReader read)
        {
            var ids = new List<GhostId>();
            foreach (var text in read.TakeAll("id"))
            {
                if (!WrittenValues.Long(text).TryGet(out var id))
                    return Result<object, string>.Error($"bad ghost id '{text}'");
                ids.Add(new GhostId(id));
            }

            return Result<object, string>.Ok(new Selector.Ids(ValueList.From(ids)));
        }

        private static Result<object, string> StructureRefs(FieldReader read)
        {
            var refs = new List<StructureRef>();
            foreach (var text in read.TakeAll("ref"))
            {
                if (!WrittenValues.Structure(text).TryGet(out var structure))
                    return Result<object, string>.Error($"bad structure '{text}'");
                refs.Add(structure);
            }

            return Result<object, string>.Ok(new StructureSelector.Refs(ValueList.From(refs)));
        }

        private static Result<SwapTo, string> SwapTo(FieldReader read)
        {
            if (read.Take("to-piece").TryGet(out var piece))
                return PieceOf(piece).Map(static p => (SwapTo)new SwapTo.Exactly(p));
            if (read.Take("to-kit").TryGet(out var kit))
                return KitOf(kit).Map(static k => (SwapTo)new SwapTo.SameShapeFrom(k));
            return Result<SwapTo, string>.Error("plan-swap names to-piece or to-kit");
        }

        private static Result<Transformation, string> Transformation(FieldReader read)
        {
            if (!read.Take("how").TryGet(out var how))
                return Result<Transformation, string>.Error("missing how");
            return how switch
            {
                "move" => Need(read, "by", WrittenValues.Cell).Map(static c => (Transformation)new Transformation.Move(new CellOffset(c.X, c.Y, c.Z))),
                "rotate" => Need(read, "pivot", WrittenValues.Cell).Bind(pivot => Need(read, "turn", TurnOf).Map(turn => (Transformation)new Transformation.Rotate(pivot, turn))),
                "mirror" => Need(read, "normal", static t => WrittenValues.Int(t).Bind(static n => n is >= 1 and <= 3
                        ? Result<Axis, string>.Ok((Axis)n)
                        : Result<Axis, string>.Error("bad normal")))
                    .Bind(normal => Need(read, "at", WrittenValues.Int).Map(at => (Transformation)new Transformation.Mirror(new MirrorPlane(normal, at)))),
                _ => Result<Transformation, string>.Error($"unknown transformation '{how}'"),
            };
        }

        private static Result<Option<Finish>, string> Finish(FieldReader read)
        {
            var problem = "";
            if (!Common.Get(Optional(read, "paint", static t => WrittenValues.Int(t).Bind(i => WrittenValues.Checked(PaintColour.Create(i), "paint", t))), out var colour, ref problem) ||
                !Common.Get(Optional(read, "name", static t => WrittenValues.Checked(ThingName.Create(t), "name", t)), out var name, ref problem) ||
                !Common.Get(Optional(read, "setting", static t => WrittenValues.Double(t).Bind(d => WrittenValues.Checked(Setting.Create(d), "setting", t))), out var setting, ref problem))
                return Result<Option<Finish>, string>.Error(problem);
            if (colour.IsNone && name.IsNone && setting.IsNone)
                return Result<Option<Finish>, string>.Ok(Option<Finish>.None);
            return WrittenValues.Checked(Api.Finish.Create(colour, name, setting), "finish", "").Map(static f => Option.Some(f));
        }

        private static Result<Option<PlanId>, string> Into(FieldReader read) => Optional(read, "into", PlanOf);

        private static Result<Maker, string> Made(FieldReader read) => Need(read, "made", MakerOf);

        private static Result<TransformMode, string> Mode(FieldReader read) =>
            read.Need("mode").Bind(mode => mode switch
            {
                "move" => Result<TransformMode, string>.Ok(new TransformMode.Move()),
                "copy" => Made(read).Map(static made => (TransformMode)new TransformMode.Copy(made)),
                _ => Result<TransformMode, string>.Error($"bad mode '{mode}'"),
            });

        private static Result<ValueMap<GhostId, Placement>, string> Moves(ValueList<MoveTarget> targets)
        {
            var to = new Dictionary<GhostId, Placement>(targets.Count);
            foreach (var target in targets)
            {
                if (to.ContainsKey(target.Ghost))
                    return Result<ValueMap<GhostId, Placement>, string>.Error($"ghost {target.Ghost.Value} moved twice");
                to.Add(target.Ghost, target.At);
            }

            return Result<ValueMap<GhostId, Placement>, string>.Ok(ValueMap<GhostId, Placement>.From(to));
        }

        private Result<T, string> Use<T>(FieldReader read, string key)
            where T : notnull =>
            read.Need(key).Bind(Resolve<T>);

        private Result<ValueList<T>, string> UseAll<T>(FieldReader read, string key)
            where T : notnull
        {
            var values = new List<T>();
            foreach (var text in read.TakeAll(key))
            {
                var resolved = Resolve<T>(text);
                if (!resolved.TryGet(out var value))
                    return Result<ValueList<T>, string>.Error(resolved.TryGetError(out var problem) ? problem : text);
                values.Add(value);
            }

            return Result<ValueList<T>, string>.Ok(ValueList.From(values));
        }

        private Result<T, string> Resolve<T>(string text)
            where T : notnull
        {
            if (text.Length < 2 || text[0] != '#' || !WrittenValues.Long(text.Substring(1)).TryGet(out var number) ||
                number < 0 || number >= _count)
                return Result<T, string>.Error($"bad reference '{text}'");
            var node = (int)number;
            if (_used[node])
                return Result<T, string>.Error($"node {node} used twice");
            if (_built[node] is not T value)
                return Result<T, string>.Error($"node {node} is not a {typeof(T).Name}");
            _used[node] = true;
            return Result<T, string>.Ok(value);
        }

        private static Result<T, string> Need<T>(FieldReader read, string key, System.Func<string, Result<T, string>> parse)
            where T : notnull =>
            read.Need(key).Bind(parse);

        private static Result<Option<T>, string> Optional<T>(FieldReader read, string key, System.Func<string, Result<T, string>> parse)
            where T : notnull =>
            Common.Optional(read.Take(key), parse);

        private static Result<PlanId, string> PlanOf(string text) => WrittenValues.Long(text).Map(static v => new PlanId(v));

        private static Result<PrefabKey, string> PieceOf(string text) => WrittenValues.Int(text).Map(static v => new PrefabKey(v));

        private static Result<KitKey, string> KitOf(string text) => WrittenValues.Int(text).Map(static v => new KitKey(v));

        private static Result<Turn, string> TurnOf(string text) =>
            WrittenValues.Int(text).Bind(i => WrittenValues.Checked(Turn.Create(i), "turn", text));

        private static Result<bool, string> FlagOf(string text) => text switch
        {
            "1" => Result<bool, string>.Ok(true),
            "0" => Result<bool, string>.Ok(false),
            _ => Result<bool, string>.Error($"bad flag '{text}'"),
        };

        private static Result<GroupName, string> GroupOf(string text) => WrittenValues.Checked(GroupName.Create(text), "group", text);

        private static Result<Maker, string> MakerOf(string text) => WrittenValues.Checked(Maker.Parse(text), "maker", text);

        private static Result<AttachmentKey, string> KeyOf(string text) => WrittenValues.Checked(AttachmentKey.Parse(text), "key", text);

        private static Result<Attachment, string> AttachmentOf(string text) => WrittenValues.Checked(Attachment.Create(text), "data", text);
    }
}
