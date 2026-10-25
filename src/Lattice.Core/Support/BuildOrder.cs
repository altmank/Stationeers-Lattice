using System.Collections.Generic;
using System.Text;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Consequences;
using Lattice.Core.Ports;

namespace Lattice.Core.Support;

/// <summary>
/// One ghost in a build order: what it puts and takes, and the edges to the ghosts it goes after. A run over a large
/// plan makes one per ghost, and most have no edge of some kind, so an edge list exists once it has an edge.
/// </summary>
internal sealed class OrderItem(Ghost ghost, int index)
{
    private List<int>? _waits;
    private List<int>? _before;
    private List<int>? _next;
    private List<int>? _leaves;

    public Ghost Ghost => ghost;

    public int Index => index;

    public Option<Placement> Put { get; set; }

    public Option<PieceFacts> Facts { get; set; }

    public Option<StructureRef> Take { get; set; }

    /// <summary>The structure its put stands as once built, made once for every index the run builds.</summary>
    public StructureInfo? Assumed { get; set; }

    /// <summary>The items it waits for (a finding names them).</summary>
    public IReadOnlyList<int> Waits => _waits ?? (IReadOnlyList<int>)System.Array.Empty<int>();

    /// <summary>The items it goes after with no wait (order only).</summary>
    public IReadOnlyList<int> Before => _before ?? (IReadOnlyList<int>)System.Array.Empty<int>();

    /// <summary>The items that go after it.</summary>
    public IReadOnlyList<int> Next => _next ?? (IReadOnlyList<int>)System.Array.Empty<int>();

    /// <summary>The items whose support it takes away (it warns).</summary>
    public IReadOnlyList<int> Leaves => _leaves ?? (IReadOnlyList<int>)System.Array.Empty<int>();

    /// <summary>Its waits, then its order-only edges: the items it goes after.</summary>
    public int Targets => Waits.Count + Before.Count;

    /// <summary>One of the items it goes after, waits first (<paramref name="edge"/> below <see cref="Targets"/>).</summary>
    public int Target(int edge) => edge < Waits.Count ? Waits[edge] : Before[edge - Waits.Count];

    /// <summary>Adds an edge to the item it goes after; false when it has that edge already.</summary>
    public bool GoesAfter(int to, bool wait)
    {
        ref var list = ref wait ? ref _waits : ref _before;
        if (list is not null && list.Contains(to))
            return false;
        (list ??= new List<int>(2)).Add(to);
        return true;
    }

    public void Precedes(int from) => (_next ??= new List<int>(2)).Add(from);

    public void TakesSupportOf(int index)
    {
        if (_leaves is null || !_leaves.Contains(index))
            (_leaves ??= new List<int>(1)).Add(index);
    }

    public bool InCycle { get; set; }

    /// <summary>Its stage, or -1 in a cycle.</summary>
    public int Stage { get; set; } = -1;
}

/// <summary>What one ghost's stage judgement says: support, the other reasons it fails or warns, and whether it is complete.</summary>
/// <param name="Support">Support's answer, for a ghost that puts a piece.</param>
/// <param name="Fails">Reasons it will fail besides support (the game's check on the built world, rules).</param>
/// <param name="Warnings">Warnings besides support's (rules).</param>
/// <param name="Complete">False while a main-thread answer it needs is missing: no findings are made from it.</param>
internal sealed record GhostVerdict(Option<SupportAnswer> Support, ValueList<Reason> Fails, ValueList<Reason> Warnings, bool Complete);

/// <summary>A ghost to judge on the view of its stage.</summary>
/// <param name="Item">The ghost in the order.</param>
/// <param name="Staged">The changes of every stage.</param>
internal sealed record StageCase(OrderItem Item, AssumedIndex Staged)
{
    public int Stage => Item.Stage;
}

/// <summary>Judges ghosts for a build order: support on the all-builds view (what waits on what), then each ghost on its stage's view.</summary>
internal interface IGhostJudge
{
    SupportAnswer Edge(OrderItem item, Placement put, PieceFacts piece, AssumedIndex all);

    GhostVerdict Stage(StageCase @case);
}

