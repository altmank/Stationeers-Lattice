using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Plans;

namespace Lattice.Core.Board;

/// <summary>
/// The board as one actor may see it: every read asks the access policy, and results are cached until the next commit,
/// a change of the policy's answers or of what the selectors module reads, then dropped. A repeated read on an unchanged board allocates nothing; with no
/// policy installed the filter asks nothing. Main thread.
/// </summary>
internal sealed class BoardView(GhostBoard board, Actor viewer) : IGhostBoard
{
    private readonly Dictionary<Cell, ValueList<Ghost>> _at = new();
    private readonly Dictionary<StructureRef, ValueList<Ghost>> _on = new();
    private readonly Dictionary<Selector, Result<ValueList<Ghost>, Reason>> _select = new();
    private int _epoch = -1;
    private int _policyEpoch = -1;
    private long _selectorsRevision = -1;
    private ValueList<GroupCount>? _groupCounts;

    public Actor Viewer { get; } = viewer;

    public Revision Revision => board.Revision;

    public Option<Ghost> Find(GhostId id) =>
        board.Index.Ghosts.TryGetValue(id, out var entry) && board.Policies.MaySee(Viewer, entry.Ghost)
            ? Option.Some(entry.Ghost)
            : Option<Ghost>.None;

    public Result<ValueList<Ghost>, Reason> Select(Selector selector)
    {
        Fresh();
        if (!_select.TryGetValue(selector, out var selected))
            _select[selector] = selected = board.Select(selector, Viewer).Map(static entries =>
            {
                var ghosts = new Ghost[entries.Count];
                for (var i = 0; i < ghosts.Length; i++)
                    ghosts[i] = entries[i].Ghost;
                return Lists.Own(ghosts);
            });
        return selected;
    }

    public ValueList<Ghost> At(Cell cell)
    {
        Fresh();
        if (!_at.TryGetValue(cell, out var ghosts))
            _at[cell] = ghosts = Visible(board.Index.InCell(cell));
        return ghosts;
    }

    public ValueList<Ghost> On(StructureRef structure)
    {
        Fresh();
        if (!_on.TryGetValue(structure, out var ghosts))
            _on[structure] = ghosts = Visible(board.Index.OnStructure(structure));
        return ghosts;
    }

    public IGhostBoard As(Actor viewer) => board.ViewFor(viewer);

    public ValueList<GroupCount> Groups
    {
        get
        {
            Fresh();
            return _groupCounts ??= CountGroups();
        }
    }

    private ValueList<GroupCount> CountGroups()
    {
        var groups = board.Index.Groups;
        if (groups.Count == 0)
            return ValueList<GroupCount>.Empty;
        var names = new List<string>(groups.Count);
        var counts = new List<GroupCount>(groups.Count);
        foreach (var (name, ids) in groups)
        {
            var seen = 0;
            foreach (var id in ids)
                if (board.Policies.MaySee(Viewer, board.Index.Ghosts[id].Ghost))
                    seen++;
            if (seen == 0)
                continue;
            names.Add(name.Value);
            counts.Add(new GroupCount(name, seen));
        }

        var keys = names.ToArray();
        var ordered = counts.ToArray();
        Array.Sort(keys, ordered, StringComparer.Ordinal);
        return Lists.Own(ordered);
    }

    private ValueList<Ghost> Visible(IReadOnlyCollection<GhostId> ids)
    {
        if (ids.Count == 0)
            return ValueList<Ghost>.Empty;
        var keys = new List<long>(ids.Count);
        var ghosts = new List<Ghost>(ids.Count);
        foreach (var id in ids)
        {
            var ghost = board.Index.Ghosts[id].Ghost;
            if (board.Policies.MaySee(Viewer, ghost))
            {
                keys.Add(id.Value);
                ghosts.Add(ghost);
            }
        }

        var keyArray = keys.ToArray();
        var ghostArray = ghosts.ToArray();
        Array.Sort(keyArray, ghostArray);
        return Lists.Own(ghostArray);
    }

    private void Fresh()
    {
        var selectors = board.Selectors?.Revision ?? 0;
        if (_epoch == board.Epoch && _policyEpoch == board.Policies.Epoch && _selectorsRevision == selectors)
            return;
        _at.Clear();
        _on.Clear();
        _select.Clear();
        _groupCounts = null;
        _epoch = board.Epoch;
        _policyEpoch = board.Policies.Epoch;
        _selectorsRevision = selectors;
    }
}

/// <summary>
/// The plan-level answers the board holds: the active plans an actor may see and the default plan. Facts and state are
/// the plan-state module's (<see cref="GhostBoard.InstallPlanStates"/>), finished plans and save points the saving modules': refused
/// <see cref="ReasonCodes.ModuleMissing"/> until they are installed. Cached like <see cref="BoardView"/>.
/// </summary>
internal sealed class PlansView(GhostBoard board, Actor viewer) : IPlans
{
    private ValueList<Plan> _all = ValueList<Plan>.Empty;
    private int _epoch = -1;
    private int _policyEpoch = -1;

