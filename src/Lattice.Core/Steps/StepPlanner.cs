using System.Collections.Generic;
using System.Globalization;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Ports;

namespace Lattice.Core.Steps;

/// <summary>
/// The exact game steps from the world as it is to one ghost's target, from the catalogue's per-piece build states
/// only, so modded pieces, tools and materials work unchanged. Every build goes to its last step.
/// <para>
/// A build places the piece's first kit (its state 0 material is what the kit uses), then advances one step per later
/// build state with that state's entry tool, or the material in hand when the state names none; a started ghost
/// continues from the state its structure is at. Steps on a structure not yet placed name the step that places it
/// (<see cref="StepTarget.PlacedBy"/>). A removal takes each built state back, last first, with its exit tool; each gives back that state's
/// material (<c>ToolUse.Deconstruct</c>; state 0 gives back the kit, <c>Structure.StructureDestroyed</c>). A swap is one
/// merge when the new piece's kit places the old piece too, both pieces merge (<see cref="PieceFacts.Merge"/>) and the
/// new piece's ends cover the old one's (<c>MultiMergeConstructor.Construct</c> joins both pieces' ends and charges the
/// kit cost difference); otherwise the old piece is taken apart and the new one built. A finish paints, names and sets
/// whatever the structure does not have yet, after its last build step.
/// </para>
/// Constraints come from the build order: <see cref="Constraint.After"/> for each ghost the ghost waits for, then
/// <see cref="Constraint.OnePipeJoinPerTick"/> when it joins pipe networks. Reads the board, the world mirror and the
/// catalogue; never the game. Cost: linear in the ghost's steps. Main thread.
/// </summary>
internal sealed class StepPlanner(GhostBoard board, WorldMirror mirror, ICatalogue catalogue, Shapes shapes, IOrderFacts order) : ISteps
{
    public Result<StepPlan, Reason> For(GhostId ghost) =>
        board.ViewFor(LatticeIdentity.Actor).Find(ghost).TryGet(out var found)
            ? For(found)
            : Result<StepPlan, Reason>.Error(Reason.Of(ReasonCodes.NotFound, StepCodes.Steps, ValueMap.Of("ghost", Text(ghost.Value))));

    /// <summary>The steps for a ghost on the board; refused for a stale ghost with the reason it is stale.</summary>
    public Result<StepPlan, Reason> For(Ghost ghost)
    {
        if (ghost.State is GhostState.Stale stale)
            return Result<StepPlan, Reason>.Error(stale.Why);

        var steps = new List<Step>();
        var refused = ghost.Intent switch
        {
            Intent.Build build => Build(build.Target, build.Finish, ghost.State, steps),
            Intent.Remove remove => Remove(remove.Target, steps),
            Intent.Swap swap => Swap(swap, ghost.State, steps),
            Intent.FinishOnly finish => FinishOnly(finish, steps),
            _ => Option.Some(Refuse(ghost.Intent)),
        };
        return refused.TryGet(out var why)
            ? Result<StepPlan, Reason>.Error(why)
            : Result<StepPlan, Reason>.Ok(new StepPlan(ghost.Id, Lists.Own(steps.ToArray()), Constraints(ghost.Id)));
    }

    /// <summary>The refusal for an intent kind this version does not know.</summary>
    internal static Reason Refuse(Intent intent) =>
        Reason.Of(ReasonCodes.UnknownKind, StepCodes.Steps, ValueMap.Of("type", intent.GetType().Name));