/// <summary>A build order before its ghosts are judged on their stages: the items, cycles, stages and pipe joins.</summary>
internal sealed class Ordering(List<OrderItem> items, List<Finding> early, AssumedIndex all, AssumedIndex staged)
{
    public List<OrderItem> Items => items;

    /// <summary>Findings made before ordering (an unknown kind or piece).</summary>
    public List<Finding> Early => early;

    public AssumedIndex All => all;

    public AssumedIndex Staged => staged;

    public List<List<int>> Cycles { get; } = [];

    public List<List<int>> Stages { get; } = [];

    public List<GhostId> Joins { get; } = [];

    /// <summary>What ordering read through views (pipe networks), and what it asked for.</summary>
    public ReadLog Reads { get; } = new();
}

/// <summary>
/// One build order across every plan (a ghost resting on another plan's unbuilt ghost
/// waits for it), judged in that order (each ghost against the world plus the ghosts
/// ordered before it).
/// <para>
/// What orders ghosts: a ghost held up by another ghost's structure waits for it; a build in a spot a removal or swap
/// of another ghost clears waits for that removal. Both are waits a finding names. A ghost held up by a structure
/// another ghost removes goes before that removal, which warns that it leaves the ghost unsupported (build safety
/// warns, never blocks); that is order only. Ghosts that wait round a loop all fail with <see cref="ReasonCodes.Cycle"/>.
/// </para>
/// <para>
/// Ghosts go out in stages: a stage holds the ghosts whose waits are all in earlier stages, at most one of them a
/// pipe-network join; a second join moves to a later stage, an order with no wait in the findings.
/// </para>
/// Two phases: <see cref="Order"/> (edges from support on the all-builds view, cycles, stages) and <see cref="Report"/>
/// (each ghost on its stage's view, findings). Judging goes through an <see cref="IGhostJudge"/>, which may answer from
/// a cache. Stale ghosts are left out. Cost: linear in ghosts and their edges plus the judgements; heavy work, so it
/// runs on Lattice's worker. Not thread-safe per instance.
/// </summary>
internal sealed class OrderRun
{
    private readonly ICatalogue _catalogue;
    private readonly IJudgeFacts _facts;
    private readonly Shapes _shapes;
    private readonly WorldModels _models;

    public OrderRun(ICatalogue catalogue, IGrid grid, IJudgeFacts facts)
    {
        _catalogue = catalogue;
        _facts = facts;
        _shapes = new Shapes(catalogue, grid);
        _models = new WorldModels(new FactsWorld(facts), catalogue, grid);
    }

    public Ordering Order(IReadOnlyList<Ghost> ghosts, IGhostJudge judge)
    {
        var early = new List<Finding>();
        var items = Items(ghosts, early);
        var all = new AssumedIndex(items.Count);
        foreach (var item in items)
        {
            if (item.Put.TryGet(out var put))
                all.Put(item.Ghost.Id, 0, put, item.Assumed ??= SupportWorld.AssumedInfo(item.Ghost.Id, put));
            if (item.Take.TryGet(out var take))
                all.Take(item.Ghost.Id, 0, take);
        }

        var ordering = new Ordering(items, early, all, new AssumedIndex(items.Count));
        Edges(ordering, judge);
        Cycles(ordering);
        Walk(ordering);
        return ordering;
    }

    /// <summary>Judges every staged ghost on its stage's view, in build order; complete is false when any judgement is.</summary>
    public OrderReport Report(Ordering ordering, IGhostJudge judge, out bool complete)
    {
        complete = true;
        var items = ordering.Items;
        var findings = new List<Finding>(items.Count + ordering.Early.Count);
        findings.AddRange(ordering.Early);
        var stages = new List<ValueList<GhostId>>(ordering.Stages.Count);
        foreach (var stage in ordering.Stages)
        {
            var ids = new GhostId[stage.Count];
            for (var at = 0; at < stage.Count; at++)
            {
                var item = items[stage[at]];
                ids[at] = item.Ghost.Id;
                var verdict = judge.Stage(new StageCase(item, ordering.Staged));
                if (!verdict.Complete)
                {
                    complete = false;
                    continue;
                }

                Findings(items, item, verdict, findings);
            }

            stages.Add(Board.Lists.Own(ids));
        }

        foreach (var cycle in ordering.Cycles)
        {
            var reason = CycleReason(items, cycle);
            foreach (var index in cycle)
                findings.Add(new Finding.WillFail(items[index].Ghost.Id, reason));
        }

        return new OrderReport(ValueList.From(stages), ValueList.From(findings), ValueSet.From(ordering.Joins));
    }

