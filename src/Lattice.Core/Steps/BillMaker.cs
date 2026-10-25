using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;

namespace Lattice.Core.Steps;

/// <summary>
/// Bills for the ghosts a selector picks: the sum of each ghost's steps. Needed is what kit placements, build steps
/// and merges use; given back is what take-apart steps return; net is needed minus given back per item, items whose
/// counts cancel left out; tools are the tools in hand (a build step done with its material in hand adds no tool) and
/// the offhand tools of merges. Items appear in the order the steps first name them. Ghosts whose steps are refused
/// (stale, no kit, no tool) are left out. Cached per selector until the board, its access policies or the world mirror
/// change. Cost: linear in the ghosts' steps on a miss, constant on a hit. Main thread.
/// </summary>
internal sealed class BillMaker(GhostBoard board, StepPlanner planner, WorldMirror mirror) : IBills
{
    private readonly Dictionary<Selector, Bill> _cache = [];
    private (int Board, int Policies, long World) _cachedAt = (-1, -1, -1);

    public Result<Bill, Reason> For(Selector which)
    {
        if (Kept(which).TryGet(out var kept))
            return Result<Bill, Reason>.Ok(kept);
        var selected = board.ViewFor(LatticeIdentity.Actor).Select(which);
        if (!selected.TryGet(out var ghosts))
            return Result<Bill, Reason>.Error(selected.TryGetError(out var why) ? why : Reason.Of(ReasonCodes.UnknownKind, StepCodes.Steps));

        var run = Start(which, ghosts);
        run.Step(int.MaxValue);
        return Result<Bill, Reason>.Ok(run.Bill());
    }

    /// <summary>
    /// The bill for these ghosts (the ones <paramref name="which"/> picks) worked out in parts (<see cref="BillRun"/>),
    /// for the main thread's slices. A finished run is kept for <paramref name="which"/> when the board and the world
    /// are as they were at its start. Constant.
    /// </summary>
    internal BillRun Start(Selector which, ValueList<Ghost> ghosts) => new(this, which, ghosts, Now());

    /// <summary>The bill for these step plans.</summary>
    internal static Bill Of(IReadOnlyList<StepPlan> plans)
    {
        var tally = new BillTally();
        foreach (var plan in plans)
            tally.Add(plan);
        return tally.Bill();
    }

    private Result<StepPlan, Reason> Plan(Ghost ghost) => planner.For(ghost);

    private (int Board, int Policies, long World) Now() => (board.Epoch, board.Policies.Epoch, mirror.Revision);

    private Option<Bill> Kept(Selector which)
    {
        var now = Now();
        if (now != _cachedAt)
        {
            _cache.Clear();
            _cachedAt = now;
        }

        return _cache.TryGetValue(which, out var kept) ? Option.Some(kept) : Option<Bill>.None;
    }

    private void Keep(Selector which, (int, int, long) at, Bill bill)
    {
        if (Kept(which).IsNone && at == _cachedAt)
            _cache[which] = bill;
    }

    /// <summary>One bill worked out a ghost at a time: a unit is one ghost's steps planned and counted. Main thread.</summary>
    internal sealed class BillRun
    {
        private readonly BillMaker _maker;
        private readonly Selector _which;
        private readonly ValueList<Ghost> _ghosts;
        private readonly (int, int, long) _at;
        private readonly BillTally _tally = new();
        private Bill? _bill;
        private int _next;

        internal BillRun(BillMaker maker, Selector which, ValueList<Ghost> ghosts, (int, int, long) at)
        {
            _maker = maker;
            _which = which;
            _ghosts = ghosts;
            _at = at;
        }

        /// <summary>Whether every ghost is counted.</summary>
        public bool Done => _next == _ghosts.Count;

        /// <summary>Works up to <paramref name="units"/> ghosts; true once every ghost is counted.</summary>
        public bool Step(int units)
        {
            for (; units > 0 && !Done; units--)
                if (_maker.Plan(_ghosts[_next++]).TryGet(out var plan))
                    _tally.Add(plan);
            return Done;
        }

        /// <summary>The bill of the ghosts counted so far; all of them once <see cref="Done"/>.</summary>
        public Bill Bill()
        {
            if (_bill is { } made)
                return made;
            var bill = _tally.Bill();
            if (!Done)
                return bill;
            _bill = bill;
            _maker.Keep(_which, _at, bill);
            return bill;
        }
    }

    /// <summary>What step plans need, give back and use, added one plan at a time.</summary>
    private sealed class BillTally
    {
        private readonly Tally _needed = new();
        private readonly Tally _givenBack = new();
        private readonly List<ItemKey> _tools = [];

        public void Add(StepPlan plan)
        {
            foreach (var step in plan.Steps)
                switch (step)
                {
                    case Step.PlaceKit place:
                        _needed.Add(place.Uses);
                        break;
                    case Step.Advance advance:
                        if (advance.Material.TryGet(out var used))
                            _needed.Add(used);
                        if (!advance.Material.TryGet(out var inHand) || inHand.Item != advance.Tool)
                            AddOnce(_tools, advance.Tool);
                        break;
                    case Step.TakeBack back:
                        foreach (var item in back.Returns)
                            _givenBack.Add(item);
                        AddOnce(_tools, back.Tool);
                        break;
                    case Step.Merge merge:
                        if (merge.Extra.TryGet(out var extra))
                            _needed.Add(extra);
                        AddOnce(_tools, merge.OffhandTool);
                        break;
                    default:
                        // Paint, name and setting steps use no item; a kind from a newer minor has no cost Lattice knows.
                        break;
                }
        }

        public Bill Bill() => new(_needed.Quantities(), _givenBack.Quantities(), Net(_needed, _givenBack), ValueSet.From(_tools));
    }

    private static ValueList<ItemDelta> Net(Tally needed, Tally givenBack)
    {
        var net = new List<ItemDelta>(needed.Items.Count + givenBack.Items.Count);
        foreach (var item in needed.Items)
            if (needed.Count(item) != givenBack.Count(item))
                net.Add(new ItemDelta(item, needed.Count(item) - givenBack.Count(item)));
        foreach (var item in givenBack.Items)
            if (!needed.Has(item))
                net.Add(new ItemDelta(item, -givenBack.Count(item)));
        return Lists.Own(net.ToArray());
    }

    private static void AddOnce(List<ItemKey> tools, ItemKey tool)
    {
        if (!tools.Contains(tool))
            tools.Add(tool);
    }

    /// <summary>Counts per item, in the order items were first added.</summary>
    private sealed class Tally
    {
        private readonly Dictionary<ItemKey, int> _counts = [];

        public List<ItemKey> Items { get; } = [];

        public bool Has(ItemKey item) => _counts.ContainsKey(item);

        public int Count(ItemKey item) => _counts.TryGetValue(item, out var count) ? count : 0;

        public void Add(ItemQuantity quantity)
        {
            if (!_counts.ContainsKey(quantity.Item))
                Items.Add(quantity.Item);
            _counts[quantity.Item] = Count(quantity.Item) + quantity.Count;
        }

        public ValueList<ItemQuantity> Quantities()
        {
            var quantities = new ItemQuantity[Items.Count];
            for (var i = 0; i < quantities.Length; i++)
                quantities[i] = ItemQuantity.Create(Items[i], _counts[Items[i]])
                    .Match(static quantity => quantity, static why => throw new System.InvalidOperationException(why.ToString()));
            return Lists.Own(quantities);
        }
    }
}