    private Option<Reason> Build(Placement target, Option<Finish> finish, GhostState state, List<Step> steps)
    {
        if (!catalogue.Piece(target.Piece).TryGet(out var piece))
            return Option.Some(catalogue.Piece(target.Piece).TryGetError(out var why) ? why : NoPiece(target.Piece));

        if (state is GhostState.Started started && mirror.Find(started.PartBuilt).TryGet(out var partBuilt))
        {
            var next = partBuilt.State switch
            {
                BuildState.Building building => building.At.Value + 1,
                BuildState.Built => piece.Steps.Count,
                _ => -1,
            };
            if (next < 0)
                return Option.Some(Reason.Of(ReasonCodes.Different, StepCodes.Steps, ValueMap.Of("problem", "broken")));
            var standing = new StepTarget.Standing(partBuilt.Ref);
            return Advance(piece, standing, next, steps).TryGet(out var stuck)
                ? Option.Some(stuck)
                : Finishing(standing, finish, Option.Some(partBuilt), steps);
        }

        if (piece.Kits.Count == 0)
            return Option.Some(NoPiece(target.Piece));
        var place = piece.Kits[0];
        var uses = piece.Steps.Count > 0 && piece.Steps[0].Material.TryGet(out var material)
            ? material
            : Quantity(new ItemKey(place.Kit.Hash), 1);
        var placed = new StepTarget.PlacedBy(steps.Count);
        steps.Add(new Step.PlaceKit(place.Kit, place.Option, uses, target));
        return Advance(piece, placed, 1, steps).TryGet(out var cannot)
            ? Option.Some(cannot)
            : Finishing(placed, finish, Option<StructureInfo>.None, steps);
    }

    private Option<Reason> Advance(PieceFacts piece, StepTarget on, int from, List<Step> steps)
    {
        for (var i = from; i < piece.Steps.Count; i++)
        {
            var step = piece.Steps[i];
            if (!step.EntryTool.TryGet(out var tool))
            {
                if (!step.Material.TryGet(out var inHand))
                    return Option.Some(NoTool(piece.Key, "build", Option.Some(i)));
                tool = inHand.Item;
            }

            steps.Add(new Step.Advance(on, tool, step.Material));
        }

        return Option<Reason>.None;
    }

    private Option<Reason> Remove(StructureRef target, List<Step> steps)
    {
        if (!mirror.Find(target).TryGet(out var standing))
            return Option<Reason>.None;
        if (!catalogue.Piece(standing.Ref.Piece).TryGet(out var piece))
            return Option.Some(catalogue.Piece(standing.Ref.Piece).TryGetError(out var why) ? why : NoPiece(standing.Ref.Piece));

        var top = standing.State switch
        {
            BuildState.Building building => building.At.Value,
            BuildState.Built => piece.Steps.Count - 1,
            _ => -1,
        };
        if (top < 0)
        {
            if (catalogue.BrokenTools.Count == 0)
                return Option.Some(NoTool(piece.Key, "broken", Option<int>.None));
            steps.Add(new Step.TakeBack(standing.Ref, catalogue.BrokenTools[0], ValueList<ItemQuantity>.Empty));
            return Option<Reason>.None;
        }

        for (var i = top; i >= 0; i--)
        {
            var step = piece.Steps[i];
            if (!step.ExitTool.TryGet(out var tool))
                return Option.Some(NoTool(piece.Key, "take-apart", Option.Some(i)));
            steps.Add(new Step.TakeBack(standing.Ref, tool, step.Material.TryGet(out var back) ? ValueList.Of(back) : ValueList<ItemQuantity>.Empty));
        }

        return Option<Reason>.None;
    }

    private Option<Reason> Swap(Intent.Swap swap, GhostState state, List<Step> steps)
    {
        if (state is GhostState.Started || !mirror.Find(swap.Old).TryGet(out var old))
            return Build(swap.New, swap.Finish, state, steps);
        if (Merge(old, swap.New) is { } merge)
        {
            var merged = new StepTarget.PlacedBy(steps.Count);
            steps.Add(merge);
            if (!catalogue.Piece(swap.New.Piece).TryGet(out var piece))
                return Option.Some(NoPiece(swap.New.Piece));
            return Advance(piece, merged, 1, steps).TryGet(out var cannot)
                ? Option.Some(cannot)
                : Finishing(merged, swap.Finish, Option<StructureInfo>.None, steps);
        }

        return Remove(old.Ref, steps).TryGet(out var why)
            ? Option.Some(why)
            : Build(swap.New, swap.Finish, new GhostState.Planned(), steps);
    }