    private List<OrderItem> Items(IReadOnlyList<Ghost> ghosts, List<Finding> findings)
    {
        var items = new List<OrderItem>(ghosts.Count);
        foreach (var ghost in ghosts)
        {
            if (ghost.State is GhostState.Stale)
                continue;
            var item = new OrderItem(ghost, items.Count);
            switch (ghost.Intent)
            {
                case Intent.Build build:
                    item.Put = Option.Some(build.Target);
                    break;
                case Intent.Swap swap:
                    item.Put = Option.Some(swap.New);
                    item.Take = Option.Some(swap.Old);
                    break;
                case Intent.Remove remove:
                    item.Take = Option.Some(remove.Target);
                    break;
                case Intent.FinishOnly:
                    break;
                default:
                    findings.Add(new Finding.WillFail(ghost.Id, Reason.Of(ReasonCodes.UnknownKind, SupportCodes.Order)));
                    continue;
            }

            if (item.Put.TryGet(out var put))
            {
                var facts = _catalogue.Piece(put.Piece);
                if (!facts.TryGet(out var piece))
                {
                    findings.Add(new Finding.WillFail(ghost.Id, facts.Match(_ => Reason.Of(ReasonCodes.UnknownPiece, SupportCodes.Order), error => error)));
                    continue;
                }

                item.Facts = Option.Some(piece);
            }

            items.Add(item);
        }

        return items;
    }

    // Waits and order-only edges, judged on the world with every build assumed and nothing removed yet.
    private void Edges(Ordering ordering, IGhostJudge judge)
    {
        var items = ordering.Items;
        var all = ordering.All;
        var byGhost = new Dictionary<GhostId, int>(items.Count);
        var removers = new Dictionary<ReferenceId, int>();
        var clearing = new Dictionary<SpotKey, List<int>>();
        var linked = new List<int>(4);
        foreach (var item in items)
        {
            byGhost[item.Ghost.Id] = item.Index;
            if (!item.Take.TryGet(out var take))
                continue;
            removers[take.Id] = item.Index;
            foreach (var spot in SpotsOf(take))
            {
                if (!clearing.TryGetValue(spot, out var here))
                    clearing.Add(spot, here = new List<int>(1));
                here.Add(item.Index);
            }
        }

        var turnsOn = new ValueList<GhostId>[items.Count];
        foreach (var item in items)
        {
            turnsOn[item.Index] = ValueList<GhostId>.Empty;
            if (!item.Put.TryGet(out var put) || !item.Facts.TryGet(out var piece))
                continue;
            var answer = judge.Edge(item, put, piece, all);
            turnsOn[item.Index] = answer.TurnsOn;
            if (answer.Outcome is SupportOutcome.Holds holds)
                foreach (var structure in holds.By)
                {
                    if (all.TryPut(structure.Id, out var supporter) && byGhost.TryGetValue(supporter.Ghost, out var waited))
                        Link(items, item.Index, waited, wait: true);
                    else if (removers.TryGetValue(structure.Id, out var remover) && remover != item.Index)
                    {
                        Link(items, remover, item.Index, wait: false);
                        items[remover].TakesSupportOf(item.Index);
                    }
                }

            if (clearing.Count > 0 && _shapes.Of(put).TryGet(out var mine))
            {
                linked.Clear();
                foreach (var spot in mine.Spots)
                    if (clearing.TryGetValue(spot, out var removing))
                        foreach (var other in removing)
                            if (other != item.Index && !linked.Contains(other))
                                linked.Add(other);
                linked.Sort();
                foreach (var other in linked)
                    Link(items, item.Index, other, wait: true);
            }
        }

        // A ghost the game's check answers for goes after the ghosts its answer turns on, so they are built (and the
        // game sees them) first; unless one of them already goes after it (directly or through others), where that
        // order stands: a ground frame does not wait for the light that will hang on the frame above it.
        HashSet<int>? seen = null;
        Stack<int>? open = null;
        for (var index = items.Count - 1; index >= 0; index--)
            foreach (var ghost in turnsOn[index])
                if (byGhost.TryGetValue(ghost, out var other) && !GoesAfter(items, other, index, seen ??= [], open ??= new Stack<int>()))
                    Link(items, index, other, wait: true);
    }