    public ValueList<Plan> All
    {
        get
        {
            if (_epoch == board.Epoch && _policyEpoch == board.Policies.Epoch)
                return _all;
            var keys = new List<long>(board.Index.Plans.Count);
            var plans = new List<Plan>(board.Index.Plans.Count);
            foreach (var entry in board.Index.Plans.Values)
                if (board.Policies.MaySee(viewer, entry.Plan))
                {
                    keys.Add(entry.Plan.Id.Value);
                    plans.Add(entry.Plan);
                }

            var planArray = plans.ToArray();
            Array.Sort(keys.ToArray(), planArray);
            (_all, _epoch, _policyEpoch) = (Lists.Own(planArray), board.Epoch, board.Policies.Epoch);
            return _all;
        }
    }

    public PlanId Default => GhostBoard.DefaultPlan;

    public Option<Plan> Find(PlanId plan) =>
        board.Index.Plans.TryGetValue(plan, out var entry) && board.Policies.MaySee(viewer, entry.Plan)
            ? Option.Some(entry.Plan)
            : Option<Plan>.None;

    /// <summary>The plan-state module's facts for a plan the viewer may see; refused not-found for any other, module-missing until it is installed.</summary>
    public Result<Answer<PlanFacts>, Reason> Facts(PlanId plan) =>
        board.PlanStates is not { } states ? Result<Answer<PlanFacts>, Reason>.Error(Missing("plan-state"))
        : Find(plan).IsSome ? states.Facts(plan)
        : Result<Answer<PlanFacts>, Reason>.Error(NotFound(plan));

    /// <summary>The plan-state module's state for a plan the viewer may see; refused as <see cref="Facts"/> is.</summary>
    public Result<Answer<PlanState>, Reason> State(PlanId plan) =>
        board.PlanStates is not { } states ? Result<Answer<PlanState>, Reason>.Error(Missing("plan-state"))
        : Find(plan).IsSome ? states.State(plan)
        : Result<Answer<PlanState>, Reason>.Error(NotFound(plan));

    /// <summary>Saving's finished-plans index; empty until saving is installed.</summary>
    public ValueList<FinishedPlan> Finished => board.Saved?.Finished ?? ValueList<FinishedPlan>.Empty;

    /// <summary>Saving's count for the plan; module-missing until saving is installed.</summary>
    public Result<int, Reason> AheadOfSave(PlanId plan) =>
        board.Saved is { } saved ? saved.AheadOfSave(plan) : Result<int, Reason>.Error(Missing("saving"));

    /// <summary>Saving's report of the last load; none until saving is installed.</summary>
    public Option<LoadReport> LastLoad => board.Saved is { } saved ? saved.LastLoad : Option<LoadReport>.None;

    /// <summary>
    /// An active plan the viewer may see, as a template of the ghosts in it the viewer may see, by id
    /// (<see cref="Transforms.TemplateMaker"/>); linear in the plan's ghosts. Any other plan is read as a finished plan
    /// (<see cref="FinishedPlans.FinishedPlanFiles"/>): a template of the pieces it built, in the order it built them;
    /// linear in its file's lines. Refused as that read is (not-found for a plan the viewer may not see).
    /// </summary>
    /// <inheritdoc />
    public string WriteTemplate(Template template) => Lattice.Core.Written.TemplateText.Write(template);

    /// <inheritdoc />
    public Result<Template, Reason> ReadTemplate(string text) => Lattice.Core.Written.TemplateText.Read(text);

    public Result<Template, Reason> AsTemplate(PlanId plan, TemplateName name)
    {
        if (!board.Index.Plans.TryGetValue(plan, out var entry))
            return FinishedPlans.FinishedPlanFiles.Read(board, plan, viewer).Bind(contents => FinishedTemplate(contents, name));
        if (!board.Policies.MaySee(viewer, entry.Plan))
            return Result<Template, Reason>.Error(NotFound(plan));
        var ids = new long[entry.Ghosts.Count];
        var at = 0;
        foreach (var id in entry.Ghosts)
            ids[at++] = id.Value;
        Array.Sort(ids);
        var ghosts = new List<Ghost>(ids.Length);
        foreach (var id in ids)
            if (board.Index.Ghosts[new GhostId(id)].Ghost is var ghost && board.Policies.MaySee(viewer, ghost))
                ghosts.Add(ghost);
        return Transforms.TemplateMaker.Of(name, ghosts);
    }

    private static Result<Template, Reason> FinishedTemplate(Saving.FinishedContents contents, TemplateName name)
    {
        var ghosts = new List<Ghost>(contents.Built.Count);
        foreach (var piece in contents.Built)
            ghosts.Add(piece.Ghost);
        return Transforms.TemplateMaker.Of(name, ghosts);
    }

    private static Reason NotFound(PlanId plan) =>
        Reason.Of(ReasonCodes.NotFound, LatticeIdentity.Board, ValueMap.Of("plan", plan.Value.ToString()));

    private static Reason Missing(string module) =>
        Reason.Of(ReasonCodes.ModuleMissing, LatticeIdentity.Board, ValueMap.Of("module", module));
}