    /// <summary>The one merge that turns the standing piece into the new one, or null when the game would not merge them.</summary>
    private Step.Merge? Merge(StructureInfo old, Placement next)
    {
        if (old.State is not BuildState.Built || old.Ref.At != next.At)
            return null;
        if (!catalogue.Piece(old.Ref.Piece).TryGet(out var was) || !catalogue.Piece(next.Piece).TryGet(out var becomes))
            return null;
        if (!was.Merge.IsSome || !becomes.Merge.TryGet(out var merging) || becomes.Kits.Count == 0 || !InKit(was, becomes.Kits[0].Kit))
            return null;
        if (!shapes.Of(new Placement(old.Ref.Piece, old.Ref.At, old.Turn)).TryGet(out var before) || !shapes.Of(next).TryGet(out var after))
            return null;
        foreach (var end in before.Ends)
            if (!HasEnd(after, end))
                return null;

        var extra = Option<ItemQuantity>.None;
        if (becomes.Steps.Count > 0 && becomes.Steps[0].Material.TryGet(out var cost))
        {
            var paid = was.Steps.Count > 0 && was.Steps[0].Material.TryGet(out var oldCost) ? oldCost.Count : 0;
            if (cost.Count > paid)
                extra = Option.Some(Quantity(cost.Item, cost.Count - paid));
        }

        return new Step.Merge(old.Ref, next.Piece, merging.OffhandTool, extra);
    }

    private static bool InKit(PieceFacts piece, KitKey kit)
    {
        foreach (var place in piece.Kits)
            if (place.Kit == kit)
                return true;
        return false;
    }

    private static bool HasEnd(PlacedShape shape, WorldEnd end)
    {
        foreach (var other in shape.Ends)
            if (other.Cell == end.Cell && other.Facing == end.Facing)
                return true;
        return false;
    }

    private Option<Reason> FinishOnly(Intent.FinishOnly finish, List<Step> steps)
    {
        if (!mirror.Find(finish.Target).TryGet(out var standing))
            return Option.Some(Reason.Of(ReasonCodes.TargetGone, StepCodes.Steps, ValueMap.Of("structure", Text(finish.Target.Id.Value))));
        return Finishing(new StepTarget.Standing(standing.Ref), Option.Some(finish.Finish), Option.Some(standing), steps);
    }

    private static Option<Reason> Finishing(StepTarget on, Option<Finish> finish, Option<StructureInfo> standing, List<Step> steps)
    {
        if (!finish.TryGet(out var wanted))
            return Option<Reason>.None;
        var has = standing.TryGet(out var info) ? info : null;
        if (wanted.Colour.TryGet(out var colour) && (has is null || has.Colour != wanted.Colour))
            steps.Add(new Step.Paint(on, colour));
        if (wanted.Name.TryGet(out var name) && (has is null || has.Name != wanted.Name))
            steps.Add(new Step.Rename(on, name));
        if (wanted.Setting.TryGet(out var setting) && (has is null || has.Setting != wanted.Setting))
            steps.Add(new Step.SetValue(on, setting));
        return Option<Reason>.None;
    }

    private ValueList<Constraint> Constraints(GhostId ghost)
    {
        var waits = order.WaitsFor(ghost);
        var joins = order.JoinsPipes(ghost);
        if (waits.Count == 0 && !joins)
            return ValueList<Constraint>.Empty;
        var constraints = new Constraint[waits.Count + (joins ? 1 : 0)];
        for (var i = 0; i < waits.Count; i++)
            constraints[i] = new Constraint.After(waits[i]);
        if (joins)
            constraints[waits.Count] = new Constraint.OnePipeJoinPerTick();
        return Lists.Own(constraints);
    }

    private static ItemQuantity Quantity(ItemKey item, int count) =>
        ItemQuantity.Create(item, count).Match(static quantity => quantity, static why => throw new System.InvalidOperationException(why.ToString()));

    private static Reason NoPiece(PrefabKey piece) =>
        Reason.Of(ReasonCodes.UnknownPiece, StepCodes.Steps, ValueMap.Of("piece", Text(piece.Hash)).With("problem", "no-kit"));

    private static Reason NoTool(PrefabKey piece, string way, Option<int> step)
    {
        var facts = ValueMap.Of("piece", Text(piece.Hash)).With("way", way);
        return Reason.Of(ReasonCodes.NoTool, StepCodes.Steps, step.TryGet(out var index) ? facts.With("step", Text(index)) : facts);
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);
}