    /// <summary>How many ghosts a turns-on check follows before it gives up and adds no edge (a bound, not a game rule).</summary>
    public const int TurnsOnReach = 256;

    // Whether `from` goes after `to` through waits and order edges; past the reach, taken as yes (no edge is added).
    // The set and stack are the caller's, reused across calls.
    private static bool GoesAfter(List<OrderItem> items, int from, int to, HashSet<int> seen, Stack<int> open)
    {
        seen.Clear();
        open.Clear();
        seen.Add(from);
        open.Push(from);
        while (open.Count > 0)
        {
            var item = items[open.Pop()];
            for (var edge = 0; edge < item.Targets; edge++)
            {
                var next = item.Target(edge);
                if (next == to)
                    return true;
                if (seen.Add(next))
                {
                    if (seen.Count > TurnsOnReach)
                        return true;
                    open.Push(next);
                }
            }
        }

        return false;
    }

    // The spots a structure a removal or swap clears takes, by its footprint as it stands.
    private IReadOnlyList<SpotKey> SpotsOf(StructureRef cleared) =>
        _facts.Structure(cleared.Id).TryGet(out var standing) &&
        _shapes.Of(new Placement(cleared.Piece, cleared.At, standing.Turn)).TryGet(out var shape)
            ? shape.Spots
            : [];

    private static void Link(List<OrderItem> items, int from, int to, bool wait)
    {
        if (from != to && items[from].GoesAfter(to, wait))
            items[to].Precedes(from);
    }

    // Tarjan's strongly connected components over waits and order edges, without recursion; a component of two or
    // more ghosts, or one that waits for itself, is a cycle.
    private static void Cycles(Ordering ordering)
    {
        var items = ordering.Items;
        var count = items.Count;
        var index = new int[count];
        var low = new int[count];
        var onStack = new bool[count];
        for (var i = 0; i < count; i++)
            index[i] = -1;
        var stack = new Stack<int>();
        var next = 0;
        var frames = new Stack<(int Node, int Edge)>();
        var component = new List<int>();
        for (var root = 0; root < count; root++)
        {
            if (index[root] >= 0)
                continue;
            frames.Push((root, 0));
            while (frames.Count > 0)
            {
                var (node, edge) = frames.Pop();
                if (edge == 0)
                {
                    index[node] = low[node] = next++;
                    stack.Push(node);
                    onStack[node] = true;
                }

                var targets = items[node];
                if (edge < targets.Targets)
                {
                    frames.Push((node, edge + 1));
                    var to = targets.Target(edge);
                    if (index[to] < 0)
                        frames.Push((to, 0));
                    else if (onStack[to])
                        low[node] = System.Math.Min(low[node], index[to]);
                    continue;
                }

                if (low[node] == index[node])
                {
                    component.Clear();
                    int member;
                    do
                    {
                        member = stack.Pop();
                        onStack[member] = false;
                        component.Add(member);
                    }
                    while (member != node);
                    if (component.Count > 1)
                    {
                        component.Sort();
                        foreach (var m in component)
                            items[m].InCycle = true;
                        ordering.Cycles.Add(new List<int>(component));
                    }
                }

                if (frames.Count > 0)
                {
                    var parent = frames.Peek().Node;
                    low[parent] = System.Math.Min(low[parent], low[node]);
                }
            }
        }
    }

    private void Walk(Ordering ordering)
    {
        var items = ordering.Items;
        var staged = ordering.Staged;
        var remaining = new int[items.Count];
        var ready = new List<int>();
        foreach (var item in items)
        {
            if (item.InCycle)
                continue;
            for (var edge = 0; edge < item.Targets; edge++)
                if (!items[item.Target(edge)].InCycle)
                    remaining[item.Index]++;
            if (remaining[item.Index] == 0)
                ready.Add(item.Index);
        }

        var networks = new PipeNetworks(_catalogue, _models);
        while (ready.Count > 0)
        {
            var number = ordering.Stages.Count;
            var world = new SupportWorld(staged, number, true, _facts, _catalogue, ordering.Reads);
            ready.Sort((a, b) => items[a].Ghost.Id.Value.CompareTo(items[b].Ghost.Id.Value));
            var stage = new List<int>(ready.Count);
            var later = new List<int>();
            var joined = false;
            networks.StartStage(number);
            foreach (var index in ready)
            {
                var item = items[index];
                if (item.Put.TryGet(out var put) && item.Facts.TryGet(out var piece))
                {
                    var isJoin = networks.Joins(world, item.Ghost.Id, put, piece);
                    if (isJoin && joined)
                    {
                        later.Add(index);
                        continue;
                    }

                    if (isJoin)
                    {
                        joined = true;
                        ordering.Joins.Add(item.Ghost.Id);
                    }

                    networks.Accept(world, item.Ghost.Id, put, piece);
                }

                item.Stage = number;
                stage.Add(index);
            }

            var newly = new List<int>();
            foreach (var index in stage)
            {
                var item = items[index];
                if (item.Take.TryGet(out var take))
                {
                    staged.Take(item.Ghost.Id, number, take);
                    networks.Changed(take.At, number);
                }

                if (item.Put.TryGet(out var put))
                {
                    staged.Put(item.Ghost.Id, number, put, item.Assumed ??= SupportWorld.AssumedInfo(item.Ghost.Id, put));
                    networks.Changed(put.At, number);
                }

                for (var at = 0; at < item.Next.Count; at++)
                {
                    var waiting = item.Next[at];
                    if (!items[waiting].InCycle && --remaining[waiting] == 0)
                        newly.Add(waiting);
                }
            }

            ordering.Stages.Add(stage);
            later.AddRange(newly);
            ready = later;
        }
    }

    // A ghost's findings: it fails (support's reason first, then the verdict's), else waits, else passes; then its
    // warnings (support's, then the verdict's) and the ghosts it leaves unsupported. Allocates only what it reports.
    private static void Findings(List<OrderItem> items, OrderItem item, GhostVerdict verdict, List<Finding> findings)
    {
        var id = item.Ghost.Id;
        var outcome = verdict.Support.TryGet(out var answer) ? answer.Outcome : null;
        var supportFails = outcome switch
        {
            null or SupportOutcome.Holds or SupportOutcome.Pending => null,
            SupportOutcome.Fails failure => failure.Why,
            _ => Reason.Of(ReasonCodes.UnknownKind, SupportCodes.Order),
        };
        if (supportFails is not null || verdict.Fails.Count > 0)
        {
            if (supportFails is not null)
                findings.Add(new Finding.WillFail(id, supportFails));
            foreach (var why in verdict.Fails)
                findings.Add(new Finding.WillFail(id, why));
        }
        else if (WaitsOf(items, item, outcome as SupportOutcome.Holds) is { Count: > 0 } waits)
            findings.Add(new Finding.WaitsFor(id, waits));
        else
            findings.Add(new Finding.Passes(id));
        if (answer is not null)
            foreach (var warning in answer.Warnings)
                findings.Add(new Finding.Warns(id, warning));
        foreach (var warning in verdict.Warnings)
            findings.Add(new Finding.Warns(id, warning));
        if (item.Leaves.Count > 0)
        {
            var left = new List<GhostId>(item.Leaves.Count);
            for (var at = 0; at < item.Leaves.Count; at++)
                left.Add(items[item.Leaves[at]].Ghost.Id);
            findings.Add(new Finding.Warns(id, Reason.Of(
                ReasonCodes.NeedsSupport,
                SupportCodes.Order,
                ValueMap<string, string>.Of("leaves-unsupported", BuildOrder.Text(left)))));
        }
    }

    // The ghosts it waits for: its wait edges, then the ones support names that are not among them.
    private static ValueList<GhostId> WaitsOf(List<OrderItem> items, OrderItem item, SupportOutcome.Holds? holds)
    {
        var named = holds?.WaitsFor.Count ?? 0;
        if (item.Waits.Count + named == 0)
            return ValueList<GhostId>.Empty;
        var waits = new List<GhostId>(item.Waits.Count + named);
        for (var at = 0; at < item.Waits.Count; at++)
            waits.Add(items[item.Waits[at]].Ghost.Id);
        if (holds is not null)
            foreach (var ghost in holds.WaitsFor)
                if (!waits.Contains(ghost))
                    waits.Add(ghost);
        return ValueList.From(waits);
    }

    private static Reason CycleReason(List<OrderItem> items, List<int> cycle)
    {
        var ghosts = new List<GhostId>(cycle.Count);
        foreach (var index in cycle)
            ghosts.Add(items[index].Ghost.Id);
        return Reason.Of(ReasonCodes.Cycle, SupportCodes.Order, ValueMap<string, string>.Of("ghosts", BuildOrder.Text(ghosts)));
    }
}

/// <summary>
/// Orders and judges a ghost list at once, on a world view read on the calling thread and the game's answers given:
/// support only, no cache. A placement the game's check must answer first makes the outcome pending.
/// </summary>
internal sealed class BuildOrder(IWorldView world, ICatalogue catalogue, IGrid grid, SupportJudge judge, GameChecks checks)
{
    public OrderOutcome Plan(ValueList<Ghost> ghosts)
    {
        var facts = new DirectFacts(world, checks);
        var run = new OrderRun(catalogue, grid, facts);
        var direct = new DirectJudge(judge, facts, catalogue);
        var ordering = run.Order(ghosts, direct);
        var report = run.Report(ordering, direct, out _);
        return direct.Pending.Count > 0
            ? new OrderOutcome.Pending(ValueList.From(direct.Pending))
            : new OrderOutcome.Ready(report);
    }

    /// <summary>Ghost ids in ascending order, comma separated.</summary>
    public static string Text(List<GhostId> ghosts)
    {
        ghosts.Sort((a, b) => a.Value.CompareTo(b.Value));
        var text = new StringBuilder(ghosts.Count * 4);
        for (var i = 0; i < ghosts.Count; i++)
        {
            if (i > 0)
                text.Append(',');
            text.Append(ghosts[i].Value);
        }

        return text.ToString();
    }

    /// <summary>Support alone, judged each time it is asked; the placements the game must answer, in build order.</summary>
    private sealed class DirectJudge(SupportJudge judge, IJudgeFacts facts, ICatalogue catalogue) : IGhostJudge
    {
        public List<Placement> Pending { get; } = [];

        public SupportAnswer Edge(OrderItem item, Placement put, PieceFacts piece, AssumedIndex all) =>
            judge.Judge(new SupportCase(item.Ghost.Id, put, piece, new SupportWorld(all, AssumedIndex.Every, false, facts, catalogue, new ReadLog())), report: false);

        public GhostVerdict Stage(StageCase @case)
        {
            var item = @case.Item;
            if (!item.Put.TryGet(out var put) || !item.Facts.TryGet(out var piece))
                return new GhostVerdict(Option<SupportAnswer>.None, ValueList<Reason>.Empty, ValueList<Reason>.Empty, true);
            var world = new SupportWorld(@case.Staged, @case.Stage, true, facts, catalogue, new ReadLog());
            var answer = judge.Judge(new SupportCase(item.Ghost.Id, put, piece, world));
            if (answer.Outcome is SupportOutcome.Pending ask)
            {
                Pending.Add(ask.Ask);
                return new GhostVerdict(Option.Some(answer), ValueList<Reason>.Empty, ValueList<Reason>.Empty, false);
            }

            judge.Rules.Guard.Settle(item.Ghost.Id);
            return new GhostVerdict(Option.Some(answer), ValueList<Reason>.Empty, ValueList<Reason>.Empty, true);
        }
    }
}
